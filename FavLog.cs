using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PlankWin
{
    // ВРЕМЕННЫЙ отладочный лог открытия/закрытия избранного. Пишет в %LocalAppData%\PlankWin\fav-debug.log.
    internal static class FavLog
    {
        private static readonly object _lock = new object();
        private static string? _path;

        public static string PathToLog => _path ??= Path.Combine(PlankConfig.GetDataDir(), "fav-debug.log");

        public static void W(string msg)
        {
            try
            {
                lock (_lock)
                {
                    var p = PathToLog;
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 5_000_000) { try { File.Delete(p + ".old"); } catch { } File.Move(p, p + ".old"); }
                    File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }

        // Кто вызвал: несколько кадров стека без системных.
        public static string Caller(int skip = 2, int take = 4)
        {
            try
            {
                var frames = new StackTrace(skip, false).GetFrames() ?? Array.Empty<StackFrame>();
                return string.Join(" <- ", frames
                    .Select(f => f.GetMethod())
                    .Where(m => m != null && m.DeclaringType != null && (m.DeclaringType.Namespace ?? "").StartsWith("PlankWin"))
                    .Take(take)
                    .Select(m => $"{m!.DeclaringType!.Name.Split('+')[0]}.{m.Name}"));
            }
            catch { return "?"; }
        }
    }
}
