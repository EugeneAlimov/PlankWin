using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace PlankWin
{
    public partial class MainWindow
    {
        private void LoadAppsConfig()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var configPath = Path.Combine(baseDir, "apps.json");

                if (!File.Exists(configPath))
                {
                    MessageBox.Show(
                        $"Файл конфигурации apps.json не найден в {baseDir}.\n" +
                        "Будет создан пример конфига.",
                        "PlankWin",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    CreateSampleConfig(configPath);
                }

                var json = File.ReadAllText(configPath);

                var apps = JsonSerializer.Deserialize<DockAppConfig[]>(
                               json,
                               new JsonSerializerOptions
                               {
                                   PropertyNameCaseInsensitive = true
                               })
                           ?? Array.Empty<DockAppConfig>();

                Apps.Clear();
                foreach (var app in apps)
                {
                    if (string.IsNullOrWhiteSpace(app.Name) ||
                        string.IsNullOrWhiteSpace(app.Path))
                        continue;

                    app.IsDynamic = false;
                    app.IsRunning = false;
                    app.WindowHandle = IntPtr.Zero;

                    Apps.Add(app);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка чтения конфигурации приложений:\n{ex.Message}",
                    "PlankWin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static void CreateSampleConfig(string configPath)
        {
            var sample = """
            [
              {
                "name": "Notepad",
                "path": "C:\\\\Windows\\\\System32\\\\notepad.exe",
                "processName": "notepad",
                "arguments": "",
                "icon": ""
              },
              {
                "name": "Paint",
                "path": "mspaint.exe",
                "processName": "mspaint",
                "arguments": "",
                "icon": ""
              },
              {
                "name": "Chrome",
                "path": "C:\\\\Program Files\\\\Google\\\\Chrome\\\\Application\\\\chrome.exe",
                "processName": "chrome",
                "arguments": "",
                "icon": ""
              }
            ]
            """;

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, sample);
        }
    }
}
