using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PlankWin
{
    // Пункт меню «Недавнее».
    public sealed class RecentItem
    {
        public string Path = string.Empty;
        public string Label = string.Empty;   // что показывать (без расширения)
        public string? Note;                  // пометка: «сессия», «открыт»
        public bool IsOpenNow;                // уже открыт в приложении — клик активирует окно
        public IntPtr Window;                 // окно, где открыт
        public string Arguments = string.Empty; // пункт-команда (custom jump list): exe + аргументы
        public string WorkingDir = string.Empty;
        public string? Group;                 // заголовок секции («Часто посещаемые»…)
        public bool IsTask;                   // задача программы («Новая вкладка») — идёт в основное меню, не в «Недавнее»
        public string? Tooltip;
        public bool IsCommand => Arguments.Length > 0;
        // Ключ для закрепления: путь, а у команд — путь + аргументы.
        public string Key => IsCommand ? Path + "\u001F" + Arguments : Path;
        public static RecentItem FromKey(string key)
        {
            int i = key.IndexOf('\u001F');
            var it = i < 0 ? new RecentItem { Path = key } : new RecentItem { Path = key.Substring(0, i), Arguments = key.Substring(i + 1) };
            it.Label = RecentDocs.LabelOf(it.Path);
            return it;
        }
        // Сетевые пути не проверяем: недоступная шара подвесила бы меню на десятки секунд.
        public bool Exists => IsUrl || RecentDocs.IsNetworkPath(Path) || File.Exists(Path) || Directory.Exists(Path);
        public bool IsUrl => Path.Contains("://");
    }

    // Источник недавних документов для конкретного приложения.
    public interface IRecentProvider
    {
        bool CanHandle(DockAppConfig app);
        List<RecentItem> Get(DockAppConfig app);
    }

    public static class RecentDocs
    {
        public const int MaxItems = 12;

        // Порядок важен: специализированные провайдеры раньше общего jump list.
        private static readonly IRecentProvider[] Providers = { new TiaPortalRecentProvider(), new OfficeRecentProvider(), new JumpListRecentProvider() };

        public static IRecentProvider? FindProvider(DockAppConfig app)
        {
            if (app == null || app.IsFavorites) return null;
            foreach (var p in Providers)
            {
                try { if (p.CanHandle(app)) return p; } catch { }
            }
            return null;
        }

        public static List<RecentItem> Get(DockAppConfig app)
        {
            var p = FindProvider(app);
            if (p == null) return new List<RecentItem>();
            try { return p.Get(app); } catch { return new List<RecentItem>(); }
        }

        public static string LabelOf(string path)
        {
            if (path.Contains("://"))
            {
                // Облачный файл: последний сегмент URL без расширения, %20 → пробел.
                string last = path.TrimEnd('/').Split('/').Last();
                try { last = Uri.UnescapeDataString(last); } catch { }
                string n = System.IO.Path.GetFileNameWithoutExtension(last);
                return string.IsNullOrEmpty(n) ? path : n;
            }
            string trimmed = path.TrimEnd('\\');
            bool isDir = !IsNetworkPath(path) && Directory.Exists(path);
            string name = isDir ? System.IO.Path.GetFileName(trimmed) : System.IO.Path.GetFileNameWithoutExtension(trimmed);
            return string.IsNullOrEmpty(name) ? path : name;
        }

        private static readonly Dictionary<char, bool> _netDrive = new();
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\")) return true;
            if (path.Length < 2 || path[1] != ':') return false;
            char d = char.ToUpperInvariant(path[0]);
            if (!_netDrive.TryGetValue(d, out bool net))
            {
                try { net = new DriveInfo(d + ":\\").DriveType == DriveType.Network; } catch { net = false; }
                _netDrive[d] = net;
            }
            return net;
        }

        // Открыть документ: если файловая ассоциация ведёт в тот же exe — через оболочку
        // (как двойной клик), иначе явно этим exe (важно, когда стоят несколько версий программы).
        public static void Open(DockAppConfig app, string path)
        {
            // Облачные документы Office: через ms-excel:ofe|u|<url> (иначе оболочка откроет ссылку в браузере).
            if (path.Contains("://"))
            {
                string? scheme = OfficeRecentProvider.UriSchemeFor(app);
                string target = scheme != null ? scheme + ":ofe|u|" + path : path;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }
            string ext = System.IO.Path.GetExtension(path);
            string? assocExe = GetAssocExe(ext);
            bool sameExe = false;
            try
            {
                sameExe = assocExe != null && !string.IsNullOrEmpty(app.Path) && !app.IsPackaged &&
                          string.Equals(System.IO.Path.GetFullPath(assocExe), System.IO.Path.GetFullPath(app.Path), StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            var psi = sameExe || string.IsNullOrEmpty(app.Path) || app.IsPackaged || !app.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo(app.Path, "\"" + path + "\"")
                {
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(app.Path) ?? string.Empty
                };
            System.Diagnostics.Process.Start(psi);
        }

        // Запуск пункта: команды — exe с аргументами, документы — через Open.
        public static void Launch(DockAppConfig app, RecentItem it)
        {
            if (!it.IsCommand) { Open(app, it.Path); return; }
            string wd = it.WorkingDir.Length > 0 ? it.WorkingDir : (System.IO.Path.GetDirectoryName(it.Path) ?? string.Empty);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.Path, it.Arguments) { UseShellExecute = true, WorkingDirectory = wd });
        }

        public static void ShowInFolder(string path)
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            else
            {
                string? dir = System.IO.Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = System.IO.Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int AssocQueryString(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);

        private static string? GetAssocExe(string ext)
        {
            try
            {
                if (string.IsNullOrEmpty(ext)) return null;
                uint len = 1024;
                var sb = new StringBuilder((int)len);
                // ASSOCF_NOTRUNCATE = 0x20, ASSOCSTR_EXECUTABLE = 2
                if (AssocQueryString(0x20, 2, ext, null, sb, ref len) != 0) return null;
                string exe = sb.ToString();
                return File.Exists(exe) ? exe : null;
            }
            catch { return null; }
        }

        internal static string GetTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(1024);
            Win32Helper.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    // TIA Portal: %APPDATA%\Siemens\Automation\Portal V<n>\Settings\Settings.xml,
    // ProjectSettings → Setting "LastProjects" → StringArray → String*. Файл пишется при закрытии TIA,
    // поэтому открытый сейчас проект добираем из заголовка окна.
    public sealed class TiaPortalRecentProvider : IRecentProvider
    {
        private const string ExeName = "Siemens.Automation.Portal.exe";
        private static readonly Dictionary<string, (DateTime stamp, List<string> list)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex ProjExt = new(@"\.(ap|als|zap)\d+$", RegexOptions.IgnoreCase);

        public bool CanHandle(DockAppConfig app) =>
            string.Equals(System.IO.Path.GetFileName(app.Path), ExeName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(app.ProcessName, "Siemens.Automation.Portal", StringComparison.OrdinalIgnoreCase);

        public List<RecentItem> Get(DockAppConfig app)
        {
            var result = new List<RecentItem>();
            string? settings = FindSettings(app);
            var paths = settings != null ? Read(settings) : new List<string>();

            // Открытые сейчас проекты — из заголовков окон («Siemens  -  C:\...\Project»).
            foreach (var h in app.WindowHandles.ToList())
            {
                string? p = ProjectFromTitle(RecentDocs.GetTitle(h), paths);
                if (p == null || result.Any(r => string.Equals(r.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(Make(p, isOpen: true, h));
            }

            foreach (var p in paths)
            {
                if (result.Count >= RecentDocs.MaxItems) break;
                if (result.Any(r => string.Equals(r.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(Make(p, false, IntPtr.Zero));
            }
            return result;
        }

        private static RecentItem Make(string path, bool isOpen, IntPtr hwnd)
        {
            var it = new RecentItem { Path = path, Label = RecentDocs.LabelOf(path), IsOpenNow = isOpen, Window = hwnd };
            var notes = new List<string>();
            if (Regex.IsMatch(path, @"\.als\d+$", RegexOptions.IgnoreCase)) notes.Add(Loc.T("rc_session"));
            if (isOpen) notes.Add(Loc.T("rc_open"));
            if (notes.Count > 0) it.Note = string.Join(", ", notes);
            return it;
        }

        // Путь проекта из заголовка: берём всё от «X:\» и подбираем реальный файл
        // (TIA показывает путь без расширения).
        private static string? ProjectFromTitle(string title, List<string> known)
        {
            var m = Regex.Match(title ?? string.Empty, @"[A-Za-z]:\\[^<>|""*?]+");
            if (!m.Success) return null;
            string c = m.Value.Trim().TrimEnd('-', ' ', '*');
            if (File.Exists(c) && ProjExt.IsMatch(c)) return c;

            var k = known.FirstOrDefault(p => string.Equals(StripExt(p), c, StringComparison.OrdinalIgnoreCase));
            if (k != null) return k;

            try
            {
                string? dir = System.IO.Path.GetDirectoryName(c);
                string name = System.IO.Path.GetFileName(c);
                if (dir != null && Directory.Exists(dir))
                    return Directory.EnumerateFiles(dir, name + ".*").FirstOrDefault(f => ProjExt.IsMatch(f));
            }
            catch { }
            return null;
        }

        private static string StripExt(string p) => ProjExt.Replace(p, string.Empty);

        private static string? FindSettings(DockAppConfig app)
        {
            string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Siemens", "Automation");
            var m = Regex.Match(app.Path ?? string.Empty, @"\\(Portal V[\d.]+)\\", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string f = System.IO.Path.Combine(root, m.Groups[1].Value, "Settings", "Settings.xml");
                if (File.Exists(f)) return f;
            }
            // Версию не определили — берём самый свежий Settings.xml среди установленных версий.
            try
            {
                return Directory.EnumerateDirectories(root, "Portal V*")
                    .Select(d => System.IO.Path.Combine(d, "Settings", "Settings.xml"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static List<string> Read(string file)
        {
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            if (_cache.TryGetValue(file, out var c) && c.stamp == stamp) return c.list;

            var list = new List<string>();
            try
            {
                // Файл многомегабайтный: читаем потоком и выходим сразу после нужного узла.
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var xr = XmlReader.Create(fs, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, IgnoreWhitespace = true });
                while (xr.Read())
                {
                    if (xr.NodeType != XmlNodeType.Element || xr.Name != "Setting" || xr.GetAttribute("name") != "LastProjects") continue;
                    using var sub = xr.ReadSubtree();
                    sub.Read();
                    while (!sub.EOF)
                    {
                        // ReadElementContentAsString сам сдвигает курсор — без лишнего Read(), иначе потеряем каждую вторую строку.
                        if (sub.NodeType == XmlNodeType.Element && sub.Name == "String")
                        {
                            string s = sub.ReadElementContentAsString().Trim();
                            if (s.Length > 0 && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
                        }
                        else sub.Read();
                    }
                    break;
                }
            }
            catch { }
            _cache[file] = (stamp, list);
            return list;
        }
    }
}
