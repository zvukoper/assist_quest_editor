// Binary SII (BSII) decoder for SCS Software save games.
// Converts a decoded ("BSII", version 1/2/3) binary SII payload into readable
// textual SiiNunit form. Port of the format described by TheLazyTomcat/SII_Decrypt
// (Documents/Binary SII - Format.txt and - Types.txt).
//
// Notes: all values are little-endian; strings are UTF-8, length-prefixed (UInt32);
// arrays are length-prefixed (UInt32); no padding/alignment anywhere.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Aqe.Scs
{
    internal sealed class BsiiField
    {
        public uint Type;
        public string Name;
        public List<KeyValuePair<uint, string>> OrdinalTable; // only for type 0x37
    }

    internal sealed class BsiiStructure
    {
        public uint Id;
        public string Name;
        public readonly List<BsiiField> Fields = new List<BsiiField>();
    }

    public static class BsiiDecoder
    {
        private const string Base38Chars = "0123456789abcdefghijklmnopqrstuvwxyz_";

        public static string LastStructure = "";
        public static string LastField = "";
        public static readonly List<string> BlockLog = new List<string>();
        public static string TraceBlock = null;
        public static readonly List<string> TraceLog = new List<string>();

        public static string DecodeFile(string inputPath)
        {
            byte[] data = File.ReadAllBytes(inputPath);
            return Decode(data);
        }

        public static void DecodeToFile(string inputPath, string outputPath)
        {
            string text = DecodeFile(inputPath);
            File.WriteAllText(outputPath, text, new UTF8Encoding(false));
        }

        public static string Decode(byte[] d)
        {
            var br = new BinReader(d);
            uint signature = br.ReadUInt32();
            if (signature != 0x49495342u) // "BSII"
                throw new InvalidDataException("Not a BSII payload (signature 0x" + signature.ToString("x8") + ").");
            uint version = br.ReadUInt32();
            if (version < 1 || version > 3)
                throw new InvalidDataException("Unsupported BSII version " + version + ".");

            var structures = new Dictionary<uint, BsiiStructure>();
            var sb = new StringBuilder(1 << 20);
            sb.Append("SiiNunit\n{\n");

            while (br.Position < br.Length)
            {
                uint blockType = br.ReadUInt32();
                if (blockType == 0)
                {
                    byte valid = br.ReadByte();
                    if (valid == 0) break; // end of file

                    uint id = br.ReadUInt32();
                    if (id == 0) throw new InvalidDataException("Structure ID 0 is invalid.");
                    var st = new BsiiStructure();
                    st.Id = id;
                    st.Name = br.ReadString();

                    while (true)
                    {
                        uint vt = br.ReadUInt32();
                        if (vt == 0) break;
                        var f = new BsiiField();
                        f.Type = vt;
                        f.Name = br.ReadString();
                        if (vt == 0x37) f.OrdinalTable = br.ReadOrdinalTable();
                        st.Fields.Add(f);
                    }
                    structures[id] = st;
                }
                else
                {
                    BsiiStructure st;
                    if (!structures.TryGetValue(blockType, out st))
                    {
                        var tail = new StringBuilder();
                        int start = Math.Max(0, BlockLog.Count - 12);
                        for (int i = start; i < BlockLog.Count; i++) tail.Append("\n  ").Append(BlockLog[i]);
                        throw new InvalidDataException("Data block at 0x" + (br.Position - 4).ToString("x") + " references unknown structure ID " + blockType + ". Last field: " + LastStructure + " / '" + LastField + "'. Recent blocks:" + tail);
                    }
                    BlockLog.Add("0x" + (br.Position - 4).ToString("x") + " " + st.Name);
                    if (BlockLog.Count > 100000) BlockLog.RemoveRange(0, 50000);
                    if (TraceBlock != null) TraceLog.Add("BLOCK " + st.Name + " @0x" + (br.Position - 4).ToString("x"));

                    string blockId = br.ReadIdString();
                    sb.Append('\n');
                    sb.Append(' ').Append(st.Name).Append(" : ").Append(blockId).Append(" {\n");
                    for (int i = 0; i < st.Fields.Count; i++)
                    {
                        LastStructure = st.Name;
                        LastField = st.Fields[i].Name + " (0x" + st.Fields[i].Type.ToString("x8") + ")";
                        bool trace = TraceBlock != null && st.Name.IndexOf(TraceBlock, StringComparison.OrdinalIgnoreCase) >= 0;
                        int startOff = br.Position;
                        br.WriteField(sb, version, st.Fields[i]);
                        if (trace) TraceLog.Add("0x" + startOff.ToString("x") + "..0x" + br.Position.ToString("x") + " (" + (br.Position - startOff) + "B) " + LastField);
                    }
                    sb.Append("}\n");
                }
            }

            sb.Append("}\n");
            return sb.ToString();
        }

        // ---------------------------------------------------------------------

        private sealed class BinReader
        {
            private readonly byte[] _d;
            private int _p;

            public BinReader(byte[] data) { _d = data; _p = 0; }

            public int Position { get { return _p; } }
            public int Length { get { return _d.Length; } }

            public byte ReadByte() { return _d[_p++]; }
            public sbyte ReadSByte() { return unchecked((sbyte)_d[_p++]); }
            public ushort ReadUInt16() { ushort v = (ushort)(_d[_p] | (_d[_p + 1] << 8)); _p += 2; return v; }
            public short ReadInt16() { return unchecked((short)ReadUInt16()); }
            public uint ReadUInt32() { uint v = (uint)(_d[_p] | (_d[_p + 1] << 8) | (_d[_p + 2] << 16) | (_d[_p + 3] << 24)); _p += 4; return v; }
            public int ReadInt32() { return unchecked((int)ReadUInt32()); }
            public ulong ReadUInt64() { ulong lo = ReadUInt32(); ulong hi = ReadUInt32(); return lo | (hi << 32); }
            public long ReadInt64() { return unchecked((long)ReadUInt64()); }
            public float ReadSingle() { float v = BitConverter.ToSingle(_d, _p); _p += 4; return v; }
            public string ReadString()
            {
                int len = (int)ReadUInt32();
                string s = Encoding.UTF8.GetString(_d, _p, len);
                _p += len;
                return s;
            }

            public List<KeyValuePair<uint, string>> ReadOrdinalTable()
            {
                int count = (int)ReadUInt32();
                var list = new List<KeyValuePair<uint, string>>(count);
                for (int i = 0; i < count; i++)
                {
                    uint ord = ReadUInt32();
                    string s = ReadString();
                    list.Add(new KeyValuePair<uint, string>(ord, s));
                }
                return list;
            }

            public string ReadIdString()
            {
                int len = ReadByte();
                int parts = (len == 0xFF) ? 1 : len;
                var values = new ulong[parts];
                for (int i = 0; i < parts; i++) values[i] = ReadUInt64();

                if (len == 0xFF)
                {
                    var sb = new StringBuilder("_nameless");
                    for (int i = 0; i < parts; i++) sb.Append('.').Append(HexGroups(values[i]));
                    return sb.ToString();
                }
                if (len == 0) return "null";
                var parts2 = new string[parts];
                for (int i = 0; i < parts; i++) parts2[i] = DecodeBase38(values[i]);
                return string.Join(".", parts2);
            }

            // Renders a 64-bit value as hex, grouped in 4-digit chunks from the right.
            private static string HexGroups(ulong v)
            {
                string hex = v.ToString("x", CultureInfo.InvariantCulture);
                if (hex.Length <= 4) return hex;
                var chunks = new List<string>();
                int end = hex.Length;
                while (end > 0)
                {
                    int start = end - 4; if (start < 0) start = 0;
                    chunks.Add(hex.Substring(start, end - start));
                    end = start;
                }
                chunks.Reverse();
                return string.Join(".", chunks.ToArray());
            }

            private static string DecodeBase38(ulong encoded)
            {
                ulong v = encoded & 0x7FFFFFFFFFFFFFFFul;
                if (v == 0) return "";
                var chars = new List<char>();
                while (v > 0)
                {
                    int idx = (int)(v % 38);
                    // Encoding traverses the string backwards accumulating
                    // result = result * 38 + index, so the first modulus yields
                    // the FIRST character: append in extraction order.
                    if (idx > 0) chars.Add(Base38Chars[idx - 1]);
                    v /= 38;
                }
                return new string(chars.ToArray());
            }

            // --- fields -------------------------------------------------------

            public void WriteField(StringBuilder sb, uint version, BsiiField f)
            {
                uint t = f.Type;
                if (t == 0x01) AppendScalar(sb, f.Name, ReadString());
                else if (t == 0x02) AppendStringArray(sb, f.Name, ReadStringArray());
                else if (t == 0x03) AppendScalar(sb, f.Name, DecodeBase38(ReadUInt64()));
                else if (t == 0x04) AppendUlongArray(sb, f.Name, ReadUInt64Array(), true);
                else if (t == 0x05) AppendScalar(sb, f.Name, FloatToStr(ReadSingle()));
                else if (t == 0x06) AppendSingleArray(sb, f.Name);
                else if (t == 0x07) AppendScalar(sb, f.Name, VecToStr(ReadFloatArray(2), 2));
                else if (t == 0x08) { int n = (int)ReadUInt32(); AppendScalar(sb, f.Name, n.ToString(CultureInfo.InvariantCulture)); for (int i = 0; i < n; i++) AppendIndexed(sb, f.Name, i, VecToStr(ReadFloatArray(2), 2)); }
                else if (t == 0x09) AppendScalar(sb, f.Name, VecToStr(ReadFloatArray(3), 3));
                else if (t == 0x0A) { int n = (int)ReadUInt32(); AppendScalar(sb, f.Name, n.ToString(CultureInfo.InvariantCulture)); for (int i = 0; i < n; i++) AppendIndexed(sb, f.Name, i, VecToStr(ReadFloatArray(3), 3)); }
                else if (t == 0x11) AppendScalar(sb, f.Name, Vec3iToStr());
                else if (t == 0x12) { int n = (int)ReadUInt32(); AppendScalar(sb, f.Name, n.ToString(CultureInfo.InvariantCulture)); for (int i = 0; i < n; i++) AppendIndexed(sb, f.Name, i, Vec3iToStr()); }
                else if (t == 0x17) AppendScalar(sb, f.Name, VecToStr(ReadFloatArray(4), 4));
                else if (t == 0x18) { int n = (int)ReadUInt32(); AppendScalar(sb, f.Name, n.ToString(CultureInfo.InvariantCulture)); for (int i = 0; i < n; i++) AppendIndexed(sb, f.Name, i, VecToStr(ReadFloatArray(4), 4)); }
                else if (t == 0x19) { int comps = (version == 1) ? 7 : 8; AppendScalar(sb, f.Name, VecToStr(ReadFloatArray(8), comps)); }
                else if (t == 0x1A) { int comps = (version == 1) ? 7 : 8; int n = (int)ReadUInt32(); AppendScalar(sb, f.Name, n.ToString(CultureInfo.InvariantCulture)); for (int i = 0; i < n; i++) AppendIndexed(sb, f.Name, i, VecToStr(ReadFloatArray(8), comps)); }
                else if (t == 0x25) AppendScalar(sb, f.Name, ReadInt32().ToString(CultureInfo.InvariantCulture));
                else if (t == 0x26) AppendIntArray(sb, f.Name, ReadIntArray());
                else if (t == 0x27) AppendScalar(sb, f.Name, ReadUInt32().ToString(CultureInfo.InvariantCulture));
                else if (t == 0x28) AppendUintArray(sb, f.Name, ReadUintArray());
                else if (t == 0x29) AppendScalar(sb, f.Name, ReadInt16().ToString(CultureInfo.InvariantCulture));
                else if (t == 0x2A) AppendShortArray(sb, f.Name);
                else if (t == 0x2B) AppendScalar(sb, f.Name, ReadUInt16().ToString(CultureInfo.InvariantCulture));
                else if (t == 0x2C) AppendUshortArray(sb, f.Name);
                else if (t == 0x2F) AppendScalar(sb, f.Name, ReadUInt32().ToString(CultureInfo.InvariantCulture));
                else if (t == 0x31) AppendScalar(sb, f.Name, ReadInt64().ToString(CultureInfo.InvariantCulture));
                else if (t == 0x32) AppendLongArray(sb, f.Name);
                else if (t == 0x33) AppendScalar(sb, f.Name, ReadUInt64().ToString(CultureInfo.InvariantCulture));
                else if (t == 0x34) AppendUlongArray(sb, f.Name, ReadUInt64Array(), false);
                else if (t == 0x35) AppendScalar(sb, f.Name, ReadByte() != 0 ? "true" : "false");
                else if (t == 0x36) AppendBoolArray(sb, f.Name);
                else if (t == 0x37) AppendOrdinal(sb, f, ReadUInt32());
                else if (t == 0x39 || t == 0x3B || t == 0x3D) AppendScalar(sb, f.Name, ReadIdString());
                else if (t == 0x3A || t == 0x3C || t == 0x3E) { int n = (int)ReadUInt32(); AppendScalar(sb, f.Name, n.ToString(CultureInfo.InvariantCulture)); for (int i = 0; i < n; i++) AppendIndexed(sb, f.Name, i, ReadIdString()); }
                else throw new InvalidDataException("Unsupported value type 0x" + t.ToString("x8") + " for field '" + f.Name + "' at 0x" + _p.ToString("x"));
            }

            private void AppendOrdinal(StringBuilder sb, BsiiField f, uint ordinal)
            {
                string s = null;
                if (f.OrdinalTable != null)
                {
                    for (int i = 0; i < f.OrdinalTable.Count; i++)
                        if (f.OrdinalTable[i].Key == ordinal) { s = f.OrdinalTable[i].Value; break; }
                }
                if (s == null) s = ordinal.ToString(CultureInfo.InvariantCulture);
                AppendScalar(sb, f.Name, QuoteIfNeeded(s));
            }

            private ulong[] ReadUInt64Array() { int n = (int)ReadUInt32(); var a = new ulong[n]; for (int i = 0; i < n; i++) a[i] = ReadUInt64(); return a; }
            private float[] ReadFloatArray(int comps) { var a = new float[comps]; for (int i = 0; i < comps; i++) a[i] = ReadSingle(); return a; }
            private string[] ReadStringArray() { int n = (int)ReadUInt32(); var a = new string[n]; for (int i = 0; i < n; i++) a[i] = ReadString(); return a; }
            private int[] ReadIntArray() { int n = (int)ReadUInt32(); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = ReadInt32(); return a; }
            private uint[] ReadUintArray() { int n = (int)ReadUInt32(); var a = new uint[n]; for (int i = 0; i < n; i++) a[i] = ReadUInt32(); return a; }

            private void AppendStringArray(StringBuilder sb, string name, string[] a)
            {
                AppendScalar(sb, name, a.Length.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < a.Length; i++) AppendIndexed(sb, name, i, QuoteIfNeeded(a[i]));
            }
            private void AppendSingleArray(StringBuilder sb, string name)
            {
                int n = (int)ReadUInt32();
                AppendScalar(sb, name, n.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < n; i++) AppendIndexed(sb, name, i, FloatToStr(ReadSingle()));
            }
            private void AppendFloatArray(StringBuilder sb, string name, float[] a, int comps)
            {
                AppendScalar(sb, name, a.Length.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < a.Length; i++) AppendIndexed(sb, name, i, FloatToStr(a[i]));
            }
            private void AppendUlongArray(StringBuilder sb, string name, ulong[] a, bool base38)
            {
                AppendScalar(sb, name, a.Length.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < a.Length; i++) AppendIndexed(sb, name, i, base38 ? DecodeBase38(a[i]) : a[i].ToString(CultureInfo.InvariantCulture));
            }
            private void AppendIntArray(StringBuilder sb, string name, int[] a)
            {
                AppendScalar(sb, name, a.Length.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < a.Length; i++) AppendIndexed(sb, name, i, a[i].ToString(CultureInfo.InvariantCulture));
            }
            private void AppendUintArray(StringBuilder sb, string name, uint[] a)
            {
                AppendScalar(sb, name, a.Length.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < a.Length; i++) AppendIndexed(sb, name, i, a[i].ToString(CultureInfo.InvariantCulture));
            }
            private void AppendShortArray(StringBuilder sb, string name)
            {
                int n = (int)ReadUInt32();
                AppendScalar(sb, name, n.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < n; i++) AppendIndexed(sb, name, i, ReadInt16().ToString(CultureInfo.InvariantCulture));
            }
            private void AppendUshortArray(StringBuilder sb, string name)
            {
                int n = (int)ReadUInt32();
                AppendScalar(sb, name, n.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < n; i++) AppendIndexed(sb, name, i, ReadUInt16().ToString(CultureInfo.InvariantCulture));
            }
            private void AppendLongArray(StringBuilder sb, string name)
            {
                int n = (int)ReadUInt32();
                AppendScalar(sb, name, n.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < n; i++) AppendIndexed(sb, name, i, ReadInt64().ToString(CultureInfo.InvariantCulture));
            }
            private void AppendBoolArray(StringBuilder sb, string name)
            {
                int n = (int)ReadUInt32();
                AppendScalar(sb, name, n.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < n; i++) AppendIndexed(sb, name, i, ReadByte() != 0 ? "true" : "false");
            }

            private string Vec3iToStr()
            {
                int a = ReadInt32(), b = ReadInt32(), c = ReadInt32();
                return "(" + a.ToString(CultureInfo.InvariantCulture) + ", " + b.ToString(CultureInfo.InvariantCulture) + ", " + c.ToString(CultureInfo.InvariantCulture) + ")";
            }

            private static string VecToStr(float[] v, int comps)
            {
                var parts = new string[comps];
                for (int i = 0; i < comps; i++) parts[i] = FloatToStr(v[i]);
                return "(" + string.Join(", ", parts) + ")";
            }

            private static string FloatToStr(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) return value.ToString(CultureInfo.InvariantCulture);
                double dv = value;
                if (Math.Floor(dv) == dv && Math.Abs(dv) < 1e15)
                    return ((long)dv).ToString(CultureInfo.InvariantCulture);
                return value.ToString("0.######", CultureInfo.InvariantCulture);
            }

            private static string QuoteIfNeeded(string s)
            {
                if (s == null) return "\"\"";
                bool limited = s.Length > 0;
                for (int i = 0; i < s.Length && limited; i++)
                {
                    char c = s[i];
                    bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || c == '_';
                    if (!ok) limited = false;
                }
                return limited ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }

            private static void AppendScalar(StringBuilder sb, string name, string value)
            {
                sb.Append(' ').Append(name).Append(": ").Append(value).Append('\n');
            }

            private static void AppendIndexed(StringBuilder sb, string name, int index, string value)
            {
                sb.Append(' ').Append(name).Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append("]: ").Append(value).Append('\n');
            }
        }
    }
}
