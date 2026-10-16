using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace PlankWin
{
    // Экспорт/импорт всей конфигурации одним .zip: профили мониторов (settings_*, apps_*) + global.json + manifest.json.
    // Профили привязаны к имени монитора (\\.\DISPLAY1…). На другом ПК имена могут отличаться — тогда профиль
    // старого основного монитора переносится на текущий основной.
    public static class ConfigTransfer
    {
        private const string ManifestName = "manifest.json";

        private sealed class Manifest
        {
            public string App { get; set; } = "PlankWin";
            public int Version { get; set; } = 1;
            public string Primary { get; set; } = "";   // безопасное имя профиля основного монитора
            public DateTime Created { get; set; } = DateTime.Now;
        }

        public static string SafeName(string deviceName) =>
            string.Join("_", deviceName.Split(Path.GetInvalidFileNameChars()));

        private static string GlobalPath => GlobalConfig.FilePath;

        private static IEnumerable<string> ProfileFiles() =>
            Directory.EnumerateFiles(PlankConfig.GetDataDir(), "*.json")
                .Where(f => { var n = Path.GetFileName(f); return n.StartsWith("settings_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("apps_", StringComparison.OrdinalIgnoreCase); });

        private static string PrimarySafe() =>
            SafeName((System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0]).DeviceName);

        public static void Export(string zipPath)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var f in ProfileFiles()) zip.CreateEntryFromFile(f, Path.GetFileName(f));
            if (File.Exists(GlobalPath)) zip.CreateEntryFromFile(GlobalPath, "global.json");
            var m = new Manifest { Primary = PrimarySafe() };
            var e = zip.CreateEntry(ManifestName);
            using var w = new StreamWriter(e.Open());
            w.Write(JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        }

        // Проверка архива без распаковки: есть манифест PlankWin и хотя бы один профиль.
        public static bool IsValid(string zipPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var me = zip.GetEntry(ManifestName);
                if (me == null) return false;
                using var r = new StreamReader(me.Open());
                var m = JsonSerializer.Deserialize<Manifest>(r.ReadToEnd());
                return m?.App == "PlankWin" && zip.Entries.Any(x => x.Name.StartsWith("settings_", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        // Импорт: текущие файлы сначала уходят в резервную копию (backup_before_import_*.zip рядом с конфигом).
        public static void Import(string zipPath)
        {
            string dataDir = PlankConfig.GetDataDir();
            try { Export(Path.Combine(dataDir, $"backup_before_import_{DateTime.Now:yyyyMMdd_HHmmss}.zip")); } catch { }

            using var zip = ZipFile.OpenRead(zipPath);
            Manifest m;
            using (var r = new StreamReader(zip.GetEntry(ManifestName)!.Open()))
                m = JsonSerializer.Deserialize<Manifest>(r.ReadToEnd()) ?? new Manifest();

            foreach (var old in ProfileFiles().ToList()) { try { File.Delete(old); } catch { } }

            foreach (var entry in zip.Entries)
            {
                string name = entry.Name;
                if (name == ManifestName || name.Length == 0) continue;
                if (name.Equals("global.json", StringComparison.OrdinalIgnoreCase)) { entry.ExtractToFile(GlobalPath, true); continue; }
                if (!(name.StartsWith("settings_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("apps_", StringComparison.OrdinalIgnoreCase))) continue;
                if (name.Contains("..") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue; // только плоские имена
                entry.ExtractToFile(Path.Combine(dataDir, name), true);
            }

            // Другой ПК: профиля текущего основного монитора в архиве нет — берём профиль старого основного.
            string cur = PrimarySafe();
            if (!string.IsNullOrEmpty(m.Primary) && !string.Equals(m.Primary, cur, StringComparison.OrdinalIgnoreCase))
                foreach (var kind in new[] { "settings_", "apps_" })
                {
                    string dst = Path.Combine(dataDir, kind + cur + ".json");
                    string src = Path.Combine(dataDir, kind + m.Primary + ".json");
                    if (!File.Exists(dst) && File.Exists(src)) File.Copy(src, dst);
                }
        }
    }
}
