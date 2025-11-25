using System;
using System.IO;
using System.Text.Json;

namespace PlankWin
{
    public enum DockPosition
    {
        Bottom,
        Top,
        Left,
        Right
    }

    public class DockSettings
    {
        public DockPosition Position { get; set; } = DockPosition.Bottom;

        public bool AutoHide { get; set; } = true;

        public double Height { get; set; } = 72;

        public double Width { get; set; } = 72;

        /// <summary>
        /// Имена процессов, которые нужно игнорировать в доке.
        /// Сравнение без учета регистра, подстрока (т.е. "nahimic" поймает и "NahimicSvc").
        /// </summary>
        public string[] IgnoreProcessNames { get; set; } = Array.Empty<string>();
    }

    public static class PlankConfig
    {
        private const string ConfigFileName = "config.json";

        public static DockSettings Load(string baseDir)
        {
            var path = Path.Combine(baseDir, ConfigFileName);

            if (!File.Exists(path))
            {
                var settings = new DockSettings();
                Save(path, settings);
                return settings;
            }

            try
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<DockSettings>(
                                   json,
                                   new JsonSerializerOptions
                                   {
                                       PropertyNameCaseInsensitive = true
                                   })
                               ?? new DockSettings();

                // На случай старого файла без IgnoreProcessNames
                if (settings.IgnoreProcessNames == null)
                    settings.IgnoreProcessNames = Array.Empty<string>();

                return settings;
            }
            catch
            {
                // Если конфиг повреждён — создаём новый по умолчанию
                var settings = new DockSettings();
                Save(path, settings);
                return settings;
            }
        }

        public static void Save(string path, DockSettings settings)
        {
            var json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            File.WriteAllText(path, json);
        }
    }
}
