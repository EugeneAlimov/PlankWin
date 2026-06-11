using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using System.Windows;
using System.Text;
using System.Threading.Tasks; // Добавили для Task.Delay

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

        // ФИКС №2: Добавили параметр retryCount для ожидания загрузки
        private void OnShellWindowCreated(IntPtr hWnd, int retryCount = 0)
        {
            string className = Win32Helper.GetWindowClassName(hWnd);
            var sb = new StringBuilder(256);
            Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            // Debug: Логируем ВСЕ новые окна
            System.Diagnostics.Debug.WriteLine($"🔍 New window: '{title}' ({className})");

            if (!IsValidAppWindow(hWnd))
            {
                System.Diagnostics.Debug.WriteLine($"❌ Filtered by IsValidAppWindow");

                // Если окно невидимо, но это UWP-коробка, не сдаемся! 
                // Ждем 200мс и проверяем снова (до 10 попыток).
                if (retryCount < 10 && Win32Helper.GetWindowClassName(hWnd) == "ApplicationFrameWindow")
                {
                    System.Diagnostics.Debug.WriteLine($"🔄 Retrying UWP window in 200ms (attempt {retryCount + 1})");
                    Task.Delay(200).ContinueWith(_ => Dispatcher.InvokeAsync(() => OnShellWindowCreated(hWnd, retryCount + 1)));
                }
                return;
            }

            string exePath = GetProcessExecutablePath(hWnd);
            if (string.IsNullOrEmpty(exePath)) return;

            System.Diagnostics.Debug.WriteLine($"✅ Valid window: '{title}' -> {exePath}");

            // Если окно стало видимым, но внутри него еще нет реального приложения 
            // (путь все еще ведет к пустой коробке ApplicationFrameHost), снова ждем.
            if (exePath.Contains("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                if (retryCount < 10)
                {
                    System.Diagnostics.Debug.WriteLine($"🔄 ApplicationFrameHost detected, retrying in 200ms");
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
                    System.Diagnostics.Debug.WriteLine($"🔄 Added window to existing app: {targetApp.Name}");
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
                System.Diagnostics.Debug.WriteLine($"➕ Added new app: {processName}");
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
                    targetApp.ForceIconUpdate(); // Добавил пинок, чтобы после закрытия окна иконка обновлялась на следующее
                }
            }
        }

        // ФИКС №1: Добавили подстраховку при активации окна
        private void OnShellWindowActivated(IntPtr hWnd)
        {
            // Если окно активировалось (вышло на передний план), но его нет на панели 
            // (например, мы пропустили его при запуске из-за невидимости) — добавляем его сейчас!
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
        // ФИЛЬТР ДЛЯ POPUP ОКОН (МИНИМАЛЬНОЕ исключение для Calculator)
        // =========================================================
        private bool IsContextMenuOrPopup(IntPtr hWnd)
        {
            int style = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_STYLE);
            if ((style & Win32Helper.WS_POPUP) != 0)
            {
                string className = Win32Helper.GetWindowClassName(hWnd);

                // Только для Calculator CoreWindow - разрешаем несмотря на WS_POPUP
                if (className == "Windows.UI.Core.CoreWindow")
                {
                    return false;
                }

                return true; // Остальные popup окна фильтруем
            }
            return false;
        }

        // =========================================================
        // ЖЕЛЕЗОБЕТОННЫЙ ФИЛЬТР ОКОН
        // =========================================================
        private bool IsValidAppWindow(IntPtr hWnd)
        {
            string className = Win32Helper.GetWindowClassName(hWnd);

            // UWP окна часто не проходят стандартные проверки - проверяем их отдельно
            bool isUwpWindow = (className == "ApplicationFrameWindow" || className == "Windows.UI.Core.CoreWindow");

            if (!isUwpWindow)
            {
                if (!Win32Helper.IsWindowVisible(hWnd)) return false;
                if (IsWindowCloaked(hWnd)) return false;
            }
            else
            {
                // Для UWP окон более мягкие проверки
                System.Diagnostics.Debug.WriteLine($"🔍 UWP window check: {className}, Visible: {Win32Helper.IsWindowVisible(hWnd)}, Cloaked: {IsWindowCloaked(hWnd)}");
            }

            // ФИКС: Проверяем popup окна (кроме Calculator CoreWindow)
            if (IsContextMenuOrPopup(hWnd)) return false;

            int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
            if ((exStyle & Win32Helper.WS_EX_TOOLWINDOW) != 0) return false;

            IntPtr owner = Win32Helper.GetWindow(hWnd, Win32Helper.GW_OWNER);
            if (owner != IntPtr.Zero && (exStyle & Win32Helper.WS_EX_APPWINDOW) == 0) return false;

            // 1. ЧЕРНЫЙ СПИСОК КЛАССОВ (УБРАЛИ Windows.UI.Core.CoreWindow для Calculator)
            string[] ignoredClasses = {
                "Progman", "WorkerW", "Shell_TrayWnd", "TrayNotifyWnd",
                "LivePreviewSurface", "ThumbnailDeviceHelperWnd",
                "TaskSwitchWnd", "MultitaskingViewFrame",
                "XamlExplorerHostIslandWindow", "NotifyIconOverflowWindow"
            };
            if (ignoredClasses.Contains(className)) return false;

            // 2. ЧЕРНЫЙ СПИСОК ЗАГОЛОВКОВ
            var sb = new StringBuilder(256);
            Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (title == "LivePreviewSurface" || title == "Task View" || title == "Program Manager") return false;

            // 3. ЧЕРНЫЙ СПИСОК ПРОЦЕССОВ (Скрытые фоновые процессы Windows 11)
            string exePath = GetProcessExecutablePath(hWnd);
            if (!string.IsNullOrEmpty(exePath))
            {
                string processName = System.IO.Path.GetFileNameWithoutExtension(exePath);

                if (processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                    processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                    processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (Settings.IgnoreProcessNames != null && Settings.IgnoreProcessNames.Any(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            return true;
        }

        private void RefreshRunningApps()
        {
            Win32Helper.EnumWindows((hWnd, lParam) =>
            {
                if (IsValidAppWindow(hWnd))
                {
                    string path = GetProcessExecutablePath(hWnd);
                    if (!string.IsNullOrEmpty(path)) Dispatcher.InvokeAsync(() => OnShellWindowCreated(hWnd));
                }
                return true;
            }, IntPtr.Zero);
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
                if (Win32Helper.QueryFullProcessImageName(hProcess, 0, sb, ref size)) return sb.ToString();
            }
            finally
            {
                Win32Helper.CloseHandle(hProcess);
            }
            return string.Empty;
        }

        // Метод для проверки cloaked окон
        private bool IsWindowCloaked(IntPtr hWnd)
        {
            const uint DWMWA_CLOAKED = 14;
            if (Win32Helper.DwmGetWindowAttribute(hWnd, (int)DWMWA_CLOAKED, out int cloakedFlag, sizeof(int)) == 0)
            {
                return cloakedFlag != 0;
            }
            return false;
        }
    }
}