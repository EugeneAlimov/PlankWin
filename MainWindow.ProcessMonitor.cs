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

                    case HSHELL_FLASH:
                        OnShellWindowFlash(targetHwnd);
                        break;

                    case HSHELL_WINDOWDESTROYED:
                        ClearAttentionForWindow(targetHwnd);
                        OnShellWindowDestroyed(targetHwnd);
                        break;

                    case HSHELL_WINDOWACTIVATED:
                    case HSHELL_RUDEAPPACTIVATED:
                        ClearAttentionForWindow(targetHwnd);
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

            // PWA (Chrome/Edge): окна PWA и обычного Chrome живут в одном процессе,
            // командная строка/AUMID окна не различают их. Признак PWA — заголовок БЕЗ суффикса браузера.
            string _exeName = System.IO.Path.GetFileName(exePath);
            bool _isChrome = _exeName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase);
            bool _isEdge = _exeName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase);
            if (_isChrome || _isEdge)
            {
                var _sb = new System.Text.StringBuilder(256); Win32Helper.GetWindowText(hWnd, _sb, 256);
                string _title = _sb.ToString();
                string _browser = _isEdge ? "edge" : "chrome";
                string _suffix = _isEdge ? " - Microsoft Edge" : " - Google Chrome";
                bool _isPwaWindow = !string.IsNullOrWhiteSpace(_title) && !_title.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase);
                if (_isPwaWindow)
                {
                    Win32Helper.GetWindowThreadProcessId(hWnd, out uint _pwaPid);
                    string _cmd = GetProcessCommandLine(_pwaPid);
                    string _appId = "";
                    string _profile = "Default";
                    var _m = System.Text.RegularExpressions.Regex.Match(_cmd, @"--app-id=([a-z]{32})");
                    var _mp = System.Text.RegularExpressions.Regex.Match(_cmd, "--profile-directory=([^\\s\"]+)");
                    if (_mp.Success) _profile = _mp.Groups[1].Value;
                    if (_m.Success)
                    {
                        _appId = _m.Groups[1].Value; // командную строку создал этот PWA
                    }
                    else
                    {
                        // Chrome создал процесс — app-id в cmd нет. Ищем по заголовку в Web Applications.
                        FindPwaByTitle(_browser, _title, out _appId, out _profile);
                    }
                    if (!string.IsNullOrEmpty(_appId))
                        identityPath = "pwa:" + _browser + ":" + _appId + "@" + _profile;
                }
            }

            // Сводим к shell:AppsFolder\<AUMID> для пакетных приложений. Два случая:
            //  1) классический UWP — окно-рамка ApplicationFrameWindow (Калькулятор и т.п.);
            //  2) любое приложение, чей exe лежит в WindowsApps (Claude/Electron, Store-приложения),
            //     у которого окно НЕ ApplicationFrameWindow — сырой exe-путь версионный и протухает
            //     после обновления приложения, давая «невидимую кнопку». AUMID стабилен.
            bool isFrame = Win32Helper.GetWindowClassName(hWnd) == "ApplicationFrameWindow";
            bool isPackaged = exePath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isFrame || isPackaged)
            {
                string winAumid = Win32Helper.GetAumidFromWindow(hWnd);
                if (!string.IsNullOrEmpty(winAumid))
                    identityPath = "shell:AppsFolder\\" + winAumid;
            }

            var targetApp = Apps.FirstOrDefault(a => a.Path.Equals(identityPath, StringComparison.OrdinalIgnoreCase));

            // САМОИСЦЕЛЕНИЕ протухших записей. Если для пакетного приложения точного совпадения
            // нет, но в конфиге есть закреплённая запись со СТАРЫМ сырым exe-путём в WindowsApps
            // того же пакета (например, осталась от прошлой версии Claude или от другой сборки) —
            // переписываем её путь на стабильный shell:AppsFolder\<AUMID> и используем её, вместо
            // создания второй кнопки. Совпадение по «семейству пакета» (имя_хешИздателя), которое
            // не меняется при обновлении приложения.
            if (targetApp == null && identityPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                string family = PackageFamilyFromAumidPath(identityPath);
                if (!string.IsNullOrEmpty(family))
                {
                    var stale = Apps.FirstOrDefault(a =>
                        a != null && !a.IsPlaceholder &&
                        a.Path != null &&
                        a.Path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        string.Equals(PackageFamilyFromExePath(a.Path), family, StringComparison.OrdinalIgnoreCase));
                    if (stale != null)
                    {
                        stale.Path = identityPath;   // лечим протухший путь
                        targetApp = stale;
                        SaveAppsConfig();
                    }
                }
            }

            // Ярлык лаунчера (Steam/Epic): exe угадан неточно или игра запустилась другим exe —
            // склеиваем по папке установки и запоминаем настоящий exe.
            if (targetApp == null && identityPath == exePath && !string.IsNullOrEmpty(exePath))
            {
                var byDir = Apps.FirstOrDefault(a => a != null && !a.IsPlaceholder && !string.IsNullOrEmpty(a.InstallDir)
                    && exePath.StartsWith(a.InstallDir!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                if (byDir != null)
                {
                    byDir.Path = exePath;
                    targetApp = byDir;
                    SaveAppsConfig();
                }
            }

            // ЗАЩИТА ОТ ЧЕХАРДЫ: приложение в процессе удаления (идёт анимация исчезновения).
            // Страховочный таймер не должен его трогать, иначе оживёт зомби-иконка.
            if (targetApp != null && targetApp.IsDeleting)
                return;

            if (targetApp != null)
            {
                if (!targetApp.WindowHandles.Contains(hWnd))
                {
                    targetApp.WindowHandles.Add(hWnd);
                    targetApp.NotifyWindowsChanged();
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
                // Схлопнуть до добавления — контейнер создастся уже невидимым (без мелькающего кадра).
                newApp.AppearScale = 0.0; newApp.AppearOpacity = 0.0;
                Apps.Add(newApp);
            }
        }

        private void OnShellWindowDestroyed(IntPtr hWnd)
        {
            var targetApp = Apps.FirstOrDefault(a => a.WindowHandles.Contains(hWnd));
            if (targetApp != null)
            {
                targetApp.WindowHandles.Remove(hWnd);
                targetApp.NotifyWindowsChanged();
                RemovePreviewSnapshot(hWnd);
                targetApp.RunningInstancesCount = targetApp.WindowHandles.Count;

                if (targetApp.RunningInstancesCount == 0)
                {
                    targetApp.IsRunning = false;
                    targetApp.WindowHandle = IntPtr.Zero;

                    if (targetApp.IsDynamic)
                    {
                        // Свой аниматор: иконка гаснет -> место стягивается -> панель сжимается. IsDeleting ставим в конце.
                        AnimatedStackPanel.IsAnimationSuspended = true; // соседи съезжают синхронно через размер ячейки
                        _dockAnimator.AnimateDisappear(targetApp,
                            () => PositionDock(skipAnimation: true),
                            () => { targetApp.IsDeleting = true; Apps.Remove(targetApp); AnimatedStackPanel.IsAnimationSuspended = false; });
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
        // Извлекает «семейство пакета» (Name_PublisherHash) из AUMID-пути вида
        // "shell:AppsFolder\Claude_pzs8sxrjxfjjc!App" -> "Claude_pzs8sxrjxfjjc".
        private static string PackageFamilyFromAumidPath(string aumidPath)
        {
            if (string.IsNullOrEmpty(aumidPath)) return string.Empty;
            const string prefix = "shell:AppsFolder\\";
            string s = aumidPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? aumidPath.Substring(prefix.Length) : aumidPath;
            int bang = s.IndexOf('!');
            if (bang >= 0) s = s.Substring(0, bang); // до '!' — PackageFamilyName
            return s.Trim();
        }

        // Извлекает «семейство пакета» из сырого exe-пути в WindowsApps вида
        // "...\WindowsApps\Claude_1.24012.11.0_x64__pzs8sxrjxfjjc\app\claude.exe"
        // Папка пакета: "Name_Version_Arch__PublisherHash" -> "Name_PublisherHash".
        private static string PackageFamilyFromExePath(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return string.Empty;
            const string marker = "\\WindowsApps\\";
            int i = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return string.Empty;
            string rest = exePath.Substring(i + marker.Length);
            int slash = rest.IndexOf('\\');
            string folder = slash >= 0 ? rest.Substring(0, slash) : rest; // Name_Version_Arch__PublisherHash
            int us = folder.IndexOf('_');
            int dhh = folder.IndexOf("__", StringComparison.Ordinal);
            if (us < 0 || dhh < 0 || dhh + 2 >= folder.Length) return string.Empty;
            string name = folder.Substring(0, us);
            string pubHash = folder.Substring(dhh + 2);
            return name + "_" + pubHash;
        }

        private void PruneDeadWindows()
        {
            foreach (var app in Apps.ToList())
            {
                if (app.IsDeleting) continue;
                if (app.WindowHandles.Count == 0) continue;

                // «Мёртвые» для этой панели: закрытые ИЛИ переехавшие на другой монитор
                // (там их подхватит RefreshRunningApps соседней панели).
                var dead = app.WindowHandles.Where(h => !Win32Helper.IsWindow(h) || !IsOnMyMonitor(h)).ToList();
                if (dead.Count == 0) continue;

                foreach (var h in dead) app.WindowHandles.Remove(h);
                app.NotifyWindowsChanged();
                app.RunningInstancesCount = app.WindowHandles.Count;

                if (app.RunningInstancesCount == 0)
                {
                    app.IsRunning = false;
                    app.WindowHandle = IntPtr.Zero;

                    if (app.IsDynamic)
                    {
                        var appToRemove = app;
                        AnimatedStackPanel.IsAnimationSuspended = true;
                        _dockAnimator.AnimateDisappear(appToRemove,
                            () => PositionDock(skipAnimation: true),
                            () => { appToRemove.IsDeleting = true; Apps.Remove(appToRemove); AnimatedStackPanel.IsAnimationSuspended = false; });
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
                CacheWindowSnapshot(hWnd);
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
            // Несколько мониторов: каждая панель показывает только окна своего монитора.
            if (!IsOnMyMonitor(hWnd)) return false;
            return true;
        }

        // Окно на мониторе этой панели? (Свёрнутое — по месту до сворачивания: так работает MonitorFromWindow.)
        private bool IsOnMyMonitor(IntPtr hWnd)
        {
            try
            {
                if (System.Windows.Forms.Screen.AllScreens.Length < 2) return true;
                return System.Windows.Forms.Screen.FromHandle(hWnd).DeviceName == _profileName;
            }
            catch { return true; }
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

        // Поиск app-id PWA по заголовку окна в папке Web Applications (когда --app-id нет в cmd).
        private readonly Dictionary<string, (string appId, string profile)> _pwaTitleCache = new();
        private bool FindPwaByTitle(string browser, string title, out string appId, out string profile)
        {
            appId = ""; profile = "Default";
            if (string.IsNullOrWhiteSpace(title)) return false;
            if (_pwaTitleCache.TryGetValue(title, out var hit)) { appId = hit.appId; profile = hit.profile; return !string.IsNullOrEmpty(appId); }
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string userData = browser == "edge"
                    ? System.IO.Path.Combine(local, "Microsoft", "Edge", "User Data")
                    : System.IO.Path.Combine(local, "Google", "Chrome", "User Data");
                if (System.IO.Directory.Exists(userData))
                {
                    foreach (var profDir in System.IO.Directory.GetDirectories(userData))
                    {
                        string webApps = System.IO.Path.Combine(profDir, "Web Applications");
                        if (!System.IO.Directory.Exists(webApps)) continue;
                        foreach (var crx in System.IO.Directory.GetDirectories(webApps, "_crx_*"))
                        {
                            foreach (var ico in System.IO.Directory.GetFiles(crx, "*.ico", System.IO.SearchOption.AllDirectories))
                            {
                                string nm = System.IO.Path.GetFileNameWithoutExtension(ico);
                                if (string.IsNullOrWhiteSpace(nm)) continue;
                                if (title.IndexOf(nm, StringComparison.OrdinalIgnoreCase) >= 0 || nm.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    appId = System.IO.Path.GetFileName(crx).Substring("_crx_".Length);
                                    profile = System.IO.Path.GetFileName(profDir);
                                    _pwaTitleCache[title] = (appId, profile);
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            _pwaTitleCache[title] = ("", "Default");
            return false;
        }

        // Командная строка процесса (WMI). Нужна для распознавания PWA (chrome --app-id=<hash>).
        private readonly Dictionary<uint, string> _cmdLineCache = new();
        private string GetProcessCommandLine(uint pid)
        {
            if (pid == 0) return string.Empty;
            if (_cmdLineCache.TryGetValue(pid, out var cached)) return cached;
            string result = string.Empty;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid);
                foreach (System.Management.ManagementObject mo in searcher.Get())
                {
                    result = mo["CommandLine"]?.ToString() ?? string.Empty;
                    break;
                }
            }
            catch { }
            _cmdLineCache[pid] = result;
            return result;
        }
    }
}