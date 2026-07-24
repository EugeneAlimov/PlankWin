using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using System.Windows;
using System.Text;
using System.Threading.Tasks; // Добавили для Task.Delay
using System.Windows.Threading; // Для DispatcherTimer (страховочный таймер гибридной модели)

namespace PlankWin
{
    public partial class MainWindow
    {
        private int _shellHookMessage;
        private HwndSource? _hwndSource;

        // ГИБРИДНАЯ МОДЕЛЬ: Shell Hook (события) + страховочный таймер.
        // События иногда промахиваются (многопроцессные приложения вроде Firefox/Chrome:
        // окно принадлежит дочернему процессу, путь не совпал при первом событии -> окно потеряно).
        // Таймер раз в ~1с пересматривает окна через RefreshRunningApps() и подбирает пропущенное.
        // Интервал намеренно большой (не 200мс старого сканера), чтобы не грузить CPU.
        private DispatcherTimer? _safetyTimer;

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

                // Запускаем страховочный таймер гибридной модели.
                // Сначала убираем закрытые окна (события destroy у многопроцессных
                // приложений вроде Claude/Electron ненадёжны), затем подбираем новые.
                _safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
                _safetyTimer.Tick += (s, e) => { PruneDeadWindows(); RefreshRunningApps(); };
                _safetyTimer.Start();
            }
        }

        private void StopProcessMonitor()
        {
            _safetyTimer?.Stop();
            _safetyTimer = null;

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
            // ИСКЛЮЧЕНИЕ: СВЁРНУТЫЙ UWP. У него дочернего CoreWindow нет (приложение
            // приостановлено), поэтому путь ВСЕГДА будет ApplicationFrameHost — сколько ни жди.
            // AUMID при этом читается по парному CoreWindow, и его достаточно для опознания.
            // Условие намеренно узкое (только свёрнутые), чтобы не пропускать служебные окна.
            if (exePath.Contains("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                string frameAumid = Win32Helper.IsIconic(hWnd)
                    ? Win32Helper.GetAumidFromWindow(hWnd)
                    : string.Empty;

                if (string.IsNullOrEmpty(frameAumid))
                {
                    if (retryCount < 10)
                    {
                        Task.Delay(200).ContinueWith(_ => Dispatcher.InvokeAsync(() => OnShellWindowCreated(hWnd, retryCount + 1)));
                    }
                    return;
                }
            }

            // Для UWP-окна идентификатором делаем стабильный shell:AppsFolder\<AUMID>,
            // а не сырой путь к exe внутри WindowsApps. Тогда после закрепления/закрытия
            // иконка достаётся (конвертер это умеет), запуск идёт через активацию по AUMID,
            // и запись совпадает с закреплением того же UWP через drag-in (без дубля кнопок).
            string identityPath = exePath;
            if (Win32Helper.GetWindowClassName(hWnd) == "ApplicationFrameWindow")
            {
                string winAumid = Win32Helper.GetAumidFromWindow(hWnd);
                if (!string.IsNullOrEmpty(winAumid))
                    identityPath = "shell:AppsFolder\\" + winAumid;
            }

            var targetApp = Apps.FirstOrDefault(a => a.Path.Equals(identityPath, StringComparison.OrdinalIgnoreCase));

            // ЗАЩИТА ОТ ЧЕХАРДЫ: приложение в процессе удаления (идёт анимация исчезновения).
            // Страховочный таймер не должен его трогать, иначе оживёт зомби-иконка.
            if (targetApp != null && targetApp.IsDeleting)
                return;

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
                string displayName = processName;

                // Свёрнутый UWP: exe — это ApplicationFrameHost, такое имя на кнопке
                // выглядело бы мусором. Берём заголовок окна ("Calculator" и т.п.).
                if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                {
                    var sbTitle = new StringBuilder(256);
                    Win32Helper.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                    string title = sbTitle.ToString().Trim();
                    if (!string.IsNullOrEmpty(title)) displayName = title;
                }

                var newApp = new DockAppConfig
                {
                    Name = displayName,
                    Path = identityPath,
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
                    targetApp.ForceIconUpdate(); // Добавил пинок, чтобы после закрытия окна иконка обновлялась на следующее
                }
            }
        }

        // Уборка «мёртвых» окон: у многопроцессных приложений (Claude/Electron, браузеры)
        // событие HSHELL_WINDOWDESTROYED часто не приходит по отслеживаемому hWnd, и в
        // WindowHandles остаётся закрытый дескриптор -> зависшие точки, «активная» кнопка
        // после закрытия/убийства процессов, невозможность открыть простым кликом.
        // Раз в тик страховочного таймера отсеиваем hWnd, которых уже нет (IsWindow == false),
        // применяя ту же логику, что и OnShellWindowDestroyed.
        private void PruneDeadWindows()
        {
            foreach (var app in Apps.ToList())
            {
                if (app.IsDeleting) continue;
                if (app.WindowHandles.Count == 0) continue;

                var dead = app.WindowHandles.Where(h => !Win32Helper.IsWindow(h)).ToList();
                if (dead.Count == 0) continue;

                foreach (var h in dead) app.WindowHandles.Remove(h);
                app.RunningInstancesCount = app.WindowHandles.Count;

                if (app.RunningInstancesCount == 0)
                {
                    app.IsRunning = false;
                    app.WindowHandle = IntPtr.Zero;

                    if (app.IsDynamic)
                    {
                        app.IsDeleting = true;
                        PositionDock(skipAnimation: false);
                        var appToRemove = app;
                        Dispatcher.InvokeAsync(async () =>
                        {
                            await AnimateItemContainerAsync(appToRemove, targetSize: 0, animateOpacity: true, durationMs: 350);
                            Apps.Remove(appToRemove);
                        });
                    }
                }
                else
                {
                    app.WindowHandle = app.WindowHandles.Last();
                    app.ForceIconUpdate();
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
        // ЖЕЛЕЗОБЕТОННЫЙ ФИЛЬТР ОКОН
        // =========================================================
        private bool IsValidAppWindow(IntPtr hWnd)
        {
            if (!Win32Helper.IsWindowVisible(hWnd)) return false;

            // Свёрнутый UWP тоже cloaked — но он реально запущен и должен быть на доке.
            // Отличаем его от фоновых/приостановленных UWP-коробок по признаку "свёрнуто":
            // свёрнутое окно = cloaked И iconic, фоновая коробка = cloaked, но не iconic.
            if (IsWindowCloaked(hWnd) && !Win32Helper.IsIconic(hWnd)) return false;

            int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
            if ((exStyle & Win32Helper.WS_EX_TOOLWINDOW) != 0) return false;

            IntPtr owner = Win32Helper.GetWindow(hWnd, Win32Helper.GW_OWNER);
            if (owner != IntPtr.Zero && (exStyle & Win32Helper.WS_EX_APPWINDOW) == 0) return false;

            string className = Win32Helper.GetWindowClassName(hWnd);

            // 1. ЧЕРНЫЙ СПИСОК КЛАССОВ (Включая LivePreviewSurface и все элементы таскбара)
            string[] ignoredClasses = {
                "Progman", "WorkerW", "Shell_TrayWnd", "TrayNotifyWnd",
                "LivePreviewSurface", "ThumbnailDeviceHelperWnd",
                "TaskSwitchWnd", "MultitaskingViewFrame", "Windows.UI.Core.CoreWindow",
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
    }
}