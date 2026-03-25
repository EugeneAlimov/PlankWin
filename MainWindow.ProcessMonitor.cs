using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using System.Windows;
using System.Text;
using System.Threading.Tasks;
using static PlankWin.Win32Helper;

namespace PlankWin
{
    public partial class MainWindow
    {
        private static DateTime _lastExplorerRedraw = DateTime.MinValue;
        private static readonly TimeSpan ExplorerRedrawInterval = TimeSpan.FromMilliseconds(100);

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
                        HandleExplorerRedraw(targetHwnd);
                        break;
                }
            }
            return IntPtr.Zero;
        }

        private void HandleExplorerRedraw(IntPtr hWnd)
        {
            try
            {
                // Уменьшенный throttling: не чаще раза в 100ms
                if (DateTime.Now - _lastExplorerRedraw < ExplorerRedrawInterval)
                {
                    return;
                }

                // Проверка что это проводник
                if (!IsExplorerWindow(hWnd))
                {
                    return;
                }

                _lastExplorerRedraw = DateTime.Now;

                // УБРАЛИ проверку IsActive - обновляем любые окна проводника
                var targetApp = Apps.FirstOrDefault(a => a.WindowHandles.Contains(hWnd));
                if (targetApp != null && targetApp.WindowHandle == hWnd)
                {
                    System.Diagnostics.Debug.WriteLine($"Explorer REDRAW: Updating icon for {targetApp.Name}");
                    targetApp.ForceIconUpdate();

                    // Дополнительное обновление через 50ms для подстраховки
                    Task.Delay(50).ContinueWith(_ => Dispatcher.InvokeAsync(() => {
                        if (targetApp.IsRunning && targetApp.WindowHandle == hWnd)
                        {
                            System.Diagnostics.Debug.WriteLine($"Explorer REDRAW: Second update for {targetApp.Name}");
                            targetApp.ForceIconUpdate();
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HandleExplorerRedraw error: {ex.Message}");
            }
        }

        private bool IsExplorerWindow(IntPtr hWnd)
        {
            try
            {
                if (!Win32Helper.IsWindow(hWnd)) return false;

                string processPath = GetProcessExecutablePath(hWnd);
                if (string.IsNullOrEmpty(processPath)) return false;

                string processName = System.IO.Path.GetFileNameWithoutExtension(processPath);
                return processName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false; // При любой ошибке - безопасно игнорируем
            }
        }

        private void OnShellWindowCreated(IntPtr hWnd, int retryCount = 0)
        {
            try
            {
                string className = Win32Helper.GetWindowClassName(hWnd);
                var sb = new StringBuilder(256);
                Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                // Размер окна
                Win32Helper.RECT rect;
                int width = 0, height = 0;
                if (Win32Helper.GetWindowRect(hWnd, out rect))
                {
                    width = rect.Right - rect.Left;
                    height = rect.Bottom - rect.Top;
                }

                System.Diagnostics.Debug.WriteLine($"🔍 New window: '{title}' ({className}), Size: {width}x{height}");

                // ФИЛЬТР КОНТЕКСТНЫХ МЕНЮ:

                // 1. Окна с размером 0x0 или очень маленькие (tooltips, временные элементы)
                if (width <= 5 && height <= 5)
                {
                    System.Diagnostics.Debug.WriteLine($"🚫 Blocked tiny/zero-size window");
                    return;
                }

                // 2. Стандартные Windows контекстные меню
                if (className == "#32768")
                {
                    System.Diagnostics.Debug.WriteLine($"🚫 Blocked Windows context menu");
                    return;
                }

                // 3. MS Office контекстные меню - ТОЛЬКО маленькие NetUIHWND БЕЗ заголовка
                if (className == "NetUIHWND" && width < 400 && height < 300 && string.IsNullOrEmpty(title))
                {
                    System.Diagnostics.Debug.WriteLine($"🚫 Blocked Office context menu");
                    return;
                }

                // 4. WS_POPUP только если действительно маленькие
                int style = (int)Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_STYLE);
                const int WS_POPUP = unchecked((int)0x80000000);
                if ((style & WS_POPUP) != 0 && width < 400 && height < 300)
                {
                    System.Diagnostics.Debug.WriteLine($"🚫 Blocked small WS_POPUP window");
                    return;
                }

                // 5. Окна без названия и класса - подозрительные элементы
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(className))
                {
                    System.Diagnostics.Debug.WriteLine($"🚫 Blocked unnamed/classless window");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"✅ Window allowed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in popup filter: {ex.Message}");
            }

            if (!IsValidAppWindow(hWnd))
            {
                if (retryCount < 10 && Win32Helper.GetWindowClassName(hWnd) == "ApplicationFrameWindow")
                {
                    Task.Delay(200).ContinueWith(_ => Dispatcher.InvokeAsync(() => OnShellWindowCreated(hWnd, retryCount + 1)));
                }
                return;
            }

            string exePath = GetProcessExecutablePath(hWnd);
            if (string.IsNullOrEmpty(exePath)) return;

            if (exePath.Contains("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                if (retryCount < 10)
                {
                    Task.Delay(200).ContinueWith(_ => Dispatcher.InvokeAsync(() => OnShellWindowCreated(hWnd, retryCount + 1)));
                }
                return;
            }

            System.Diagnostics.Debug.WriteLine($"=== WINDOW CREATED ===");
            System.Diagnostics.Debug.WriteLine($"Process Path: {exePath}");
            string processName = System.IO.Path.GetFileNameWithoutExtension(exePath);
            System.Diagnostics.Debug.WriteLine($"Process Name: {processName}");

            // ПЕРВАЯ ПОПЫТКА: Точное сопоставление по пути
            var targetApp = Apps.FirstOrDefault(a => a.Path.Equals(exePath, StringComparison.OrdinalIgnoreCase));

            // ВТОРАЯ ПОПЫТКА: Сопоставление по имени процесса (для браузеров и сложных приложений)
            if (targetApp == null)
            {
                targetApp = Apps.FirstOrDefault(a =>
                    System.IO.Path.GetFileNameWithoutExtension(a.Path).Equals(processName, StringComparison.OrdinalIgnoreCase));

                if (targetApp != null)
                {
                    System.Diagnostics.Debug.WriteLine($"✅ MATCHED by process name: {targetApp.Name}");
                    System.Diagnostics.Debug.WriteLine($"   Pinned path: {targetApp.Path}");
                    System.Diagnostics.Debug.WriteLine($"   Running path: {exePath}");
                }
            }

            // ТРЕТЬЯ ПОПЫТКА: Специальная логика для браузеров
            if (targetApp == null && (processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
                                     processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                                     processName.Equals("msedge", StringComparison.OrdinalIgnoreCase)))
            {
                // Ищем любое закрепленное приложение которое содержит имя браузера
                targetApp = Apps.FirstOrDefault(a =>
                    a.Path.Contains(processName, StringComparison.OrdinalIgnoreCase) ||
                    a.Name.Contains(processName, StringComparison.OrdinalIgnoreCase));

                if (targetApp != null)
                {
                    System.Diagnostics.Debug.WriteLine($"✅ MATCHED browser by name contains: {targetApp.Name}");
                    System.Diagnostics.Debug.WriteLine($"   Pinned path: {targetApp.Path}");
                    System.Diagnostics.Debug.WriteLine($"   Running path: {exePath}");
                }
            }

            if (targetApp != null)
            {
                System.Diagnostics.Debug.WriteLine($"✅ MATCHED with existing app: {targetApp.Name}");

                if (!targetApp.WindowHandles.Contains(hWnd))
                {
                    targetApp.WindowHandles.Add(hWnd);
                    targetApp.WindowHandle = hWnd;
                    targetApp.RunningInstancesCount = targetApp.WindowHandles.Count;
                    targetApp.IsRunning = true;

                    // МГНОВЕННОЕ обновление иконки
                    System.Diagnostics.Debug.WriteLine($"Added window to existing app: {targetApp.Name} - forcing immediate icon update");
                    targetApp.ForceIconUpdate();

                    // Дополнительные обновления для подстраховки
                    Task.Delay(200).ContinueWith(_ => Dispatcher.InvokeAsync(() => {
                        if (targetApp.IsRunning)
                        {
                            System.Diagnostics.Debug.WriteLine($"Second icon update for: {targetApp.Name}");
                            targetApp.ForceIconUpdate();
                        }
                    }));

                    Task.Delay(500).ContinueWith(_ => Dispatcher.InvokeAsync(() => {
                        if (targetApp.IsRunning)
                        {
                            System.Diagnostics.Debug.WriteLine($"Third icon update for: {targetApp.Name}");
                            targetApp.ForceIconUpdate();
                        }
                    }));
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"❌ NO MATCH found for process: {exePath}");
                System.Diagnostics.Debug.WriteLine($"   Available pinned apps:");
                foreach (var app in Apps.Where(a => !a.IsDynamic))
                {
                    System.Diagnostics.Debug.WriteLine($"   - {app.Name}: {app.Path}");
                }

                // Создаем новое динамическое приложение
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

                System.Diagnostics.Debug.WriteLine($"   ➕ Created new dynamic app: {processName}");

                // МГНОВЕННОЕ обновление для новых динамических приложений
                newApp.ForceIconUpdate();

                // Дополнительное обновление
                Task.Delay(300).ContinueWith(_ => Dispatcher.InvokeAsync(() => {
                    if (newApp.IsRunning)
                    {
                        System.Diagnostics.Debug.WriteLine($"Second icon update for new app: {processName}");
                        newApp.ForceIconUpdate();
                    }
                }));
            }
            System.Diagnostics.Debug.WriteLine($"=======================");
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

        private bool IsValidAppWindow(IntPtr hWnd)
        {
            string className = Win32Helper.GetWindowClassName(hWnd);
            var sb = new StringBuilder(256);
            Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            bool isVisible = Win32Helper.IsWindowVisible(hWnd);
            bool isCloaked = IsWindowCloaked(hWnd);

            System.Diagnostics.Debug.WriteLine($"🔍 Window: '{title}' ({className}) - Visible: {isVisible}, Cloaked: {isCloaked}");

            if (!isVisible)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Filtered: Not visible");
                return false;
            }
            if (isCloaked)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Filtered: Cloaked");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"✅ Reached popup filter");

            // НОВАЯ ПРОВЕРКА: Фильтруем контекстные меню
            if (IsContextMenuOrPopup(hWnd))
            {
                return false;
            }

            int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
            if ((exStyle & Win32Helper.WS_EX_TOOLWINDOW) != 0) return false;

            IntPtr owner = Win32Helper.GetWindow(hWnd, Win32Helper.GW_OWNER);
            if (owner != IntPtr.Zero && (exStyle & Win32Helper.WS_EX_APPWINDOW) == 0) return false;


            // Остальные фильтры...
            string[] ignoredClasses = {
        "Progman", "WorkerW", "Shell_TrayWnd", "TrayNotifyWnd",
        "LivePreviewSurface", "ThumbnailDeviceHelperWnd",
        "TaskSwitchWnd", "MultitaskingViewFrame", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "NotifyIconOverflowWindow"
    };
            if (ignoredClasses.Contains(className)) return false;

            Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);

            if (title == "LivePreviewSurface" || title == "Task View" || title == "Program Manager") return false;

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
            var foundApps = new List<DockAppConfig>();
            int totalWindows = 0;
            int validWindows = 0;
            int invalidWindows = 0;

            System.Diagnostics.Debug.WriteLine("=== REFRESHING RUNNING APPS ===");

            Win32Helper.EnumWindows((hWnd, lParam) =>
            {
                totalWindows++;

                try
                {
                    // Получаем базовую информацию об окне
                    string className = Win32Helper.GetWindowClassName(hWnd);
                    var sb = new StringBuilder(256);
                    Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();
                    bool isVisible = Win32Helper.IsWindowVisible(hWnd);

                    // ПОДРОБНОЕ ЛОГИРОВАНИЕ для диагностики
                    if (!string.IsNullOrEmpty(title) && title.Length > 3) // Логируем только окна с разумными заголовками
                    {
                        System.Diagnostics.Debug.WriteLine($"--- Window Analysis ---");
                        System.Diagnostics.Debug.WriteLine($"HWND: {hWnd}");
                        System.Diagnostics.Debug.WriteLine($"Title: {title}");
                        System.Diagnostics.Debug.WriteLine($"Class: {className}");
                        System.Diagnostics.Debug.WriteLine($"Visible: {isVisible}");

                        // Проверяем почему окно может быть отфильтровано
                        bool passesFilter = IsValidAppWindow(hWnd);
                        System.Diagnostics.Debug.WriteLine($"Passes IsValidAppWindow: {passesFilter}");

                        if (!passesFilter)
                        {
                            // Диагностика почему не прошло фильтр
                            if (!isVisible)
                            {
                                System.Diagnostics.Debug.WriteLine("  ❌ Filtered: Not visible");
                            }
                            else if (IsWindowCloaked(hWnd))
                            {
                                System.Diagnostics.Debug.WriteLine("  ❌ Filtered: Cloaked");
                            }
                            else
                            {
                                int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
                                if ((exStyle & Win32Helper.WS_EX_TOOLWINDOW) != 0)
                                {
                                    System.Diagnostics.Debug.WriteLine("  ❌ Filtered: Tool window");
                                }
                                else
                                {
                                    IntPtr owner = Win32Helper.GetWindow(hWnd, Win32Helper.GW_OWNER);
                                    if (owner != IntPtr.Zero && (exStyle & Win32Helper.WS_EX_APPWINDOW) == 0)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"  ❌ Filtered: Has owner ({owner}) and no APPWINDOW flag");
                                    }
                                    else
                                    {
                                        System.Diagnostics.Debug.WriteLine("  ❌ Filtered: Other reason (class/title blacklist)");
                                    }
                                }
                            }
                            invalidWindows++;
                        }
                        else
                        {
                            string path = GetProcessExecutablePath(hWnd);
                            System.Diagnostics.Debug.WriteLine($"  ✅ VALID - Process: {path}");

                            if (!string.IsNullOrEmpty(path))
                            {
                                validWindows++;
                                Dispatcher.InvokeAsync(() =>
                                {
                                    OnShellWindowCreated(hWnd);

                                    // Запоминаем приложение для последующего обновления иконок
                                    var app = Apps.FirstOrDefault(a => a.WindowHandles.Contains(hWnd));
                                    if (app != null && !foundApps.Contains(app))
                                    {
                                        foundApps.Add(app);
                                        System.Diagnostics.Debug.WriteLine($"    📝 Added to found apps: {app.Name}");
                                    }
                                });
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("  ❌ No executable path found");
                            }
                        }
                        System.Diagnostics.Debug.WriteLine($"------------------------");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error processing window {hWnd}: {ex.Message}");
                }

                return true;
            }, IntPtr.Zero);

            System.Diagnostics.Debug.WriteLine($"=== SCAN COMPLETE ===");
            System.Diagnostics.Debug.WriteLine($"Total windows: {totalWindows}");
            System.Diagnostics.Debug.WriteLine($"Valid app windows: {validWindows}");
            System.Diagnostics.Debug.WriteLine($"Filtered windows: {invalidWindows}");
            System.Diagnostics.Debug.WriteLine($"Apps found: {foundApps.Count}");
            foreach (var app in foundApps)
            {
                System.Diagnostics.Debug.WriteLine($"  - {app.Name} (Running: {app.IsRunning})");
            }
            System.Diagnostics.Debug.WriteLine($"=======================");

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
                            // Проверяем если это UWP приложение
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

        private bool IsContextMenuOrPopup(IntPtr hWnd)
        {
            try
            {
                string className = Win32Helper.GetWindowClassName(hWnd);
                var sb = new StringBuilder(256);
                Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                // Получаем стили окна
                int style = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_STYLE);
                int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);

                System.Diagnostics.Debug.WriteLine($"Window check: '{title}' ({className})");
                System.Diagnostics.Debug.WriteLine($"  Style: 0x{style:X8}, ExStyle: 0x{exStyle:X8}");

                // 1. ГЛАВНАЯ ПРОВЕРКА: WS_POPUP флаг
                // Контекстные меню почти всегда создаются с WS_POPUP
                const int WS_POPUP = unchecked((int)0x80000000);
                if ((style & WS_POPUP) != 0)
                {
                    // Дополнительная проверка: не блокируем большие popup окна (диалоги)
                    Win32Helper.RECT rect;
                    if (Win32Helper.GetWindowRect(hWnd, out rect))
                    {
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;

                        // Если popup большой (>600x400) и имеет длинный заголовок - возможно диалог
                        if (width > 600 && height > 400 && !string.IsNullOrEmpty(title) && title.Length > 10)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Large popup dialog allowed: {width}x{height}");
                            return false;
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"  Filtered WS_POPUP window");
                    return true;
                }

                // 2. TOPMOST окна без основного окна = вероятно меню
                const int WS_EX_TOPMOST = 0x00000008;
                if ((exStyle & WS_EX_TOPMOST) != 0)
                {
                    IntPtr owner = Win32Helper.GetWindow(hWnd, Win32Helper.GW_OWNER);
                    if (owner != IntPtr.Zero)
                    {
                        // Topmost окно с владельцем - вероятно меню
                        System.Diagnostics.Debug.WriteLine($"  Filtered topmost owned window");
                        return true;
                    }
                }

                // 3. СТАНДАРТНЫЕ контекстные меню Windows
                if (className == "#32768")
                {
                    System.Diagnostics.Debug.WriteLine($"  Filtered Windows context menu");
                    return true;
                }

                // 4. TOOL WINDOW стиль - часто используется для popup элементов
                const int WS_EX_TOOLWINDOW = 0x00000080;
                if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                {
                    // Но tool window уже фильтруется в IsValidAppWindow, 
                    // это просто дополнительная подстраховка
                    Win32Helper.RECT rect;
                    if (Win32Helper.GetWindowRect(hWnd, out rect))
                    {
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;

                        if (width < 300 && height < 200)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Filtered small tool window");
                            return true;
                        }
                    }
                }

                // 5. ОЧЕНЬ маленькие окна независимо от стиля
                Win32Helper.RECT rectSmall;
                if (Win32Helper.GetWindowRect(hWnd, out rectSmall))
                {
                    int width = rectSmall.Right - rectSmall.Left;
                    int height = rectSmall.Bottom - rectSmall.Top;

                    if (width < 50 && height < 50)
                    {
                        System.Diagnostics.Debug.WriteLine($"  Filtered tiny window: {width}x{height}");
                        return true;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"  Window allowed");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IsContextMenuOrPopup error: {ex.Message}");
                return false;
            }
        }
    }
}