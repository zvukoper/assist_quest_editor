using System.Globalization;
using System.Text;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Декодер двоичного SII (<c>BSII</c>, версии 1/2/3) в читаемый текст
/// <c>SiiNunit</c>.
///
/// Формат описан TheLazyTomcat/SII_Decrypt и проверен на реальном
/// <c>game.sii</c> из ETS2 (~2 МБ, распознаётся целиком). Двоичный SII — это
/// необязательная часть чтения профиля: <c>profile.sii</c> и <c>info.sii</c>
/// приходят текстом, а <c>game.sii</c> — двоичными, и без этого декодера
/// метаданные сохранения остались бы недоступны.
///
/// Отличие от инструмента в <c>ci/</c>: здесь нет статических журналов и трассировки —
/// домен не пишет в консоль, а ошибки выражает исключением. Правило формата при
/// этом одно: вторая версия декодера разошлась бы с первой на первом же
/// изменении игры.
/// </summary>
public static class Ets2BinarySii
{
    private const string Base38Chars = "0123456789abcdefghijklmnopqrstuvwxyz_";
    private const uint BsiiSignature = 0x49495342u; // "BSII"

    /// <summary>Декодирует полезную нагрузку BSII в текст SiiNunit.</summary>
    public static string Decode(byte[] data)
    {
        var reader = new Reader(data);
        if (reader.ReadUInt32() != BsiiSignature)
            throw new InvalidDataException("Полезная нагрузка не является BSII.");

        var version = reader.ReadUInt32();
        if (version is < 1 or > 3)
            throw new InvalidDataException($"Версия BSII {version} не поддерживается.");

        var structures = new Dictionary<uint, Structure>();
        var builder = new StringBuilder(1 << 20);
        builder.Append("SiiNunit\n{\n");

        while (reader.Position < reader.Length)
        {
            var blockType = reader.ReadUInt32();
            if (blockType == 0)
            {
                if (reader.ReadByte() == 0)
                    break; // конец файла

                var id = reader.ReadUInt32();
                if (id == 0)
                    throw new InvalidDataException("Идентификатор структуры не может быть нулевым.");

                var structure = new Structure { Id = id, Name = reader.ReadString() };
                while (true)
                {
                    var valueType = reader.ReadUInt32();
                    if (valueType == 0)
                        break;

                    var field = new Field { Type = valueType, Name = reader.ReadString() };
                    if (valueType == 0x37)
                        field.OrdinalTable = reader.ReadOrdinalTable();
                    structure.Fields.Add(field);
                }

                structures[id] = structure;
                continue;
            }

            if (!structures.TryGetValue(blockType, out var block))
                throw new InvalidDataException($"Блок данных ссылается на неизвестную структуру {blockType}.");

            var blockId = reader.ReadIdString();
            builder.Append('\n').Append(' ').Append(block.Name).Append(" : ").Append(blockId).Append(" {\n");
            foreach (var field in block.Fields)
                reader.WriteField(builder, version, field);
            builder.Append("}\n");
        }

        builder.Append("}\n");
        return builder.ToString();
    }

    private sealed class Field
    {
        public uint Type;
        public string Name = string.Empty;
        public List<KeyValuePair<uint, string>>? OrdinalTable;
    }

    private sealed class Structure
    {
        public uint Id;
        public string Name = string.Empty;
        public readonly List<Field> Fields = new();
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        private int _position;

        public Reader(byte[] data) => _data = data;

        public int Position => _position;
        public int Length => _data.Length;

        public byte ReadByte() => _data[_position++];

        public ushort ReadUInt16()
        {
            ushort value = (ushort)(_data[_position] | (_data[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public short ReadInt16() => unchecked((short)ReadUInt16());

        public uint ReadUInt32()
        {
            var value = (uint)(_data[_position]
                | (_data[_position + 1] << 8)
                | (_data[_position + 2] << 16)
                | (_data[_position + 3] << 24));
            _position += 4;
            return value;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public ulong ReadUInt64() => ReadUInt32() | ((ulong)ReadUInt32() << 32);

        public long ReadInt64() => unchecked((long)ReadUInt64());

        public float ReadSingle()
        {
            var value = BitConverter.ToSingle(_data, _position);
            _position += 4;
            return value;
        }

        public string ReadString()
        {
            var length = (int)ReadUInt32();
            var value = Encoding.UTF8.GetString(_data, _position, length);
            _position += length;
            return value;
        }

        public List<KeyValuePair<uint, string>> ReadOrdinalTable()
        {
            var count = (int)ReadUInt32();
            var list = new List<KeyValuePair<uint, string>>(count);
            for (var i = 0; i < count; i++)
            {
                var ordinal = ReadUInt32();
                list.Add(new KeyValuePair<uint, string>(ordinal, ReadString()));
            }

            return list;
        }

        public string ReadIdString()
        {
            var length = ReadByte();
            var parts = length == 0xFF ? 1 : length;
            var values = new ulong[parts];
            for (var i = 0; i < parts; i++)
                values[i] = ReadUInt64();

            if (length == 0xFF)
            {
                var builder = new StringBuilder("_nameless");
                for (var i = 0; i < parts; i++)
                    builder.Append('.').Append(HexGroups(values[i]));
                return builder.ToString();
            }

            if (length == 0)
                return "null";

            var decoded = new string[parts];
            for (var i = 0; i < parts; i++)
                decoded[i] = DecodeBase38(values[i]);
            return string.Join(".", decoded);
        }

        private static string HexGroups(ulong value)
        {
            var hex = value.ToString("x", CultureInfo.InvariantCulture);
            if (hex.Length <= 4)
                return hex;

            var chunks = new List<string>();
            for (var end = hex.Length; end > 0;)
            {
                var start = Math.Max(0, end - 4);
                chunks.Add(hex.Substring(start, end - start));
                end = start;
            }

            chunks.Reverse();
            return string.Join(".", chunks);
        }

        private static string DecodeBase38(ulong encoded)
        {
            var value = encoded & 0x7FFFFFFFFFFFFFFFul;
            if (value == 0)
                return string.Empty;

            // Кодирование идёт по строке С КОНЦА, накапливая result = result * 38 + index,
            // поэтому первый остаток — ПЕРВЫЙ символ: добавляем по мере извлечения.
            var chars = new List<char>();
            while (value > 0)
            {
                var index = (int)(value % 38);
                if (index > 0)
                    chars.Add(Base38Chars[index - 1]);
                value /= 38;
            }

            return new string(chars.ToArray());
        }

        public void WriteField(StringBuilder builder, uint version, Field field)
        {
            var type = field.Type;
            switch (type)
            {
                case 0x01: AppendScalar(builder, field.Name, ReadString()); break;
                case 0x02: AppendStringArray(builder, field.Name); break;
                case 0x03: AppendScalar(builder, field.Name, DecodeBase38(ReadUInt64())); break;
                case 0x04: AppendUlongArray(builder, field.Name, true); break;
                case 0x05: AppendScalar(builder, field.Name, FloatToStr(ReadSingle())); break;
                case 0x06: AppendSingleArray(builder, field.Name); break;
                case 0x07: AppendScalar(builder, field.Name, VecToStr(ReadFloatArray(2), 2)); break;
                case 0x08: AppendIndexedVectorArray(builder, field.Name, 2); break;
                case 0x09: AppendScalar(builder, field.Name, VecToStr(ReadFloatArray(3), 3)); break;
                case 0x0A: AppendIndexedVectorArray(builder, field.Name, 3); break;
                case 0x11: AppendScalar(builder, field.Name, Vec3iToStr()); break;
                case 0x12: AppendIndexedVectorArray(builder, field.Name, 3, vector3i: true); break;
                case 0x17: AppendScalar(builder, field.Name, VecToStr(ReadFloatArray(4), 4)); break;
                case 0x18: AppendIndexedVectorArray(builder, field.Name, 4); break;
                case 0x19: AppendScalar(builder, field.Name, VecToStr(ReadFloatArray(8), version == 1 ? 7 : 8)); break;
                case 0x1A: AppendIndexedVectorArray(builder, field.Name, 8, components: version == 1 ? 7 : 8); break;
                case 0x25: AppendScalar(builder, field.Name, ReadInt32().ToString(CultureInfo.InvariantCulture)); break;
                case 0x26: AppendIntArray(builder, field.Name); break;
                case 0x27: AppendScalar(builder, field.Name, ReadUInt32().ToString(CultureInfo.InvariantCulture)); break;
                case 0x28: AppendUintArray(builder, field.Name); break;
                case 0x29: AppendScalar(builder, field.Name, ReadInt16().ToString(CultureInfo.InvariantCulture)); break;
                case 0x2A: AppendShortArray(builder, field.Name); break;
                case 0x2B: AppendScalar(builder, field.Name, ReadUInt16().ToString(CultureInfo.InvariantCulture)); break;
                case 0x2C: AppendUshortArray(builder, field.Name); break;
                case 0x2F: AppendScalar(builder, field.Name, ReadUInt32().ToString(CultureInfo.InvariantCulture)); break;
                case 0x31: AppendScalar(builder, field.Name, ReadInt64().ToString(CultureInfo.InvariantCulture)); break;
                case 0x32: AppendLongArray(builder, field.Name); break;
                case 0x33: AppendScalar(builder, field.Name, ReadUInt64().ToString(CultureInfo.InvariantCulture)); break;
                case 0x34: AppendUlongArray(builder, field.Name, false); break;
                case 0x35: AppendScalar(builder, field.Name, ReadByte() != 0 ? "true" : "false"); break;
                case 0x36: AppendBoolArray(builder, field.Name); break;
                case 0x37: AppendOrdinal(builder, field, ReadUInt32()); break;
                case 0x39 or 0x3B or 0x3D: AppendScalar(builder, field.Name, ReadIdString()); break;
                case 0x3A or 0x3C or 0x3E: AppendIdArray(builder, field.Name); break;
                default:
                    throw new InvalidDataException(
                        $"Тип значения 0x{type:x8} поля «{field.Name}» не поддерживается.");
            }
        }

        private void AppendOrdinal(StringBuilder builder, Field field, uint ordinal)
        {
            string? text = null;
            if (field.OrdinalTable is not null)
            {
                foreach (var pair in field.OrdinalTable)
                {
                    if (pair.Key != ordinal)
                        continue;

                    text = pair.Value;
                    break;
                }
            }

            AppendScalar(builder, field.Name, QuoteIfNeeded(text ?? ordinal.ToString(CultureInfo.InvariantCulture)));
        }

        private void AppendIdArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, ReadIdString());
        }

        private void AppendIndexedVectorArray(
            StringBuilder builder, string name, int dimensions, int components = 0, bool vector3i = false)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            var actualComponents = components > 0 ? components : dimensions;
            for (var i = 0; i < count; i++)
            {
                var text = vector3i
                    ? Vec3iToStr()
                    : VecToStr(ReadFloatArray(dimensions), actualComponents);
                AppendIndexed(builder, name, i, text);
            }
        }

        private string Vec3iToStr()
            => "(" + ReadInt32().ToString(CultureInfo.InvariantCulture) + ", "
                + ReadInt32().ToString(CultureInfo.InvariantCulture) + ", "
                + ReadInt32().ToString(CultureInfo.InvariantCulture) + ")";

        private float[] ReadFloatArray(int components)
        {
            var values = new float[components];
            for (var i = 0; i < components; i++)
                values[i] = ReadSingle();
            return values;
        }

        private void AppendStringArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, QuoteIfNeeded(ReadString()));
        }

        private void AppendSingleArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, FloatToStr(ReadSingle()));
        }

        private void AppendUlongArray(StringBuilder builder, string name, bool base38)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
            {
                var value = ReadUInt64();
                AppendIndexed(builder, name, i,
                    base38 ? DecodeBase38(value) : value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void AppendIntArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, ReadInt32().ToString(CultureInfo.InvariantCulture));
        }

        private void AppendUintArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, ReadUInt32().ToString(CultureInfo.InvariantCulture));
        }

        private void AppendShortArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, ReadInt16().ToString(CultureInfo.InvariantCulture));
        }

        private void AppendUshortArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, ReadUInt16().ToString(CultureInfo.InvariantCulture));
        }

        private void AppendLongArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, ReadInt64().ToString(CultureInfo.InvariantCulture));
        }

        private void AppendBoolArray(StringBuilder builder, string name)
        {
            var count = (int)ReadUInt32();
            AppendScalar(builder, name, count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
                AppendIndexed(builder, name, i, ReadByte() != 0 ? "true" : "false");
        }

        private static string VecToStr(float[] values, int components)
        {
            var parts = new string[components];
            for (var i = 0; i < components; i++)
                parts[i] = FloatToStr(values[i]);
            return "(" + string.Join(", ", parts) + ")";
        }

        private static string FloatToStr(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return value.ToString(CultureInfo.InvariantCulture);

            var wide = (double)value;
            if (Math.Floor(wide) == wide && Math.Abs(wide) < 1e15)
                return ((long)wide).ToString(CultureInfo.InvariantCulture);

            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string QuoteIfNeeded(string value)
        {
            var limited = value.Length > 0;
            for (var i = 0; i < value.Length && limited; i++)
            {
                var ch = value[i];
                var allowed = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || ch == '_';
                if (!allowed)
                    limited = false;
            }

            return limited
                ? value
                : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void AppendScalar(StringBuilder builder, string name, string value)
            => builder.Append(' ').Append(name).Append(": ").Append(value).Append('\n');

        private static void AppendIndexed(StringBuilder builder, string name, int index, string value)
            => builder.Append(' ').Append(name).Append('[').Append(index.ToString(CultureInfo.InvariantCulture))
                .Append("]: ").Append(value).Append('\n');
    }
}
