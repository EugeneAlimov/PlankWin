using System.Windows;
using Microsoft.Win32;

namespace PlankWin
{
    public partial class App : Application
    {
        public App()
        {
            // ОТКЛЮЧАЕМ ТОРМОЗА ОТ МЫШЕК С ВЫСОКОЙ ЧАСТОТОЙ ОПРОСА (MX Master 3S и др.)
            // Это отключит поиск сенсорных жестов и разблокирует UI-поток
            AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport", true);
        }

        // Словарь для хранения открытых панелей. Ключ - системное имя монитора (например, \\.\DISPLAY1)
        private Dictionary<string, MainWindow> _docks = new Dictionary<string, MainWindow>();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Подписываемся на системное событие подключения/отключения мониторов
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            // Запускаем первую проверку при старте программы
            SyncMonitors();
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            // Если юзер воткнул или выдернул кабель - пересчитываем панели
            SyncMonitors();
        }

        private void SyncMonitors()
        {
            var currentScreens = System.Windows.Forms.Screen.AllScreens;
            var activeDeviceNames = currentScreens.Select(s => s.DeviceName).ToList();

            // 1. Убиваем панели на отключенных мониторах
            var disconnectedMonitors = _docks.Keys.Except(activeDeviceNames).ToList();
            foreach (var monitor in disconnectedMonitors)
            {
                _docks[monitor].Close();
                _docks.Remove(monitor);
            }

            // 2. Создаем панели для новых (или всех при старте) мониторов
            foreach (var screen in currentScreens)
            {
                if (!_docks.ContainsKey(screen.DeviceName))
                {
                    // Создаем панель и передаем ей системное имя монитора в качестве имени профиля!
                    var dock = new MainWindow(screen.DeviceName);
                    _docks[screen.DeviceName] = dock;
                    dock.Show();
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Убираем за собой мусор при выходе
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            base.OnExit(e);
        }
    }
}