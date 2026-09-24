using AssistQuestEditor.Domain;
using System.Drawing.Imaging;

namespace AssistQuestEditor.App;

/// <summary>
/// Иконка приложения в виде готового ICO.
///
/// Собирается из логотипов в четырёх размерах, лежащих рядом с приложением.
/// Почему не <c>ApplicationIcon</c> из exe: он встраивается в ресурсы PE, но
/// ОТДЕЛЬНЫЕ кадры оттуда не читаются — а иконка ассоциации файла должна быть
/// многоразмерным ICO. Почему не рисовать иконку кодом: нарисованные значки
/// (стрелки, ноды, диалоги) не имели отношения к логотипу, и типы файлов
/// выглядели как чужие приложения.
///
/// Размеры берутся ровно те, что подготовлены: 16, 32, 48 и 256. Промежуточные
/// (24, 64, 128) НЕ досчитываются масштабированием: Windows сама уменьшает
/// ближайший кадр, а лишние кадры только увеличивают файл.
/// </summary>
public static class AppIconService
{
    /// <summary>Имя файла иконки приложения рядом с exe и в поставке.</summary>
    public const string IconFileName = "AQE_logo.ico";

    /// <summary>Размеры кадров в порядке возрастания.</summary>
    public static readonly IReadOnlyList<int> FrameSizes = [16, 32, 48, 256];

    private static readonly object Sync = new();
    private static byte[]? _cachedBytes;
    private static bool _attempted;

    /// <summary>
    /// Каталог ресурсов иконок: рядом с exe.
    ///
    /// Путь берётся от каталога приложения, а не от текущего каталога процесса:
    /// приложение запускают и двойным кликом по файлу ресурса, и из ярлыка, и
    /// тогда рабочий каталог — не папка приложения.
    /// </summary>
    public static string AssetsDirectory => Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>
    /// Готовый многоразмерный ICO.
    ///
    /// Результат кэшируется: ICO читают и окна, и регистрация ассоциаций, а
    /// пересобирать файл на каждое обращение незачем.
    /// Возвращает <c>null</c>, если логотипов рядом нет: отсутствие иконки не
    /// должно мешать работе, и вызывающий код просто оставит иконку по умолчанию.
    /// </summary>
    public static byte[]? ToIcoBytes()
    {
        lock (Sync)
        {
            if (_attempted)
                return _cachedBytes;

            _attempted = true;

            try
            {
                _cachedBytes = BuildIco();
                AppLogger.Info("AppIcon: иконка собрана.",
                    $"frames={FrameSizes.Count}; bytes={_cachedBytes.Length}; dir={AssetsDirectory}");
            }
            catch (Exception ex)
            {
                // Иконка — оформление. Её отсутствие не должно ронять приложение.
                AppLogger.Warn("AppIcon: не удалось собрать иконку из логотипов.", ex.Message);
                _cachedBytes = null;
            }

            return _cachedBytes;
        }
    }

    /// <summary>
    /// Иконка окна.
    ///
    /// Загружается из собранного ICO через <see cref="Icon"/>: окну нужен объект
    /// GDI, а не байты. При неудаче возвращается <c>null</c> — форма останется с
    /// иконкой по умолчанию, но откроется.
    /// </summary>
    public static Icon? CreateWindowIcon()
    {
        var bytes = ToIcoBytes();
        if (bytes is null)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            // Копия из потока обязательна: Icon держит поток открытым, а он
            // закрывается вместе с using — иначе иконка «портилась» бы после
            // возврата из метода.
            using var source = new Icon(stream);
            return (Icon)source.Clone();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AppIcon: не удалось создать иконку окна.", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Существует ли ICO как ФАЙЛ рядом с приложением.
    ///
    /// Отдельная проверка нужна ассоциациям: они ссылаются на путь, а не на
    /// содержимое, и при отсутствии файла ссылка вела бы в никуда.
    /// </summary>
    public static string? IconFilePath()
    {
        var path = Path.Combine(AssetsDirectory, IconFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Ставит иконку приложения на окно.
    ///
    /// Один вызов на форму вместо копии в каждой: окно без иконки выглядит
    /// чужим в панели задач и в Alt+Tab, а забыть выставить её в новой форме —
    /// самое частое упущение. Отсутствие иконки не мешает окну открыться.
    /// </summary>
    public static void ApplyTo(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (form.Icon is not null)
            return;

        var icon = CreateWindowIcon();
        if (icon is not null)
            form.Icon = icon;
    }

    /// <summary>
    /// Собирает ICO из подготовленных PNG-кадров.
    ///
    /// Формат контейнера: ICONDIR (6 байт) и по 16 байт на кадр, затем кадры.
    /// Кадры кладутся как PNG, а не как BMP: так файл компактнее, и Windows Vista
    /// и новее читает PNG-кадры во всех размерах, включая 256.
    /// </summary>
    private static byte[] BuildIco()
    {
        var frames = new List<(int Size, byte[] Bytes)>();

        foreach (var size in FrameSizes)
        {
            var path = Path.Combine(AssetsDirectory, $"AQE_logo_{size}.png");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Логотип размера {size} не найден. Ожидался файл: {path}", path);

            frames.Add((size, File.ReadAllBytes(path)));
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // тип: 1 = иконка
        writer.Write((ushort)frames.Count);   // число кадров

        var offset = 6 + 16 * frames.Count;

        foreach (var (size, bytes) in frames)
        {
            // Ширина и высота занимают ОДИН байт, поэтому 256 записывается как 0:
            // это и есть соглашение формата для «256».
            var dimension = (byte)(size >= 256 ? 0 : size);

            writer.Write(dimension);
            writer.Write(dimension);
            writer.Write((byte)0);            // размер палитры
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // плоскостей цвета
            writer.Write((ushort)32);         // бит на пиксель
            writer.Write(bytes.Length);
            writer.Write(offset);

            offset += bytes.Length;
        }

        foreach (var (_, bytes) in frames)
            writer.Write(bytes);

        writer.Flush();
        return output.ToArray();
    }
}
