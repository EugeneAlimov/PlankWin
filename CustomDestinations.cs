using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PlankWin
{
    // Пункт из .customDestinations-ms: сериализованный ярлык (.lnk) — exe + аргументы + заголовок.
    internal sealed class CustomEntry
    {
        public string Target = "";
        public string Arguments = "";
        public string WorkingDir = "";
        public string Title = "";
        public string Description = "";
    }

    internal sealed class CustomCategory
    {
        public string Name = "";
        public bool IsTasks;
        public List<CustomEntry> Items = new();
    }

    // Формат: version, numCategories, reserved; категории: 0 = своя (имя + пункты), 1 = известная (Recent/Frequent, без пунктов),
    // 2 = задачи. Пункт = CLSID ShellLink (16 байт) + тело .lnk. Между категориями — сигнатура 0xBABFFBAB.
    internal static class CustomDestinations
    {
        private const uint Footer = 0xBABFFBAB;
        private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

        public static List<CustomCategory> Parse(byte[] d)
        {
            var cats = new List<CustomCategory>();
            try
            {
                if (d.Length < 12) return cats;
                int ncat = BitConverter.ToInt32(d, 4);
                int pos = 12;
                for (int c = 0; c < ncat && pos + 4 <= d.Length; c++)
                {
                    int type = BitConverter.ToInt32(d, pos); pos += 4;
                    var cat = new CustomCategory();
                    int count = 0;
                    if (type == 0)
                    {
                        int nlen = BitConverter.ToUInt16(d, pos); pos += 2;
                        cat.Name = ResolveIndirect(Encoding.Unicode.GetString(d, pos, nlen * 2)); pos += nlen * 2;
                        count = BitConverter.ToInt32(d, pos); pos += 4;
                    }
                    else if (type == 1) { pos += 4; }
                    else if (type == 2) { cat.IsTasks = true; count = BitConverter.ToInt32(d, pos); pos += 4; }
                    else throw new InvalidDataException("category type " + type);

                    for (int i = 0; i < count; i++)
                    {
                        var g = new Guid(new ReadOnlySpan<byte>(d, pos, 16));
                        if (g != ShellLinkClsid) throw new InvalidDataException("entry clsid " + g);
                        var e = Lnk.Parse(d, pos + 16, out int end);
                        if (e == null || end <= pos + 16) throw new InvalidDataException("lnk");
                        cat.Items.Add(e);
                        pos = end;
                    }
                    if (pos + 4 <= d.Length && BitConverter.ToUInt32(d, pos) == Footer) pos += 4;
                    if (type != 1) cats.Add(cat);
                }
                return cats;
            }
            catch
            {
                // Структура не совпала с ожидаемой — ищем ярлыки по сигнатуре.
                return Scan(d, cats);
            }
        }

        // Запасной путь: ищем заголовки .lnk по сигнатуре и складываем всё в одну безымянную категорию.
        private static List<CustomCategory> Scan(byte[] d, List<CustomCategory> partial)
        {
            byte[] sig = { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46 };
            var cat = new CustomCategory();
            for (int i = 0; i + sig.Length <= d.Length; i++)
            {
                if (!d.AsSpan(i, sig.Length).SequenceEqual(sig)) continue;
                var e = Lnk.Parse(d, i, out int end);
                if (e != null) { cat.Items.Add(e); i = Math.Max(i, end - 1); }
            }
            return cat.Items.Count > 0 ? new List<CustomCategory> { cat } : partial;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

        private static string ResolveIndirect(string s)
        {
            if (!s.StartsWith("@")) return s;
            try { var sb = new StringBuilder(512); return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : s; }
            catch { return s; }
        }
    }

    // Разбор Shell Link (MS-SHLLINK) из массива: цель, аргументы, рабочая папка, заголовок (System.Title).
    internal static class Lnk
    {
        private static readonly Guid FmtTitle = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SHGetPathFromIDListEx(IntPtr pidl, StringBuilder pszPath, int cchPath, int uOpts);

        public static CustomEntry? Parse(byte[] d, int start, out int end)
        {
            end = start;
            if (start + 0x4C > d.Length || BitConverter.ToUInt32(d, start) != 0x4C) return null;
            uint flags = BitConverter.ToUInt32(d, start + 0x14);
            bool unicode = (flags & 0x80) != 0;
            int pos = start + 0x4C;
            var e = new CustomEntry();

            if ((flags & 0x01) != 0) // LinkTargetIDList
            {
                int idSize = BitConverter.ToUInt16(d, pos);
                e.Target = PathFromIdList(d, pos + 2, idSize);
                pos += 2 + idSize;
            }
            if ((flags & 0x02) != 0) // LinkInfo
            {
                int liSize = BitConverter.ToInt32(d, pos);
                if (e.Target.Length == 0) e.Target = PathFromLinkInfo(d, pos);
                pos += liSize;
            }

            string ReadStr()
            {
                int n = BitConverter.ToUInt16(d, pos); pos += 2;
                int bytes = unicode ? n * 2 : n;
                string s = unicode ? Encoding.Unicode.GetString(d, pos, bytes) : Encoding.Latin1.GetString(d, pos, bytes);
                pos += bytes;
                return s;
            }
            if ((flags & 0x04) != 0) e.Description = ReadStr();
            if ((flags & 0x08) != 0) ReadStr();               // относительный путь
            if ((flags & 0x10) != 0) e.WorkingDir = ReadStr();
            if ((flags & 0x20) != 0) e.Arguments = ReadStr();
            if ((flags & 0x40) != 0) ReadStr();               // иконка

            // ExtraData: блоки до терминатора (размер < 4).
            while (pos + 4 <= d.Length)
            {
                int size = BitConverter.ToInt32(d, pos);
                if (size < 4) { pos += 4; break; }
                if (pos + size > d.Length) break;
                uint sig = BitConverter.ToUInt32(d, pos + 4);
                if (sig == 0xA0000009) e.Title = TitleFromPropertyStore(d, pos + 8, pos + size);
                else if (sig == 0xA0000001 && e.Target.Length == 0 && size >= 8 + 260 + 520)
                    e.Target = Environment.ExpandEnvironmentVariables(Encoding.Unicode.GetString(d, pos + 8 + 260, 520).Split('\0')[0]);
                pos += size;
            }
            end = pos;
            return e;
        }

        private static string PathFromIdList(byte[] d, int ofs, int size)
        {
            if (size <= 0 || ofs + size > d.Length) return "";
            IntPtr mem = Marshal.AllocCoTaskMem(size + 2);
            try
            {
                Marshal.Copy(d, ofs, mem, size);
                Marshal.WriteInt16(mem, size, 0);
                var sb = new StringBuilder(1024);
                return SHGetPathFromIDListEx(mem, sb, sb.Capacity, 0) ? sb.ToString() : "";
            }
            catch { return ""; }
            finally { Marshal.FreeCoTaskMem(mem); }
        }

        private static string PathFromLinkInfo(byte[] d, int li)
        {
            try
            {
                int hdr = BitConverter.ToInt32(d, li + 4);
                int liFlags = BitConverter.ToInt32(d, li + 8);
                if ((liFlags & 1) == 0) return "";
                if (hdr >= 0x24)
                {
                    int u = BitConverter.ToInt32(d, li + 28), us = BitConverter.ToInt32(d, li + 32);
                    if (u > 0) return ZUni(d, li + u) + (us > 0 ? ZUni(d, li + us) : "");
                }
                int a = BitConverter.ToInt32(d, li + 16), sfx = BitConverter.ToInt32(d, li + 24);
                return ZAnsi(d, li + a) + (sfx > 0 ? ZAnsi(d, li + sfx) : "");
            }
            catch { return ""; }
        }

        private static string ZUni(byte[] d, int p) { int e = p; while (e + 1 < d.Length && (d[e] | d[e + 1]) != 0) e += 2; return Encoding.Unicode.GetString(d, p, e - p); }
        private static string ZAnsi(byte[] d, int p) { int e = p; while (e < d.Length && d[e] != 0) e++; return Encoding.Latin1.GetString(d, p, e - p); }

        // Сериализованное хранилище свойств: ищем System.Title (FMTID F29F85E0…, PID 2, VT_LPWSTR).
        private static string TitleFromPropertyStore(byte[] d, int p, int limit)
        {
            try
            {
                while (p + 24 <= limit)
                {
                    int storSize = BitConverter.ToInt32(d, p);
                    if (storSize <= 0 || p + storSize > limit) break;
                    var fmt = new Guid(new ReadOnlySpan<byte>(d, p + 8, 16));
                    if (fmt == FmtTitle)
                    {
                        int v = p + 24;
                        while (v + 13 <= p + storSize)
                        {
                            int vsize = BitConverter.ToInt32(d, v);
                            if (vsize <= 0) break;
                            int id = BitConverter.ToInt32(d, v + 4);
                            int vt = BitConverter.ToUInt16(d, v + 9);
                            if (id == 2 && vt == 0x1F)
                            {
                                int chars = BitConverter.ToInt32(d, v + 13);
                                if (chars > 0 && v + 17 + chars * 2 <= limit)
                                    return Encoding.Unicode.GetString(d, v + 17, chars * 2).TrimEnd('\0');
                            }
                            v += vsize;
                        }
                    }
                    p += storSize;
                }
            }
            catch { }
            return "";
        }
    }
}
