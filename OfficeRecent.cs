using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PlankWin
{
    // Office: собственный MRU в реестре — полнее jump list'а (облачные файлы OneDrive/SharePoint, закреплённые в Office).
    // HKCU\Software\Microsoft\Office\<ver>\<App>\User MRU\<учётка>\File MRU  (и старый ...\<App>\File MRU)
    // Значения "Item N" = "[F00000000][T01DA…][O00000000]*C:\путь\файл.xlsx"; F — флаги (бит 0 = закреплён), T — FILETIME.
    // Поверх добавляем jump list Windows: там бывают файлы, открытые не из самого Office.
    public sealed class OfficeRecentProvider : IRecentProvider
    {
        private static readonly Dictionary<string, (string regApp, string scheme, string[] exts)> Apps = new(StringComparer.OrdinalIgnoreCase)
        {
            ["WINWORD.EXE"] = ("Word", "ms-word", new[] { ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".rtf", ".odt" }),
            ["EXCEL.EXE"] = ("Excel", "ms-excel", new[] { ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm", ".csv", ".ods" }),
            ["POWERPNT.EXE"] = ("PowerPoint", "ms-powerpoint", new[] { ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".ppsm", ".pot", ".potx", ".odp" }),
            ["VISIO.EXE"] = ("Visio", "ms-visio", new[] { ".vsd", ".vsdx", ".vsdm", ".vss", ".vssx", ".vst", ".vstx" }),
            ["MSACCESS.EXE"] = ("Access", "ms-access", new[] { ".accdb", ".mdb", ".accde", ".accdr" }),
            ["MSPUB.EXE"] = ("Publisher", "ms-publisher", new[] { ".pub" }),
        };

        // Папка ярлыков Office — общая для всех программ: отсюда Backstage берёт «Последние» для локальных файлов.
        private static readonly string OfficeRecentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Office\Recent");
        private static readonly Dictionary<string, (DateTime stamp, string target)> _lnkCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex ItemRx = new(@"^\[F([0-9A-Fa-f]+)\]\[T([0-9A-Fa-f]+)\](?:\[O[0-9A-Fa-f]+\])?\*(.+)$");
        private readonly JumpListRecentProvider _jump = new();

        public bool CanHandle(DockAppConfig app) => Apps.ContainsKey(Path.GetFileName(app.Path ?? ""));

        // Схема ms-excel:/ms-word: для облачных файлов (по https), иначе null.
        public static string? UriSchemeFor(DockAppConfig app) =>
            Apps.TryGetValue(Path.GetFileName(app.Path ?? ""), out var a) ? a.scheme : null;

        public List<RecentItem> Get(DockAppConfig app)
        {
            var result = new List<RecentItem>();
            if (!Apps.TryGetValue(Path.GetFileName(app.Path ?? ""), out var info)) return result;

            var all = ReadMru(info.regApp);
            foreach (var r in ReadRecentFolder(info.exts))
            {
                int i = all.FindIndex(e => string.Equals(e.path, r.path, StringComparison.OrdinalIgnoreCase));
                if (i < 0) all.Add(r);
                else if (r.time > all[i].time) all[i] = (all[i].path, r.time, all[i].pinned);
            }

            foreach (var e in all.OrderByDescending(e => e.pinned).ThenByDescending(e => e.time))
            {
                if (result.Count >= RecentDocs.MaxItems) break;
                var it = new RecentItem { Path = e.path, Label = RecentDocs.LabelOf(e.path) };
                if (!e.pinned && !it.Exists) continue;
                result.Add(it);
            }

            // Дополняем jump list'ом (без дублей), не превышая общий лимит.
            try
            {
                foreach (var j in _jump.CanHandle(app) ? _jump.Get(app) : new List<RecentItem>())
                {
                    bool dup = result.Any(r => string.Equals(r.Path, j.Path, StringComparison.OrdinalIgnoreCase) && r.Arguments == j.Arguments);
                    if (j.Group != null) { if (!dup) result.Add(j); continue; }   // свои категории/задачи Office — без повторов
                    if (result.Count(r => r.Group == null) >= RecentDocs.MaxItems) continue;
                    if (dup) continue;
                    result.Insert(result.Count(r => r.Group == null), j);
                }
            }
            catch { }
            return result;
        }

        private static List<(string path, DateTime time, bool pinned)> ReadRecentFolder(string[] exts)
        {
            var list = new List<(string path, DateTime time, bool pinned)>();
            try
            {
                if (!Directory.Exists(OfficeRecentDir)) return list;
                foreach (var lnk in Directory.EnumerateFiles(OfficeRecentDir, "*.lnk"))
                {
                    // Быстрый отсев по имени ярлыка («Книга.xlsx.LNK»), не открывая файл.
                    string inner = Path.GetExtension(Path.GetFileNameWithoutExtension(lnk));
                    if (!exts.Contains(inner, StringComparer.OrdinalIgnoreCase)) continue;

                    DateTime stamp = File.GetLastWriteTimeUtc(lnk);
                    if (!_lnkCache.TryGetValue(lnk, out var c) || c.stamp != stamp)
                    {
                        string target = "";
                        try { target = Lnk.Parse(File.ReadAllBytes(lnk), 0, out _)?.Target ?? ""; }
                        catch { }
                        _lnkCache[lnk] = c = (stamp, target);
                    }
                    if (c.target.Length > 0 && exts.Contains(Path.GetExtension(c.target), StringComparer.OrdinalIgnoreCase))
                        list.Add((c.target, stamp, false));
                }
            }
            catch { }
            return list;
        }

        private static List<(string path, DateTime time, bool pinned)> ReadMru(string regApp)
        {
            var map = new Dictionary<string, (string path, DateTime time, bool pinned)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var office = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office");
                if (office == null) return new();
                foreach (var ver in office.GetSubKeyNames().Where(v => Regex.IsMatch(v, @"^\d+\.\d+$")))
                {
                    using var appKey = office.OpenSubKey(ver + "\\" + regApp);
                    if (appKey == null) continue;

                    var mruKeys = new List<RegistryKey?> { appKey.OpenSubKey("File MRU") };
                    using (var user = appKey.OpenSubKey("User MRU"))
                        if (user != null)
                            foreach (var acc in user.GetSubKeyNames())
                                mruKeys.Add(user.OpenSubKey(acc + "\\File MRU"));

                    foreach (var k in mruKeys)
                    {
                        if (k == null) continue;
                        using (k)
                            foreach (var name in k.GetValueNames().Where(n => n.StartsWith("Item ", StringComparison.OrdinalIgnoreCase)))
                            {
                                if (k.GetValue(name) is not string v) continue;
                                var m = ItemRx.Match(v);
                                if (!m.Success) continue;
                                string path = m.Groups[3].Value.Trim();
                                bool pinned = (Convert.ToUInt32(m.Groups[1].Value, 16) & 1) != 0;
                                DateTime time = DateTime.MinValue;
                                try { time = DateTime.FromFileTimeUtc(Convert.ToInt64(m.Groups[2].Value, 16)); } catch { }
                                if (!map.TryGetValue(path, out var old) || time > old.time)
                                    map[path] = (path, time, pinned || (map.TryGetValue(path, out var o2) && o2.pinned));
                            }
                    }
                }
            }
            catch { }
            return map.Values.ToList();
        }
    }
}
