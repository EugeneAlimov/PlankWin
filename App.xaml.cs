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

            // Отчёты об ошибках — как можно раньше, чтобы поймать и падения при старте.
            CrashReporter.Install(this);
        }

        // Словарь для хранения открытых панелей. Ключ - системное имя монитора (например, \\.\DISPLAY1)
        private Dictionary<string, MainWindow> _docks = new Dictionary<string, MainWindow>();

        protected override void OnStartup(StartupEventArgs e)
        {
            // Перезапуск после импорта: дожидаемся выхода прежнего экземпляра (трей, AppBar, файлы).
            int wi = Array.IndexOf(e.Args, "--wait-pid");
            if (wi >= 0 && wi + 1 < e.Args.Length && int.TryParse(e.Args[wi + 1], out int oldPid))
            {
                try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(8000); } catch { }
            }
            Updater.CleanupOld(); // хвост прошлого обновления (PlankWin.exe.old)
            PlankWin.StartMenuAumids.Warmup(); // фоном: карта exe → AUMID для меню «Недавнее»
            // Win10: прямые углы у элементов «под систему» (меню, списки, вкладки, превью).
            if (!PlankWin.Win32Helper.IsWin11)
            {
                Resources["SysRadiusL"] = new System.Windows.CornerRadius(0);
                Resources["SysRadiusM"] = new System.Windows.CornerRadius(0);
                Resources["SysRadiusS"] = new System.Windows.CornerRadius(0);
            }

            base.OnStartup(e);

            // Язык интерфейса — до создания окон (XAML берёт строки через DynamicResource).
            Loc.Apply(GlobalConfig.Language);

            // Подписываемся на системное событие подключения/отключения мониторов
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            // Запускаем первую проверку при старте программы
            SyncMonitors();

            // Проверка механизма отчётов: PlankWin.exe --crash-test (ошибка через 3 с; окно отчёта — при следующем запуске).
            if (Array.IndexOf(e.Args, "--crash-test") >= 0)
            {
                var ct = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                ct.Tick += (s, a) => { ct.Stop(); throw new InvalidOperationException("Crash test (--crash-test)"); };
                ct.Start();
            }

            // Была ошибка в прошлом сеансе — предложить отчёт (после того как панели поднялись).
            Dispatcher.BeginInvoke(new Action(CrashReporter.ShowPendingIfAny), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // Обновления: первая проверка через 20 с после старта, дальше раз в 6 часов.
            var updTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            updTimer.Tick += async (s, a) => { updTimer.Interval = TimeSpan.FromHours(6); await Updater.BackgroundCheck(); };
            updTimer.Start();
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            // Если юзер воткнул или выдернул кабель - пересчитываем панели
            SyncMonitors();
        }

        public MainWindow? GetDock(string deviceName) => _docks.TryGetValue(deviceName, out var d) ? d : null;

        // Включить/выключить панель на мониторе (из трея). Последнюю включённую выключить нельзя.
        public void SetMonitorEnabled(string deviceName, bool enabled)
        {
            if (!enabled)
            {
                int enabledCount = System.Windows.Forms.Screen.AllScreens.Count(s => GlobalConfig.IsMonitorEnabled(s.DeviceName));
                if (enabledCount <= 1) return;
            }
            GlobalConfig.SetMonitorEnabled(deviceName, enabled);
            SyncMonitors();
        }

        public void SyncMonitors()
        {
            var currentScreens = System.Windows.Forms.Screen.AllScreens;
            // Мониторы, на которых панель выключена пользователем, пропускаем.
            // Если выключены все подключённые — включаем основной (без панели остаться нельзя).
            var wanted = currentScreens.Where(s => GlobalConfig.IsMonitorEnabled(s.DeviceName)).ToList();
            if (wanted.Count == 0)
            {
                var primary = currentScreens.FirstOrDefault(s => s.Primary) ?? currentScreens.First();
                GlobalConfig.SetMonitorEnabled(primary.DeviceName, true);
                wanted.Add(primary);
            }
            var activeDeviceNames = wanted.Select(s => s.DeviceName).ToList();

            // 1. Убиваем панели на отключенных мониторах
            var disconnectedMonitors = _docks.Keys.Except(activeDeviceNames).ToList();
            foreach (var monitor in disconnectedMonitors)
            {
                _docks[monitor].Close();
                _docks.Remove(monitor);
            }

            // 2. Создаем панели для новых (или всех при старте) мониторов
            foreach (var screen in wanted)
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