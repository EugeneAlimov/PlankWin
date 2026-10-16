using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PlankWin
{
    // Системные jump list'ы Windows: %APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations\<AppID>.automaticDestinations-ms.
    // Файл — OLE-контейнер (CFB); нам нужен только поток DestList: в нём путь, время и признак закрепления каждого пункта.
    // Имя файла = CRC64 от AppID приложения: явный AUMID (UWP, ярлык в Пуске) или путь к exe с подстановкой известных папок.
    public sealed class JumpListRecentProvider : IRecentProvider
    {
        private static readonly string AutoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\AutomaticDestinations");

        private static readonly Dictionary<string, (DateTime stamp, List<DestEntry> list)> _cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string CustomDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\CustomDestinations");
        private static readonly Dictionary<string, (DateTime stamp, List<CustomCategory> cats)> _customCache = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxPerGroup = 10;

        public bool CanHandle(DockAppConfig app)
        {
            var ids = AppIdCandidates(app).ToList();
            return FindFile(ids, AutoDir, ".automaticDestinations-ms") != null || FindFile(ids, CustomDir, ".customDestinations-ms") != null;
        }

        public List<RecentItem> Get(DockAppConfig app)
        {
            var result = new List<RecentItem>();
            var ids = AppIdCandidates(app).ToList();
            bool Has(string path, string args) => result.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase) && r.Arguments == args);

            // 1) Системный список (automatic): документы/папки, открывавшиеся программой.
            string? auto = FindFile(ids, AutoDir, ".automaticDestinations-ms");
            if (auto != null)
            {
                var entries = Read(auto)
                    .Where(e => IsFileSystemPath(e.Path))
                    .OrderByDescending(e => e.Pinned)          // закреплённые в самой Windows — первыми
                    .ThenByDescending(e => e.Time);
                foreach (var e in entries)
                {
                    if (result.Count >= RecentDocs.MaxItems) break;
                    if (Has(e.Path, "")) continue;
                    var it = new RecentItem { Path = e.Path, Label = RecentDocs.LabelOf(e.Path) };
                    if (!e.Pinned && !it.Exists) continue; // удалённое Windows помнит долго — не засоряем список
                    result.Add(it);
                }
            }

            // 2) Свои категории программы (custom): «Недавние» VS Code, «Часто посещаемые» браузеров, задачи.
            string? custom = FindFile(ids, CustomDir, ".customDestinations-ms");
            if (custom != null)
            {
                var cats = ReadCustom(custom);
                foreach (var cat in cats.OrderBy(c => c.IsTasks)) // задачи — в самом низу
                {
                    string group = cat.IsTasks ? Loc.T("rc_tasks") : (cat.Name.Length > 0 ? cat.Name : Loc.T("m_recent"));
                    int n = 0;
                    foreach (var ce in cat.Items)
                    {
                        if (n >= MaxPerGroup) break;
                        if (ce.Target.Length == 0 || Has(ce.Target, ce.Arguments)) continue;
                        string label = ce.Title.Length > 0 ? ce.Title : ce.Description.Length > 0 ? ce.Description : RecentDocs.LabelOf(ce.Target);
                        result.Add(new RecentItem
                        {
                            Path = ce.Target, Arguments = ce.Arguments, WorkingDir = ce.WorkingDir,
                            Label = label, Group = group, IsTask = cat.IsTasks,
                            Tooltip = ce.Description.Length > 0 && ce.Description != label ? ce.Description : null
                        });
                        n++;
                    }
                }
            }
            return result;
        }

        private static bool IsFileSystemPath(string p) =>
            p.Length > 3 && ((char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\') || p.StartsWith(@"\\"));

        // ---------------- Поиск файла jump list ----------------

        private static string? FindFile(List<string> ids, string dir, string ext)
        {
            if (!Directory.Exists(dir)) return null;
            string? best = null; DateTime bestTime = DateTime.MinValue;
            foreach (var id in ids)
            {
                string f = Path.Combine(dir, AppIdHash(id) + ext);
                if (!File.Exists(f)) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (best == null || t > bestTime) { best = f; bestTime = t; } // несколько кандидатов — берём самый живой
            }
            return best;
        }

        private static List<CustomCategory> ReadCustom(string file)
        {
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            if (_customCache.TryGetValue(file, out var c) && c.stamp == stamp) return c.cats;
            var cats = new List<CustomCategory>();
            try
            {
                byte[] raw = ReadAllShared(file);
                cats = CustomDestinations.Parse(raw);
            }
            catch { }
            _customCache[file] = (stamp, cats);
            return cats;
        }

        private static byte[] ReadAllShared(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var raw = new byte[fs.Length];
            int n = 0; while (n < raw.Length) { int r = fs.Read(raw, n, raw.Length - n); if (r <= 0) break; n += r; }
            return raw;
        }

        private static IEnumerable<string> AppIdCandidates(DockAppConfig app)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = app.Path ?? string.Empty;

            // 1) UWP: shell:AppsFolder\<AUMID>
            if (app.IsPackaged) { string a = path.Substring("shell:AppsFolder\\".Length); if (seen.Add(a)) yield return a; }

            // 2) Проводник: явный AUMID
            if (string.Equals(Path.GetFileName(path), "explorer.exe", StringComparison.OrdinalIgnoreCase))
                if (seen.Add("Microsoft.Windows.Explorer")) yield return "Microsoft.Windows.Explorer";

            // 3) AUMID запущенных окон
            foreach (var h in app.WindowHandles.ToList().Take(3))
            {
                string a = string.Empty;
                try { a = Win32Helper.GetAumidFromWindow(h); } catch { }
                if (!string.IsNullOrEmpty(a) && seen.Add(a)) yield return a;
            }

            if (app.IsPackaged || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) yield break;

            // 4) AUMID из ярлыка в меню Пуск, указывающего на этот exe (Office, Chrome, VS Code…)
            foreach (var a in StartMenuAumids.For(path))
                if (seen.Add(a)) yield return a;

            // 5) Вычисляемый AppID: путь с подстановкой известной папки, и «сырой» путь
            string kf = KnownFolderForm(path);
            if (seen.Add(kf)) yield return kf;
            if (seen.Add(path)) yield return path;
        }

        // Windows заменяет начало пути на GUID известной папки (самое длинное совпадение).
        private static readonly (string guid, Func<string> dir)[] KnownFolders =
        {
            ("{6D809377-6AF0-444B-8957-A3773F02200E}", () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("{6365D5A7-0F0D-45E5-87F6-0DA56B6A4F7D}", () => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles)),
            ("{DE974D24-D9C6-4D3E-BF91-F4455120B917}", () => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)),
            ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", () => Environment.GetFolderPath(Environment.SpecialFolder.System)),
            ("{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", () => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)),
            ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", () => Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            ("{5CD7AEE2-2219-4A67-B85D-6C9CE15660CB}", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")),
            ("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}", () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("{5E6C858F-0E22-4760-9AFE-EA3317B67173}", () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        };

        private static string KnownFolderForm(string path)
        {
            string? bestGuid = null; int bestLen = 0;
            foreach (var (guid, dirF) in KnownFolders)
            {
                string d;
                try { d = dirF().TrimEnd('\\'); } catch { continue; }
                if (d.Length == 0 || d.Length <= bestLen) continue;
                if (path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) { bestGuid = guid; bestLen = d.Length; }
            }
            return bestGuid == null ? path : bestGuid + path.Substring(bestLen);
        }

        // CRC-64 (полином 0x92C64265D32139A4, init ~0) от UTF-16LE строки в верхнем регистре.
        // Проверено: "Microsoft.Windows.Explorer" → f01b4d95cf55d32a, "{System}\notepad.exe" → 9b9cdc69c1c24e2b.
        public static string AppIdHash(string appId)
        {
            ulong crc = ulong.MaxValue;
            foreach (byte b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x92C64265D32139A4UL : crc >> 1;
            }
            return crc.ToString("x16");
        }

        // ---------------- DestList ----------------

        private sealed class DestEntry { public string Path = ""; public DateTime Time; public bool Pinned; }

        private static List<DestEntry> Read(string file)
        {
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            if (_cache.TryGetValue(file, out var c) && c.stamp == stamp) return c.list;
            var list = new List<DestEntry>();
            try
            {
                byte[] raw = ReadAllShared(file);
                byte[]? dl = CompoundFile.ReadStream(raw, "DestList");
                if (dl != null) list = ParseDestList(dl);
            }
            catch { }
            _cache[file] = (stamp, list);
            return list;
        }

        private static List<DestEntry> ParseDestList(byte[] d)
        {
            var list = new List<DestEntry>();
            if (d.Length < 32) return list;
            int version = BitConverter.ToInt32(d, 0);
            int count = BitConverter.ToInt32(d, 4);
            bool v3 = version >= 2;         // Win10/11: длинная запись
            int pathLenOfs = v3 ? 128 : 112;
            int pos = 32;

            for (int i = 0; i < count && pos + pathLenOfs + 2 <= d.Length; i++)
            {
                int len = BitConverter.ToUInt16(d, pos + pathLenOfs);
                int strPos = pos + pathLenOfs + 2;
                if (len == 0 || strPos + len * 2 > d.Length) break;

                var e = new DestEntry { Path = Encoding.Unicode.GetString(d, strPos, len * 2).TrimEnd('\0') };
                e.Time = FileTimeAt(d, pos + 100) ?? FileTimeAt(d, pos + 96) ?? DateTime.MinValue;
                e.Pinned = BitConverter.ToInt32(d, pos + 108) >= 0;
                list.Add(e);

                pos = strPos + len * 2 + (v3 ? 4 : 0);
            }
            return list;
        }

        private static DateTime? FileTimeAt(byte[] d, int ofs)
        {
            if (ofs + 8 > d.Length) return null;
            long ft = BitConverter.ToInt64(d, ofs);
            if (ft <= 0) return null;
            try
            {
                var t = DateTime.FromFileTimeUtc(ft);
                return t.Year >= 2000 && t.Year <= 2100 ? t : null;
            }
            catch { return null; }
        }
    }

    // Минимальный читатель OLE Compound File (CFB v3/v4): только чтение потока по имени из корня.
    internal static class CompoundFile
    {
        private const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF;

        public static byte[]? ReadStream(byte[] f, string name)
        {
            if (f.Length < 512 || BitConverter.ToUInt64(f, 0) != 0xE11AB1A1E011CFD0UL) return null;
            int secSize = 1 << BitConverter.ToUInt16(f, 0x1E);
            int miniSize = 1 << BitConverter.ToUInt16(f, 0x20);
            uint dirStart = BitConverter.ToUInt32(f, 0x30);
            uint miniCutoff = BitConverter.ToUInt32(f, 0x38);
            uint miniFatStart = BitConverter.ToUInt32(f, 0x3C);
            uint difatStart = BitConverter.ToUInt32(f, 0x44);
            int difatCount = BitConverter.ToInt32(f, 0x48);

            long Off(uint sid) => (long)(sid + 1) * secSize;

            // FAT: сектора перечислены в DIFAT (109 в заголовке + цепочка DIFAT-секторов)
            var fatSectors = new List<uint>();
            for (int i = 0; i < 109; i++) { uint s = BitConverter.ToUInt32(f, 0x4C + i * 4); if (s != FreeSect) fatSectors.Add(s); }
            uint ds = difatStart; int guard = 0;
            while (ds != EndOfChain && ds != FreeSect && guard++ < difatCount + 1 && Off(ds) + secSize <= f.Length)
            {
                int per = secSize / 4 - 1;
                for (int i = 0; i < per; i++) { uint s = BitConverter.ToUInt32(f, (int)Off(ds) + i * 4); if (s != FreeSect) fatSectors.Add(s); }
                ds = BitConverter.ToUInt32(f, (int)Off(ds) + per * 4);
            }
            var fat = new List<uint>();
            foreach (var s in fatSectors)
            {
                if (Off(s) + secSize > f.Length) break;
                for (int i = 0; i < secSize / 4; i++) fat.Add(BitConverter.ToUInt32(f, (int)Off(s) + i * 4));
            }

            byte[] Chain(uint start)
            {
                var ms = new MemoryStream();
                uint s = start; int g = 0;
                while (s != EndOfChain && s < fat.Count && g++ < fat.Count && Off(s) + secSize <= f.Length)
                {
                    ms.Write(f, (int)Off(s), secSize);
                    s = fat[(int)s];
                }
                return ms.ToArray();
            }

            byte[] dir = Chain(dirStart);
            if (dir.Length < 128) return null;
            uint rootStart = BitConverter.ToUInt32(dir, 116);

            for (int ofs = 0; ofs + 128 <= dir.Length; ofs += 128)
            {
                int nameLen = BitConverter.ToUInt16(dir, ofs + 64);
                if (dir[ofs + 66] != 2 || nameLen < 2 || nameLen > 64) continue; // 2 = поток
                string n = Encoding.Unicode.GetString(dir, ofs, nameLen - 2);
                if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;

                uint start = BitConverter.ToUInt32(dir, ofs + 116);
                int size = (int)BitConverter.ToUInt32(dir, ofs + 120);

                if (size >= miniCutoff)
                {
                    byte[] all = Chain(start);
                    return all.Length >= size ? all.AsSpan(0, size).ToArray() : all;
                }

                // Маленький поток лежит в мини-потоке (данные корня), адресация через MiniFAT.
                byte[] miniStream = Chain(rootStart);
                byte[] miniFatRaw = Chain(miniFatStart);
                var res = new MemoryStream();
                uint m = start; int g2 = 0;
                while (m != EndOfChain && res.Length < size && g2++ < 1_000_000)
                {
                    long mo = (long)m * miniSize;
                    if (mo + miniSize > miniStream.Length) break;
                    res.Write(miniStream, (int)mo, miniSize);
                    if ((m + 1) * 4 > miniFatRaw.Length) break;
                    m = BitConverter.ToUInt32(miniFatRaw, (int)m * 4);
                }
                var r = res.ToArray();
                return r.Length >= size ? r.AsSpan(0, size).ToArray() : r;
            }
            return null;
        }
    }

    // Карта «exe → AUMID» по ярлыкам меню Пуск. Строится один раз в фоне (STA-поток, COM ShellLink).
    internal static class StartMenuAumids
    {
        private static Dictionary<string, List<string>>? _map;
        private static readonly ManualResetEventSlim _ready = new(false);
        private static int _started;

        public static void Warmup()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            var t = new Thread(Build) { IsBackground = true, Name = "StartMenuAumids" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public static int Count => _map?.Count ?? -1;

        public static IEnumerable<string> For(string exePath)
        {
            Warmup();
            _ready.Wait(400); // в первый раз даём немного времени, дальше — мгновенно
            var map = _map;
            if (map != null && map.TryGetValue(exePath, out var l)) return l;
            return Array.Empty<string>();
        }

        // Через Shell.Application/WScript.Shell — тот же путь, что в проверочном PowerShell-скрипте
        // (прямое чтение IPropertyStore ярлыка на этой системе отдавало пустоту).
        private static void Build()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int seen = 0, withId = 0;
            try
            {
                var shType = Type.GetTypeFromProgID("Shell.Application");
                var wsType = Type.GetTypeFromProgID("WScript.Shell");
                if (shType == null || wsType == null) throw new InvalidOperationException("no Shell.Application");
                dynamic sh = Activator.CreateInstance(shType)!;
                dynamic ws = Activator.CreateInstance(wsType)!;

                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                };
                foreach (var root in roots.Where(Directory.Exists))
                    foreach (var dir in new[] { root }.Concat(Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })))
                    {
                        string[] lnks;
                        try { lnks = Directory.GetFiles(dir, "*.lnk"); } catch { continue; }
                        if (lnks.Length == 0) continue;
                        dynamic? folder = null;
                        try { folder = sh.NameSpace(dir); } catch { }
                        if (folder == null) continue;

                        foreach (var lnk in lnks)
                        {
                            seen++;
                            try
                            {
                                dynamic? item = folder.ParseName(Path.GetFileName(lnk));
                                if (item == null) continue;
                                string aumid = (item.ExtendedProperty("System.AppUserModel.ID") as string) ?? "";
                                if (aumid.Length == 0) continue;
                                string target = (ws.CreateShortcut(lnk).TargetPath as string) ?? "";
                                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                                withId++;
                                if (!map.TryGetValue(target, out var l)) map[target] = l = new List<string>();
                                if (!l.Contains(aumid, StringComparer.OrdinalIgnoreCase)) l.Add(aumid);
                            }
                            catch { }
                        }
                    }
            }
            catch { }
            _map = map;
            _ready.Set();
        }
    }
}
