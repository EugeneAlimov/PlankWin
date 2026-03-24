using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using System.Windows;
using System.Text;
using System.Threading.Tasks;

namespace PlankWin
{
    public partial class MainWindow
    {
        private int _shellHookMessage;
        private HwndSource? _hwndSource;

        private const int HSHELL_WINDOWCREATED = 1;
        private const int HSHELL_WINDOWDESTROYED = 2;
        private const int HSHELL_WINDOWACTIVATED = 4;
        private const int HSHELL_RUDEAPPACTIVATED = 32772;
        private const int HSHELL_REDRAW = 6;

        private void InitializeProcessMonitor()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);

            if (_hwndSource != null)
            {
                _hwndSource.AddHook(ShellMonitorWndProc);
                _shellHookMessage = Win32Helper.RegisterWindowMessage("SHELLHOOK");
                Win32Helper.RegisterShellHookWindow(hwnd);
                RefreshRunningApps();
            }
        }

        private void StopProcessMonitor()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Win32Helper.DeregisterShellHookWindow(hwnd);
            _hwndSource?.RemoveHook(ShellMonitorWndProc);
        }

        private void OnShellWindowRedraw(IntPtr hWnd)
        {
            var targetApp = Apps.FirstOrDefault(a => a.WindowHandles.Contains(hWnd));
            if (targetApp != null && targetApp.WindowHandle == hWnd)
            {
                targetApp.ForceIconUpdate();
            }
        }

        private IntPtr ShellMonitorWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == _shellHookMessage)
            {
                int shellEvent = wParam.ToInt32();
                IntPtr targetHwnd = lParam;

                switch (shellEvent)
                {
                    case HSHELL_WINDOWCREATED:
                        OnShellWindowCreated(targetHwnd);

                        // ФИКС ДЛЯ ИКОНОК WPF: 
                        // Даем приложению 1 секунду на отрисовку, затем просим конвертер обновить иконку
                        Task.Delay(1000).ContinueWith(_ => Dispatcher.InvokeAsync(() => {
                            var app = Apps.FirstOrDefault(a => a.WindowHandles.Contains(targetHwnd));
                            app?.ForceIconUpdate();
                        }));
                        break;

                    case HSHELL_WINDOWDESTROYED:
                        OnShellWindowDestroyed(targetHwnd);
                        break;

                    case HSHELL_WINDOWACTIVATED:
                    case HSHELL_RUDEAPPACTIVATED:
                        OnShellWindowActivated(targetHwnd);
                        break;

                    case HSHELL_REDRAW:
                        // ФИКС ПЕРЕГРЕВА ПРОЦЕССОРА:
                        // OnShellWindowRedraw(targetHwnd); // <-- ЗАКОММЕНТИРОВАНО!
                        break;
                }
            }
            return IntPtr.Zero;
        }

        private void OnShellWindowCreated(IntPtr hWnd, int retryCount = 0)
        {
            if (!IsValidAppWindow(hWnd))
            {
                // Если окно невидимо, но это UWP-коробка, не сдаемся! 
                // Ждем 200мс и проверяем снова (до 10 попыток).
                if (retryCount < 10 && Win32Helper.GetWindowClassName(hWnd) == "ApplicationFrameWindow")
                {
                    Task.Delay(200).ContinueWith(_ => Dispatcher.InvokeAsync(() => OnShellWindowCreated(hWnd, retryCount + 1)));
                }
                return;
            }

            string exePath = GetProcessExecutablePath(hWnd);
            if (string.IsNullOrEmpty(exePath)) return;

            // Если окно стало видимым, но внутри него еще нет реального приложения 
            // (путь все еще ведет к пустой коробке ApplicationFrameHost), снова ждем.
            if (exePath.Contains("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                if (retryCount < 10)
                {
                    Task.Delay(200).ContinueWith(_ => Dispatcher.InvokeAsync(() => OnShellWindowCreated(hWnd, retryCount + 1)));
                }
                return;
            }

            var targetApp = Apps.FirstOrDefault(a => a.Path.Equals(exePath, StringComparison.OrdinalIgnoreCase));

            if (targetApp != null)
            {
                if (!targetApp.WindowHandles.Contains(hWnd))
                {
                    targetApp.WindowHandles.Add(hWnd);
                    targetApp.WindowHandle = hWnd;
                    targetApp.RunningInstancesCount = targetApp.WindowHandles.Count;
                    targetApp.IsRunning = true;
                    targetApp.ForceIconUpdate();
                }
            }
            else
            {
                string processName = System.IO.Path.GetFileNameWithoutExtension(exePath);
                var newApp = new DockAppConfig
                {
                    Name = processName,
                    Path = exePath,
                    ProcessName = processName,
                    IsDynamic = true,
                    IsRunning = true,
                    WindowHandle = hWnd,
                    RunningInstancesCount = 1
                };
                newApp.WindowHandles.Add(hWnd);
                Apps.Add(newApp);
            }
        }

        private void OnShellWindowDestroyed(IntPtr hWnd)
        {
            var targetApp = Apps.FirstOrDefault(a => a.WindowHandles.Contains(hWnd));
            if (targetApp != null)
            {
                targetApp.WindowHandles.Remove(hWnd);
                targetApp.RunningInstancesCount = targetApp.WindowHandles.Count;

                if (targetApp.RunningInstancesCount == 0)
                {
                    targetApp.IsRunning = false;
                    targetApp.WindowHandle = IntPtr.Zero;

                    if (targetApp.IsDynamic)
                    {
                        targetApp.IsDeleting = true;
                        PositionDock(skipAnimation: false);
                        Dispatcher.InvokeAsync(async () =>
                        {
                            await AnimateItemContainerAsync(targetApp, targetSize: 0, animateOpacity: true, durationMs: 350);
                            Apps.Remove(targetApp);
                        });
                    }
                }
                else
                {
                    targetApp.WindowHandle = targetApp.WindowHandles.Last();
                    targetApp.ForceIconUpdate();
                }
            }
        }

        private void OnShellWindowActivated(IntPtr hWnd)
        {
            // Если окно активировалось (вышло на передний план), но его нет на панели 
            bool isKnown = Apps.Any(a => a.WindowHandles.Contains(hWnd));
            if (!isKnown)
            {
                OnShellWindowCreated(hWnd);
            }

            foreach (var app in Apps) { app.IsActive = false; app.ActiveWindowIndex = -1; }
            var targetApp = Apps.FirstOrDefault(a => a.WindowHandles.Contains(hWnd));
            if (targetApp != null)
            {
                targetApp.IsActive = true;
                targetApp.ActiveWindowIndex = targetApp.WindowHandles.IndexOf(hWnd);
                targetApp.WindowHandle = hWnd;
            }
        }

        // =========================================================
        // УЛУЧШЕННЫЙ ЖЕЛЕЗОБЕТОННЫЙ ФИЛЬТР ОКОН
        // =========================================================
        private bool IsValidAppWindow(IntPtr hWnd)
        {
            try
            {
                // Базовые проверки видимости
                if (!Win32Helper.IsWindowVisible(hWnd)) return false;
                if (IsWindowCloaked(hWnd)) return false;

                // Проверки стилей окна
                int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
                if ((exStyle & Win32Helper.WS_EX_TOOLWINDOW) != 0) return false;

                IntPtr owner = Win32Helper.GetWindow(hWnd, Win32Helper.GW_OWNER);
                if (owner != IntPtr.Zero && (exStyle & Win32Helper.WS_EX_APPWINDOW) == 0) return false;

                // Получаем данные об окне
                string className = Win32Helper.GetWindowClassName(hWnd);
                var sb = new StringBuilder(256);
                Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                // 1. ЧЕРНЫЙ СПИСОК КЛАССОВ ОКОН (БЕЗ блокировки UWP!)
                string[] ignoredClasses = {
                    // Основные системные окна
                    "Progman", "WorkerW", "Shell_TrayWnd", "TrayNotifyWnd",
                    
                    // Task View и предварительный просмотр - ВОТ ГЛАВНЫЕ ВРАГИ!
                    "LivePreviewSurface", "ThumbnailDeviceHelperWnd",
                    "TaskSwitchWnd", "MultitaskingViewFrame",
                    "TaskListThumbnailWnd", "TaskbandCreatedByUnknownApplication",
                    
                    // Windows 11 служебные окна
                    "XamlExplorerHostIslandWindow", "NotifyIconOverflowWindow",
                    "Windows.UI.Popups.PopupHost",
                    
                    // Дополнительные системные классы
                    "IMEClass", "TextInputHost", "InputSwitchWndClass",
                    "MSTaskSwWClass", "Button", "Static"
                };

                if (ignoredClasses.Contains(className, StringComparer.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"Filtered by class: {className} - {title}");
                    return false;
                }

                // 2. ЧЕРНЫЙ СПИСОК ЗАГОЛОВКОВ ОКОН
                string[] ignoredTitles = {
                    "LivePreviewSurface", "Task View", "Program Manager",
                    "Default IME", "MSCTFIME UI", "GDI+ Window",
                    "OLEChannelWnd", "Windows Input Experience",
                    "Tooltip", "ToolTip", "PopupHost"
                };

                if (ignoredTitles.Any(t => title.Equals(t, StringComparison.OrdinalIgnoreCase)))
                {
                    System.Diagnostics.Debug.WriteLine($"Filtered by title: {title} - {className}");
                    return false;
                }

                // 3. СПЕЦИАЛЬНАЯ ПРОВЕРКА ДЛЯ LivePreviewSurface и подобного
                if (title.Contains("Preview", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("Preview", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("Thumbnail", StringComparison.OrdinalIgnoreCase))
                {
                    // Проверяем процесс - если это explorer.exe с подозрительным окном
                    string exePath = GetProcessExecutablePath(hWnd);
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        string processName = System.IO.Path.GetFileNameWithoutExtension(exePath);
                        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Debug.WriteLine($"Filtered explorer preview window: {title} - {className}");
                            return false;
                        }
                    }
                }

                // 4. ОСТОРОЖНЫЙ ЧЕРНЫЙ СПИСОК ПРОЦЕССОВ (БЕЗ ApplicationFrameHost!)
                string processExecutablePath = GetProcessExecutablePath(hWnd);
                if (!string.IsNullOrEmpty(processExecutablePath))
                {
                    string processName = System.IO.Path.GetFileNameWithoutExtension(processExecutablePath);

                    // ТОЛЬКО самые очевидные системные процессы (НЕ блокируем UWP!)
                    string[] ignoredProcesses = {
                        "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
                        "TextInputHost", "RuntimeBroker"
                        // УБРАЛ: ApplicationFrameHost, WWAHost, SystemSettings - они нужны для UWP!
                    };

                    if (ignoredProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
                    {
                        System.Diagnostics.Debug.WriteLine($"Filtered by process: {processName} - {title}");
                        return false;
                    }

                    // Пользовательский черный список из настроек
                    if (Settings.IgnoreProcessNames != null &&
                        Settings.IgnoreProcessNames.Any(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase)))
                    {
                        System.Diagnostics.Debug.WriteLine($"Filtered by user setting: {processName}");
                        return false;
                    }
                }

                // 5. ПРОВЕРКИ ДЛЯ UWP (НЕ блокируем, а проверяем содержимое)
                if (className == "ApplicationFrameWindow")
                {
                    // Для UWP окон проверяем есть ли реальное содержимое
                    IntPtr realWindow = IntPtr.Zero;
                    Win32Helper.EnumChildWindows(hWnd, (child, param) =>
                    {
                        string childClass = Win32Helper.GetWindowClassName(child);
                        if (childClass == "Windows.UI.Core.CoreWindow")
                        {
                            realWindow = child;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);

                    // Если нет реального содержимого - это пустая оболочка, но НЕ блокируем сразу
                    // Даем шанс приложению загрузиться в OnShellWindowCreated
                    if (realWindow == IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine($"Empty UWP frame (will retry): {title}");
                        // НЕ возвращаем false! Пусть OnShellWindowCreated попробует несколько раз
                    }
                }

                // 6. ПРОВЕРКА НА МИНИМАЛЬНЫЙ РАЗМЕР ОКНА
                if (Win32Helper.GetWindowRect(hWnd, out var rect))
                {
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;

                    // Игнорируем слишком маленькие окна (скорее всего служебные)
                    if (width < 50 || height < 50)
                    {
                        System.Diagnostics.Debug.WriteLine($"Filtered tiny window: {width}x{height} - {title}");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IsValidAppWindow error: {ex.Message}");
                return false;
            }
        }

        private void RefreshRunningApps()
        {
            var foundApps = new List<DockAppConfig>();

            Win32Helper.EnumWindows((hWnd, lParam) =>
            {
                if (IsValidAppWindow(hWnd))
                {
                    string path = GetProcessExecutablePath(hWnd);
                    if (!string.IsNullOrEmpty(path))
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            OnShellWindowCreated(hWnd);

                            // Запоминаем приложение для последующего обновления иконок
                            var app = Apps.FirstOrDefault(a => a.WindowHandles.Contains(hWnd));
                            if (app != null && !foundApps.Contains(app))
                            {
                                foundApps.Add(app);
                            }
                        });
                    }
                }
                return true;
            }, IntPtr.Zero);

            // Принудительное обновление иконок после небольшой задержки
            Task.Delay(500).ContinueWith(_ => Dispatcher.InvokeAsync(() =>
            {
                System.Diagnostics.Debug.WriteLine($"Force updating icons for {foundApps.Count} apps after initial scan");

                foreach (var app in foundApps)
                {
                    if (app != null && app.IsRunning)
                    {
                        System.Diagnostics.Debug.WriteLine($"Force updating icon for: {app.Name}");
                        app.ForceIconUpdate();
                    }
                }

                // Дополнительное обновление для UWP приложений через 1.5 секунды
                Task.Delay(1000).ContinueWith(__ => Dispatcher.InvokeAsync(() =>
                {
                    foreach (var app in foundApps)
                    {
                        if (app != null && app.IsRunning)
                        {
                            // Проверяем если это UWP приложение или если иконка еще не загрузилась
                            bool isUwpApp = app.WindowHandles?.Any(hwnd =>
                                Win32Helper.IsWindow(hwnd) &&
                                Win32Helper.GetWindowClassName(hwnd) == "ApplicationFrameWindow") == true;

                            if (isUwpApp)
                            {
                                System.Diagnostics.Debug.WriteLine($"Second force update for UWP app: {app.Name}");
                                app.ForceIconUpdate();
                            }
                        }
                    }
                }));
            }));
        }

        private string GetProcessExecutablePath(IntPtr hWnd)
        {
            string className = Win32Helper.GetWindowClassName(hWnd);
            if (className == "ApplicationFrameWindow")
            {
                IntPtr realHwnd = IntPtr.Zero;
                Win32Helper.EnumChildWindows(hWnd, (child, param) =>
                {
                    if (Win32Helper.GetWindowClassName(child) == "Windows.UI.Core.CoreWindow")
                    {
                        realHwnd = child;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (realHwnd != IntPtr.Zero) hWnd = realHwnd;
            }

            Win32Helper.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return string.Empty;

            IntPtr hProcess = Win32Helper.OpenProcess(0x1000, false, pid);
            if (hProcess == IntPtr.Zero) return string.Empty;

            try
            {
                int size = 1024;
                var sb = new StringBuilder(size);
                if (Win32Helper.QueryFullProcessImageName(hProcess, 0, sb, ref size))
                    return sb.ToString();
            }
            finally
            {
                Win32Helper.CloseHandle(hProcess);
            }
            return string.Empty;
        }
    }
}