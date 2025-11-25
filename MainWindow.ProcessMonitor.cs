using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Threading;

namespace PlankWin
{
    public partial class MainWindow
    {
        private void StartProcessMonitor()
        {
            _processPollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _processPollTimer.Tick += (_, _) => UpdateRunningState();
            _processPollTimer.Start();

            UpdateRunningState();
        }

        private static bool MatchesTaskbarEntry(DockAppConfig app, TaskbarApp tb)
        {
            // 1. Явное совпадение по ProcessName
            if (!string.IsNullOrWhiteSpace(app.ProcessName) &&
                string.Equals(app.ProcessName, tb.ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;

            // 2. Совпадение по имени exe, если путь указан
            try
            {
                if (!string.IsNullOrWhiteSpace(app.Path))
                {
                    var exe = System.IO.Path.GetFileNameWithoutExtension(app.Path);
                    if (!string.IsNullOrWhiteSpace(exe) &&
                        string.Equals(exe, tb.ProcessName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
            }

            // 3. Совпадение по названию (для динамических приложений)
            if (!string.IsNullOrWhiteSpace(app.Name) &&
                !string.IsNullOrWhiteSpace(tb.Title) &&
                string.Equals(app.Name, tb.Title, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private void UpdateRunningState()
        {
            var taskbarApps = new List<TaskbarApp>(EnumerateTaskbarWindows());

            // Обновляем IsRunning и WindowHandle для закреплённых приложений
            foreach (var app in Apps)
            {
                bool running = false;
                app.WindowHandle = IntPtr.Zero;

                foreach (var tb in taskbarApps)
                {
                    if (MatchesTaskbarEntry(app, tb))
                    {
                        running = true;
                        app.WindowHandle = tb.Hwnd;
                        break;
                    }
                }

                app.IsRunning = running;
            }

            // Удаляем динамические, которые больше не запущены
            for (int i = Apps.Count - 1; i >= 0; i--)
            {
                var app = Apps[i];
                if (!app.IsDynamic)
                    continue;

                bool stillRunning = false;
                foreach (var tb in taskbarApps)
                {
                    if (MatchesTaskbarEntry(app, tb))
                    {
                        stillRunning = true;
                        break;
                    }
                }

                if (!stillRunning)
                {
                    Apps.RemoveAt(i);
                }
            }

            // Добавляем динамические для новых приложений
            foreach (var tb in taskbarApps)
            {
                // 1. Уже покрыто закреплённым приложением
                bool coveredByPinned = false;
                foreach (var app in Apps)
                {
                    if (!app.IsDynamic && MatchesTaskbarEntry(app, tb))
                    {
                        coveredByPinned = true;
                        break;
                    }
                }

                if (coveredByPinned)
                    continue;

                // 2. Уже есть динамический элемент для этого процесса
                bool alreadyDynamic = false;
                foreach (var app in Apps)
                {
                    if (app.IsDynamic && MatchesTaskbarEntry(app, tb))
                    {
                        alreadyDynamic = true;
                        app.IsRunning = true;
                        app.WindowHandle = tb.Hwnd;

                        if (!string.IsNullOrWhiteSpace(tb.ExePath) &&
                            string.IsNullOrWhiteSpace(app.Path))
                        {
                            app.Path = tb.ExePath;
                        }

                        break;
                    }
                }

                if (alreadyDynamic)
                    continue;

                // 3. Новый динамический элемент
                var dynApp = new DockAppConfig
                {
                    Name = string.IsNullOrWhiteSpace(tb.Title) ? tb.ProcessName : tb.Title,
                    Path = tb.ExePath ?? string.Empty,
                    ProcessName = tb.ProcessName,
                    IsDynamic = true,
                    IsRunning = true,
                    WindowHandle = tb.Hwnd
                };

                Apps.Add(dynApp);
            }
        }

        private sealed class TaskbarApp
        {
            public string ProcessName { get; }
            public string? ExePath { get; }
            public string Title { get; }
            public IntPtr Hwnd { get; }

            public TaskbarApp(string processName, string? exePath, string title, IntPtr hwnd)
            {
                ProcessName = processName;
                ExePath = exePath;
                Title = title;
                Hwnd = hwnd;
            }
        }

        /// <summary>
        /// Проверяем, нужно ли игнорировать процесс по имени из настроек.
        /// Сравнение без учёта регистра, по подстроке.
        /// </summary>
        private bool ShouldIgnoreProcess(string processName)
        {
            if (_settings?.IgnoreProcessNames == null || _settings.IgnoreProcessNames.Length == 0)
                return false;

            foreach (var raw in _settings.IgnoreProcessNames)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var pattern = raw.Trim();
                if (processName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Перечисляет окна, которые должны быть на панели задач (и в доке).
        /// Используем EnumWindows + фильтрацию по стилям, плюс спец-логика для Nahimic.
        /// </summary>
        private IEnumerable<TaskbarApp> EnumerateTaskbarWindows()
        {
            var result = new Dictionary<int, TaskbarApp>();
            bool explorerFound = false;

            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    // Невидимые и cloaked окна не считаем "приложениями"
                    if (!IsWindowVisible(hWnd))
                        return true;

                    if (IsWindowCloaked(hWnd))
                        return true;

                    if (IsShellLikeWindow(hWnd))
                        return true;

                    int textLen = GetWindowTextLength(hWnd);
                    if (textLen == 0)
                        return true;

                    var sbTitle = new StringBuilder(textLen + 1);
                    GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                    string title = sbTitle.ToString();
                    if (string.IsNullOrWhiteSpace(title))
                        return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == 0)
                        return true;

                    Process proc;
                    try
                    {
                        proc = Process.GetProcessById((int)pid);
                    }
                    catch
                    {
                        return true;
                    }

                    string processName = proc.ProcessName;
                    if (string.IsNullOrWhiteSpace(processName))
                        return true;

                    // Игнор по списку из конфига
                    if (ShouldIgnoreProcess(processName))
                        return true;

                    string lower = processName.ToLowerInvariant();

                    // Явно игнорируем служебную экранную клавиатуру / IME
                    if (lower.Contains("textinputhost"))
                        return true;

                    // Спец-флаг: это системное приложение "Настройки"
                    bool isSettingsApp =
                        lower.Contains("systemsettings") ||
                        lower.Contains("immersivecontrolpanel");

                    // -------- Спец-логика для Nahimic --------
                    bool isNahimic = lower.Contains("nahimic");
                    if (isNahimic)
                    {
                        IntPtr mainHandle = IntPtr.Zero;
                        try
                        {
                            mainHandle = proc.MainWindowHandle;
                        }
                        catch
                        {
                            mainHandle = IntPtr.Zero;
                        }

                        // Нет главного окна → считаем фоном, полностью игнорируем процесс
                        if (mainHandle == IntPtr.Zero)
                            return true;

                        // Это не главное окно процесса → не учитываем
                        if (hWnd != mainHandle)
                            return true;
                    }

                    // -------- стили окна --------
                    IntPtr stylePtr = IntPtr.Size == 8
                        ? GetWindowLongPtr(hWnd, GWL_STYLE)
                        : new IntPtr(GetWindowLong(hWnd, GWL_STYLE));
                    IntPtr exStylePtr = IntPtr.Size == 8
                        ? GetWindowLongPtr(hWnd, GWL_EXSTYLE)
                        : new IntPtr(GetWindowLong(hWnd, GWL_EXSTYLE));

                    uint style = (uint)stylePtr.ToInt64();
                    uint exStyle = (uint)exStylePtr.ToInt64();

                    // TOOLWINDOW — служебное окно; но для Settings делаем исключение
                    if ((exStyle & WS_EX_TOOLWINDOW) != 0 && !isSettingsApp)
                        return true;

                    IntPtr owner = GetWindow(hWnd, GW_OWNER);
                    if (owner != IntPtr.Zero && (exStyle & WS_EX_APPWINDOW) == 0 && !isSettingsApp)
                        return true;

                    bool hasCaption = (style & WS_CAPTION) != 0;
                    bool hasMinimize = (style & WS_MINIMIZEBOX) != 0;
                    if (!hasCaption && !hasMinimize && !isSettingsApp)
                        return true;

                    // Отмечаем, что explorer найден
                    if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase))
                        explorerFound = true;

                    string? exePath = null;

                    try
                    {
                        exePath = proc.MainModule?.FileName;
                    }
                    catch
                    {
                        exePath = null;
                    }

                    if (string.IsNullOrEmpty(exePath))
                    {
                        try
                        {
                            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, proc.Id);
                            if (hProcess != IntPtr.Zero)
                            {
                                var sbPath = new StringBuilder(1024);
                                int size = sbPath.Capacity;
                                if (QueryFullProcessImageName(hProcess, 0, sbPath, ref size))
                                {
                                    exePath = sbPath.ToString(0, size);
                                }
                                CloseHandle(hProcess);
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (!result.ContainsKey(proc.Id))
                    {
                        result[proc.Id] = new TaskbarApp(processName, exePath, title, hWnd);
                    }
                }
                catch
                {
                }

                return true;
            }, IntPtr.Zero);

            // На всякий случай гарантируем наличие Explorer (File Explorer)
            if (!explorerFound)
            {
                try
                {
                    var explorers = Process.GetProcessesByName("explorer");
                    if (explorers.Length > 0)
                    {
                        var exProc = explorers[0];
                        if (!result.ContainsKey(exProc.Id))
                        {
                            string? exePath = null;

                            try
                            {
                                exePath = exProc.MainModule?.FileName;
                            }
                            catch
                            {
                                exePath = null;
                            }

                            if (string.IsNullOrEmpty(exePath))
                            {
                                try
                                {
                                    IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, exProc.Id);
                                    if (hProcess != IntPtr.Zero)
                                    {
                                        var sbPath = new StringBuilder(1024);
                                        int size = sbPath.Capacity;
                                        if (QueryFullProcessImageName(hProcess, 0, sbPath, ref size))
                                        {
                                            exePath = sbPath.ToString(0, size);
                                        }
                                        CloseHandle(hProcess);
                                    }
                                }
                                catch
                                {
                                }
                            }

                            result[exProc.Id] = new TaskbarApp("explorer", exePath, "File Explorer", IntPtr.Zero);
                        }
                    }
                }
                catch
                {
                }
            }

            return result.Values;
        }
    }
}
