using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace PlankWin
{
    public partial class MainWindow
    {
        // --- КОНВЕРТЕР-ГЛУШИТЕЛЬ ДЛЯ INTPTR ---
        private class IntPtrConverter : JsonConverter<IntPtr>
        {
            public override IntPtr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => IntPtr.Zero;
            public override void Write(Utf8JsonWriter writer, IntPtr value, JsonSerializerOptions options) => writer.WriteNumberValue(0);
        }

        private JsonSerializerOptions GetJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            options.Converters.Add(new IntPtrConverter());
            return options;
        }

        // МАГИЯ: Генерируем индивидуальное имя файла для каждой панели!
        private string GetAppsConfigPath()
        {
            string safeProfileName = string.Join("_", _profileName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(AppContext.BaseDirectory, $"apps_{safeProfileName}.json");
        }

        private void LoadAppsConfig()
        {
            try
            {
                _isLoadingConfig = true; // Блокируем анимации
                string configPath = GetAppsConfigPath();
                string oldConfigPath = Path.Combine(AppContext.BaseDirectory, "apps.json");

                // Если индивидуального конфига еще нет, но есть старый общий — копируем его
                if (!File.Exists(configPath) && File.Exists(oldConfigPath))
                {
                    File.Copy(oldConfigPath, configPath);
                }
                else if (!File.Exists(configPath))
                {
                    CreateSampleConfig(configPath);
                }

                var json = File.ReadAllText(configPath);
                var apps = JsonSerializer.Deserialize<DockAppConfig[]>(json, GetJsonOptions());

                if (apps == null) return;

                foreach (var app in apps)
                {
                    if (string.IsNullOrWhiteSpace(app.Name) || string.IsNullOrWhiteSpace(app.Path))
                        continue;

                    app.IsDynamic = false; // Если загружено из файла — значит закреплено
                    app.IsRunning = false;
                    app.WindowHandle = IntPtr.Zero;

                    Apps.Add(app);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка чтения конфигурации приложений:\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isLoadingConfig = false; //Разрешаем анимации
            }
        }

        private void SaveAppsConfig()
        {
            try
            {
                string configPath = GetAppsConfigPath();

                // Сохраняем ТОЛЬКО закрепленные (не динамические) иконки
                var pinnedApps = Apps.Where(a => !a.IsDynamic && !a.IsPlaceholder).ToArray();
                var json = JsonSerializer.Serialize(pinnedApps, GetJsonOptions());

                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения apps:\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void CreateSampleConfig(string configPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, "[]");
        }
    }
}