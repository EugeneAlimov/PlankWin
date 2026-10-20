using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PlankWin
{
    internal static class FileAssoc
    {
        // Программа по умолчанию для типа файла (полный путь к exe) или null.
        public static string? DefaultExe(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return null;
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

        private const int ASSOCSTR_EXECUTABLE = 2;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint AssocQueryString(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);
    }
}
