using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlankWin
{
    // Интернет-ярлыки лаунчеров (.url): Steam (steam://rungameid/ID), Epic (com.epicgames.launcher://apps/...).
    // Внутри ярлыка нет exe — только URI. Находим папку установки и exe игры через манифесты лаунчера,
    // чтобы закреплённая кнопка склеивалась с запущенным процессом. Запуск всё равно идёт через URI
    // (оверлей, DRM, облачные сохранения лаунчера продолжают работать).
    public static class UrlShortcutResolver
    {
        public sealed class Result
        {
            public string Uri = string.Empty;        // чем запускать
            public string ExePath = string.Empty;    // exe игры (может быть пусто, если не нашли)
            public string InstallDir = string.Empty; // папка установки (для склейки с процессом)
            public string IconFile = string.Empty;   // IconFile= из ярлыка
        }

        // Кэш: Resolve зовётся на каждом DragOver, а поиск exe обходит папку игры —
        // без кэша обработка отстаёт от курсора и заглушка встаёт на слот позади.
        private static readonly System.Collections.Generic.Dictionary<string, (DateTime stamp, Result? res)> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static Result? Resolve(string urlFile)
        {
            try
            {
                if (!File.Exists(urlFile)) return null;
                DateTime stamp = File.GetLastWriteTimeUtc(urlFile);
                if (_cache.TryGetValue(urlFile, out var c) && c.stamp == stamp) return c.res;
                var res = ResolveCore(urlFile);
                _cache[urlFile] = (stamp, res);
                return res;
            }
            catch { return null; }
        }

        private static Result? ResolveCore(string urlFile)
        {
            try
            {
                var r = new Result();
                foreach (var raw in File.ReadAllLines(urlFile))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) r.Uri = line.Substring(4).Trim();
                    else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) r.IconFile = line.Substring(9).Trim();
                }
                if (string.IsNullOrEmpty(r.Uri)) return null;

                var steam = Regex.Match(r.Uri, @"^steam://rungameid/(\d+)", RegexOptions.IgnoreCase);
                if (steam.Success) ResolveSteam(steam.Groups[1].Value, r);
                else if (r.Uri.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase)) ResolveEpic(r.Uri, r);

                return r;
            }
            catch { return null; }
        }

        // ---------------- Steam ----------------
        private static void ResolveSteam(string appId, Result r)
        {
            string? root = GetSteamRoot();
            if (string.IsNullOrEmpty(root)) return;

            var libs = new System.Collections.Generic.List<string> { root };
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!libs.Any(l => string.Equals(l.TrimEnd('\\', '/'), p.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                        libs.Add(p);
                }
            }

            foreach (var lib in libs)
            {
                string acf = Path.Combine(lib, "steamapps", "appmanifest_" + appId + ".acf");
                if (!File.Exists(acf)) continue;
                var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                string dir = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
                if (!Directory.Exists(dir)) continue;
                r.InstallDir = dir;
                r.ExePath = GuessMainExe(dir, m.Groups[1].Value);
                return;
            }
        }

        private static string? GetSteamRoot()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (k?.GetValue("SteamPath") is string sp && Directory.Exists(sp)) return sp.Replace('/', '\\');
            }
            catch { }
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (k?.GetValue("InstallPath") is string ip && Directory.Exists(ip)) return ip;
            }
            catch { }
            return null;
        }

        // Главный exe игры: совпадение имени с папкой, иначе самый крупный exe (без утилит/инсталляторов).
        private static string GuessMainExe(string dir, string folderName)
        {
            string[] junk = { "unins", "crash", "setup", "redist", "vcredist", "vc_redist", "dxsetup", "dotnet", "ue4prereq", "uninstall", "report", "helper", "easyanticheat", "battleye" };
            var exes = Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(f => !junk.Any(j => Path.GetFileName(f).Contains(j, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (exes.Count == 0) return string.Empty;

            string norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            string want = norm(folderName);
            var byName = exes.FirstOrDefault(f => norm(Path.GetFileNameWithoutExtension(f)) == want);
            if (byName != null) return byName;

            return exes.OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }).First();
        }

        // ---------------- Epic ----------------
        private static void ResolveEpic(string uri, Result r)
        {
            // com.epicgames.launcher://apps/<namespace>%3A<catalogId>%3A<AppName>?action=launch
            string body = uri.Substring(uri.IndexOf("apps/", StringComparison.OrdinalIgnoreCase) + 5);
            int q = body.IndexOf('?'); if (q >= 0) body = body.Substring(0, q);
            body = Uri.UnescapeDataString(body);
            string appName = body.Split(':').Last();

            string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(manifests)) return;

            foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("AppName", out var an) || !string.Equals(an.GetString(), appName, StringComparison.OrdinalIgnoreCase)) continue;
                    string loc = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() ?? "" : "";
                    string exe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() ?? "" : "";
                    if (Directory.Exists(loc)) r.InstallDir = loc;
                    if (!string.IsNullOrEmpty(loc) && !string.IsNullOrEmpty(exe))
                    {
                        string full = Path.Combine(loc, exe);
                        if (File.Exists(full)) r.ExePath = full;
                    }
                    return;
                }
                catch { }
            }
        }
    }
}