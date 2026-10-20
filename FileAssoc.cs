using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PlankWin
{
    // Какие программы умеют открывать тип файла — тот же список, что «Открыть с помощью» в Проводнике.
    internal static class FileAssoc
    {
        private static readonly Dictionary<string, HashSet<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Имена exe (без пути: версии и пути установки у одной программы бывают разные).
        public static HashSet<string> HandlerExeNames(string ext)
        {
            if (_cache.TryGetValue(ext, out var cached)) return cached;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (SHAssocEnumHandlers(ext, ASSOC_FILTER_NONE, out var en) == 0 && en != null)
                {
                    var arr = new IAssocHandler[1];
                    while (en.Next(1, arr, out uint got) == 0 && got == 1)
                    {
                        try
                        {
                            if (arr[0].GetName(out var name) == 0 && !string.IsNullOrEmpty(name))
                                set.Add(Path.GetFileName(name.Trim('"')));
                        }
                        catch { }
                        finally { try { Marshal.ReleaseComObject(arr[0]); } catch { } }
                    }
                    Marshal.ReleaseComObject(en);
                }
            }
            catch { }
            string? def = DefaultExe(ext);
            if (def != null) set.Add(Path.GetFileName(def));
            _cache[ext] = set;
            return set;
        }

        public static bool Supports(string ext, string exePath) =>
            !string.IsNullOrEmpty(ext) && HandlerExeNames(ext).Contains(Path.GetFileName(exePath));

        // Программа по умолчанию для типа (полный путь к exe) или null.
        public static string? DefaultExe(string ext)
        {
            try
            {
                uint len = 1024;
                var sb = new StringBuilder((int)len);
                if (AssocQueryString(0, ASSOCSTR_EXECUTABLE, ext, "open", sb, ref len) == 0)
                {
                    string p = sb.ToString();
                    if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        private const int ASSOC_FILTER_NONE = 0;
        private const int ASSOCSTR_EXECUTABLE = 2;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHAssocEnumHandlers(string pszExtra, int afFilter, out IEnumAssocHandlers ppEnumHandler);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint AssocQueryString(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);

        [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumAssocHandlers
        {
            [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IAssocHandler[] rgelt, out uint pceltFetched);
        }

        [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAssocHandler
        {
            [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
            [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
            [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string ppszPath, out int pIndex);
            [PreserveSig] int IsRecommended();
            [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
            [PreserveSig] int Invoke(IntPtr pdo);
            [PreserveSig] int CreateInvoker(IntPtr pdo, out IntPtr ppInvoker);
        }
    }
}
