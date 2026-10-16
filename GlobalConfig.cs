using System.IO;
using System.Text.Json;

namespace PlankWin
{
    // Настройки уровня приложения (не профиля монитора): на каких мониторах панель выключена.
    // Файл global.json — в папке данных (%LocalAppData%\PlankWin), как и профили. Раньше лежал рядом с exe,
    // но у single-file сборки AppContext.BaseDirectory — временная папка распаковки, которая меняется с версией.
    public static class GlobalConfig
    {
        private sealed class Data
        {
            public List<string> DisabledMonitors { get; set; } = new();
            public string Language { get; set; } = "auto"; // "auto" | ru | en | uk | de
            public bool CheckUpdates { get; set; } = true;   // автопроверка обновлений на GitHub
        }

        private static Data _data = Load();
        public static string FilePath => Path.Combine(PlankConfig.GetDataDir(), "global.json");

        private static Data Load()
        {
            try
            {
                string path = FilePath;
                string legacy = Path.Combine(AppContext.BaseDirectory, "global.json");
                if (!File.Exists(path) && File.Exists(legacy)) { try { File.Copy(legacy, path); } catch { } }
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new Data();
            }
            catch { }
            return new Data();
        }

        private static void Save()
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }

        public static string Language
        {
            get => _data.Language;
            set { _data.Language = string.IsNullOrEmpty(value) ? "auto" : value; Save(); }
        }

        public static bool CheckUpdates
        {
            get => _data.CheckUpdates;
            set { _data.CheckUpdates = value; Save(); }
        }

        public static bool IsMonitorEnabled(string deviceName) =>
            !_data.DisabledMonitors.Contains(deviceName, StringComparer.OrdinalIgnoreCase);

        public static void SetMonitorEnabled(string deviceName, bool enabled)
        {
            _data.DisabledMonitors.RemoveAll(m => string.Equals(m, deviceName, StringComparison.OrdinalIgnoreCase));
            if (!enabled) _data.DisabledMonitors.Add(deviceName);
            Save();
        }
    }
}
