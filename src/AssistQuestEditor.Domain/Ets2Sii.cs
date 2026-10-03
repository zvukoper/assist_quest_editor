using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Чтение файлов SCS SII (`.sii`) — формата, которым Euro Truck Simulator 2
/// хранит профили и метаданные сохранений.
///
/// Три состояния файла, и все три встречаются на одной машине:
///   * <c>ScsC</c> — зашифрованная полезная нагрузка (AES-256-CBC + zlib);
///   * <c>BSII</c> — двоичный SII (версии 1/2/3);
///   * <c>SiiN</c> — уже текст.
///
/// Знание формата живёт в домене, а не в форме: это правило ФАЙЛА, а не окна, и
/// вторая его копия в Host разошлась бы с первой при первом же изменении игры.
/// Проверено на реальных файлах профиля: зашифрованный <c>profile.sii</c>
/// (~4,5 КБ) после расшифровки оказывается обычным текстом <c>SiiNunit</c>, а
/// <c>game.sii</c> (~400 КБ) — двоичным <c>BSII</c>.
///
/// Ключ AES — известный ключ SCS из «Savegame Decrypter» (JohnnyGuitar), тот же,
/// что использует <c>ci/scs_sii_codec_lib.ps1</c> в проверках. Держать его здесь
/// обязательно: без него зашифрованные профили не читаются вовсе.
/// </summary>
public static class Ets2Sii
{
    /// <summary>Ключ AES-256 (32 байта) для расшифровки контейнера <c>ScsC</c>.</summary>
    private static readonly byte[] ScsKey =
    {
        0x2A, 0x5F, 0xCB, 0x17, 0x91, 0xD2, 0x2F, 0xB6,
        0x02, 0x45, 0xB3, 0xD8, 0x36, 0x9E, 0xD0, 0xB2,
        0xC2, 0x73, 0x71, 0x56, 0x3F, 0xBF, 0x1F, 0x3C,
        0x9E, 0xDF, 0x6B, 0x11, 0x82, 0x5A, 0x5D, 0x0A
    };

    private static readonly byte[] ScsSignature = "ScsC"u8.ToArray();
    private static readonly byte[] BsiiSignature = "BSII"u8.ToArray();
    private static readonly byte[] SiiSignature = "SiiN"u8.ToArray();

    /// <summary>
    /// Читает файл `.sii` и возвращает текстовый <c>SiiNunit</c>.
    ///
    /// Ошибка не глотается: вызывающий обязан отличать «файла нет» от «файл есть,
    /// но не читается». Молчаливый пропуск сделал бы окно профилей пустым без
    /// объяснения причины.
    /// </summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return DecodeBytes(bytes);
    }

    /// <summary>То же, что <see cref="ReadText"/>, но над уже прочитанными байтами.</summary>
    public static string DecodeBytes(byte[] bytes)
    {
        var payload = bytes;
        if (StartsWith(bytes, ScsSignature))
            payload = DecryptScs(bytes);

        if (StartsWith(payload, BsiiSignature))
            return Ets2BinarySii.Decode(payload);

        if (StartsWith(payload, SiiSignature))
            return Encoding.UTF8.GetString(payload);

        // Файл без подписи — не SII. Считать его текстом нельзя: декодер выдал бы
        // мусор, который потом выглядел бы как «испорченный профиль».
        throw new InvalidDataException("Файл не является SII (нет подписи ScsC/BSII/SiiN).");
    }

    /// <summary>Признак того, что файл начинается с указанной подписи.</summary>
    private static bool StartsWith(byte[] data, byte[] signature)
        => data.Length >= signature.Length && data.AsSpan(0, signature.Length).SequenceEqual(signature);

    /// <summary>
    /// Расшифровывает контейнер <c>ScsC</c>.
    ///
    /// Формат: подпись (4) + HMAC (32) + IV (16) + размер полезной нагрузки (4,
    /// little-endian) + AES-256-CBC(zlib(payload)). HMAC не проверяется — игра
    /// его не проверяет при загрузке профиля, а вычислить его без ключа подписи
    /// нельзя.
    /// </summary>
    private static byte[] DecryptScs(byte[] bytes)
    {
        const int headerSize = 4 + 32 + 16 + 4;
        if (bytes.Length < headerSize)
            throw new InvalidDataException("Файл ScsC короче заголовка.");

        var iv = bytes.AsSpan(36, 16).ToArray();
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(52, 4));
        var cipher = bytes.AsSpan(headerSize).ToArray();

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        // Тело выровнено по границе блока, поэтому добивки нет: PaddingMode.PKCS7
        // съел бы последние байты потока zlib.
        aes.Padding = PaddingMode.None;
        aes.Key = ScsKey;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var compressed = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        // Первые два байта — заголовок zlib, DeflateStream их не принимает.
        using var input = new MemoryStream(compressed, 2, compressed.Length - 2);
        using var zlib = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(declaredSize > 0 ? (int)Math.Min(declaredSize, int.MaxValue) : 0);
        zlib.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// Разобранный текстовый SII: блоки вида <c>имя : id { поле: значение ... }</c>.
///
/// Словарь, а не дерево: у SII нет вложенности блоков, и любое усложнение здесь
/// было бы выдумкой, которую пришлось бы поддерживать.
/// </summary>
public sealed class Ets2SiiDocument
{
    private readonly Dictionary<string, Dictionary<string, List<string>>> _blocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Значения полей БЕЗ индекса — до разведения со счётчиками массивов.
    ///
    /// У SII строка <c>имя: 1</c> перед списком <c>имя[0..N]</c> — это ОБЪЯВЛЕННОЕ
    /// ЧИСЛО ЭЛЕМЕНТОВ, а не первый элемент. Отличить её от обычного скаляра можно
    /// только после разбора, когда видно, были ли у поля элементы, поэтому такие
    /// значения временно копятся здесь.
    ///
    /// Без этого счётчик попадал в список первым элементом: в окне профилей
    /// появлялся мод с именем «1» — ровно там, где игра пишет
    /// <c>active_mods: 1</c> перед единственным подключённым модом.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, List<string>>> _scalars =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Объявленное число элементов массивных полей: <c>active_mods: 1</c> при
    /// объявленном <c>active_mods[0]</c>. Заполняется ПОСЛЕ разбора.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, long>> _arrayCounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Число блоков в документе.</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>Имена блоков в порядке первого появления.</summary>
    public IReadOnlyList<string> BlockNames => _blockNames;
    private readonly List<string> _blockNames = new();

    /// <summary>Есть ли блок с таким именем (например, <c>user_profile</c>).</summary>
    public bool HasBlock(string name) => _blocks.ContainsKey(name);

    /// <summary>Скалярное значение поля или null.</summary>
    public string? GetString(string block, string field)
    {
        if (!_blocks.TryGetValue(block, out var fields) || !fields.TryGetValue(field, out var values))
            return null;

        return values.Count > 0 ? values[0] : null;
    }

    /// <summary>
    /// Все значения поля.
    ///
    /// Для массивов вида <c>active_mods[0..N]</c> это ЭЛЕМЕНТЫ; объявленное число
    /// элементов в список не входит и получается через
    /// <see cref="GetArrayCount"/>. Для обычных полей — единственное значение.
    /// </summary>
    public IReadOnlyList<string> GetValues(string block, string field)
    {
        if (!_blocks.TryGetValue(block, out var fields) || !fields.TryGetValue(field, out var values))
            return Array.Empty<string>();

        return values;
    }

    /// <summary>
    /// Объявленное число элементов массивного поля или <c>null</c>.
    ///
    /// <c>null</c> означает «счётчика в файле нет», а не «ноль элементов»: у
    /// <c>dependencies</c> счётчика не бывает вовсе, и возвращать ноль значило бы
    /// утверждать, что список пуст.
    /// </summary>
    public long? GetArrayCount(string block, string field)
        => _arrayCounts.TryGetValue(block, out var fields) && fields.TryGetValue(field, out var count)
            ? count
            : null;

    /// <summary>Целое значение поля или значение по умолчанию.</summary>
    public long GetInt64(string block, string field, long fallback = 0)
        => long.TryParse(GetString(block, field), out var value) ? value : fallback;

    /// <summary>Дробное значение поля или значение по умолчанию.</summary>
    public double GetDouble(string block, string field, double fallback = 0d)
        => double.TryParse(GetString(block, field), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>Логическое значение поля.</summary>
    public bool GetBool(string block, string field)
        => string.Equals(GetString(block, field), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Разбирает текст <c>SiiNunit</c>.
    ///
    /// Разбор построчный и намеренно простой: SII — не язык со вложенностью, а
    /// поток строк. Строки в кавычках могут содержать экранирование и переводы
    /// строк внутри значения, поэтому кавычки отслеживаются посимвольно.
    /// </summary>
    public static Ets2SiiDocument Parse(string text)
    {
        var document = new Ets2SiiDocument();
        var currentName = (string?)null;
        var currentElements = (Dictionary<string, List<string>>?)null;
        var currentScalars = (Dictionary<string, List<string>>?)null;

        foreach (var rawLine in EnumerateLines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line is "{" or "}")
                continue;

            if (line.StartsWith("SiiNunit", StringComparison.OrdinalIgnoreCase))
                continue;

            if (currentElements is null)
            {
                // Заголовок блока: `имя : идентификатор {` — имя до двоеточия.
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                currentName = line[..colon].Trim();
                currentElements = document.BlockFields(currentName, indexed: true);
                currentScalars = document.BlockFields(currentName, indexed: false);
                continue;
            }

            var fieldColon = line.IndexOf(':');
            if (fieldColon <= 0)
                continue;

            var field = line[..fieldColon].Trim();
            var value = Unquote(line[(fieldColon + 1)..].Trim());

            // Массивные поля приходят как `имя[N]`; индекс отбрасывается, значения
            // собираются в один список — порядок строк и есть порядок элементов.
            var bracket = field.IndexOf('[');
            if (bracket >= 0)
            {
                field = field[..bracket];
                Add(currentElements!, field, value);
                continue;
            }

            // Без индекса — либо скаляр, либо объявленное число элементов массива;
            // разводятся они в Reconcile().
            Add(currentScalars!, field, value);
        }

        document.Reconcile();
        return document;
    }

    /// <summary>Возвращает словарь полей блока, создавая его при первом обращении.</summary>
    private Dictionary<string, List<string>> BlockFields(string block, bool indexed)
    {
        var target = indexed ? _blocks : _scalars;
        if (target.TryGetValue(block, out var existing))
            return existing;

        if (!indexed)
            return target[block] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Имя блока регистрируется ОДИН раз — по первому появлению: повтор
        // встречается (несколько `save_container`), и повторная запись в список
        // имён давала бы дубликаты.
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        target[block] = fields;
        _blockNames.Add(block);
        return fields;
    }

    private static void Add(Dictionary<string, List<string>> fields, string field, string value)
    {
        if (!fields.TryGetValue(field, out var list))
            fields[field] = list = new List<string>();

        list.Add(value);
    }

    /// <summary>
    /// Разводит скаляры и счётчики массивов. Вызывается один раз после разбора.
    ///
    /// У поля ЕСТЬ элементы с индексом и ОДНО значение без индекса — значит это
    /// массив с объявленным числом элементов: значение переезжает в
    /// <see cref="_arrayCounts"/>, а не остаётся в списке. Во всех остальных
    /// случаях значение без индекса — обычный скаляр и остаётся значением поля.
    /// </summary>
    private void Reconcile()
    {
        foreach (var (block, scalars) in _scalars)
        {
            var elements = _blocks[block];

            foreach (var (field, values) in scalars)
            {
                var isArray = elements.TryGetValue(field, out var indexed) && indexed.Count > 0;

                if (isArray && values.Count == 1 && long.TryParse(values[0], out var declared))
                {
                    if (!_arrayCounts.TryGetValue(block, out var counts))
                        _arrayCounts[block] = counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                    counts[field] = declared;
                    continue;
                }

                // Обычное поле: единственное значение — значение скаляра. Прежний
                // код собирал скаляры и элементы в ОДИН список, поэтому у массивов
                // счётчик стоял первым элементом; здесь они разведены.
                elements[field] = values;
            }
        }
    }

    /// <summary>
    /// Разбивает текст на строки, не ломая значения в кавычках.
    ///
    /// Обычный <c>Split('\n')</c> здесь неверен: строковые значения SII могут
    /// содержать переводы строк внутри кавычек, и такая строка превратилась бы в
    /// два поля, второе из которых — мусор.
    /// </summary>
    private static IEnumerable<string> EnumerateLines(string text)
    {
        var builder = new StringBuilder();
        var inQuotes = false;
        var escaped = false;

        foreach (var ch in text)
        {
            if (escaped)
            {
                builder.Append(ch);
                escaped = false;
                continue;
            }

            switch (ch)
            {
                case '\\' when inQuotes:
                    builder.Append(ch);
                    escaped = true;
                    break;

                case '"':
                    inQuotes = !inQuotes;
                    builder.Append(ch);
                    break;

                case '\n' when !inQuotes:
                    yield return builder.ToString();
                    builder.Clear();
                    break;

                default:
                    builder.Append(ch);
                    break;
            }
        }

        if (builder.Length > 0)
            yield return builder.ToString();
    }

    /// <summary>
    /// Снимает кавычки и раскрывает escape-последовательности SII.
    ///
    /// Важно: незакавыченное содержимое возвращается КАК ЕСТЬ, потому что в SII
    /// имя компании хранится в кавычках с \xNN для не-ASCII, а числа — без кавычек,
    /// и обрабатывать их одинаково значило бы портить одно из двух.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;

        var inner = value[1..^1];
        if (inner.IndexOf('\\') < 0)
            return inner;

        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                builder.Append(inner[i]);
                continue;
            }

            i++;
            switch (inner[i])
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                // SCS пишет не-ASCII как \xNN (по байту UTF-8). Одиночный байт
                // восстановить в символ нельзя — собираем байты и декодируем
                // группу целиком, иначе кириллица превратилась бы в «Ð¢ÑŽÐ¼».
                case 'x': builder.Append(ReadHexByte(inner, ref i)); break;
                default: builder.Append(inner[i]); break;
            }
        }

        return DecodeUtf8Escapes(builder.ToString());
    }

    /// <summary>Собирает \xNN в один байт (символ с кодом 0..255).</summary>
    private static char ReadHexByte(string text, ref int index)
    {
        if (index + 2 >= text.Length)
            return 'x';

        var hex = text.Substring(index + 1, 2);
        index += 2;
        return byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? (char)value
            : 'x';
    }

    /// <summary>
    /// Превращает последовательность байтов (символы 0..255) в UTF-8 строку.
    ///
    /// SII хранит кириллицу как цепочку \xNN, каждый NN — один байт UTF-8.
    /// Декодировать их по отдельности нельзя, поэтому вся строка сначала
    /// собирается в байты, а затем переводится в UTF-8.
    /// </summary>
    private static string DecodeUtf8Escapes(string value)
    {
        var needsDecoding = false;
        foreach (var ch in value)
        {
            if (ch > 0xFF)
                return value; // настоящий Unicode — уже декодировано.

            if (ch is >= (char)0x80)
                needsDecoding = true;
        }

        if (!needsDecoding)
            return value;

        var bytes = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
            bytes[i] = (byte)value[i];

        return Encoding.UTF8.GetString(bytes);
    }
}
