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

    public sealed class DockSettings
    {
        /// <summary>Положение дока на экране.</summary>
        public DockPosition Position { get; set; } = DockPosition.Bottom;

        /// <summary>Высота дока при размещении сверху/снизу.</summary>
        public double Height { get; set; } = 72;

        /// <summary>Ширина дока при размещении слева/справа.</summary>
        public double Width { get; set; } = 72;

        /// <summary>Автоскрытие дока.</summary>
        public bool AutoHide { get; set; } = true;

        /// <summary>
        /// Имена процессов, которые нужно игнорировать (не показывать в доке).
        /// Сравнение без учёта регистра, по подстроке.
        /// </summary>
        public string[] IgnoreProcessNames { get; set; } = Array.Empty<string>();
    }

    public static class PlankConfig
    {
        private const string ConfigFileName = "config.json";

        public static DockSettings Load(string baseDir)
        {
            var path = Path.Combine(baseDir, ConfigFileName);

            // Нет файла — создаём дефолтный.
            if (!File.Exists(path))
            {
                var settings = new DockSettings();
                TrySave(path, settings);
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

                if (settings.IgnoreProcessNames == null)
                    settings.IgnoreProcessNames = Array.Empty<string>();

                return settings;
            }
            catch
            {
                // Конфиг битый или старого формата — не ломаем док, просто берём дефолт.
                return new DockSettings();
            }
        }

        public static void Save(string baseDir, DockSettings settings)
        {
            var path = Path.Combine(baseDir, ConfigFileName);
            TrySave(path, settings);
        }

        private static void TrySave(string path, DockSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                File.WriteAllText(path, json);
            }
            catch
            {
                // Тихо игнорируем — это не критично для работы дока.
            }
        }
    }
}
