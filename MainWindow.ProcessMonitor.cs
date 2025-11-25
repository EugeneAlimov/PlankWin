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
        /// Перечисляет процессы, которые должны считаться "приложениями" (как в разделе Apps диспетчера задач).
        /// Ориентируемся на Process.MainWindowHandle / MainWindowTitle, плюс проверка стилей окна.
        /// </summary>
        private IEnumerable<TaskbarApp> EnumerateTaskbarWindows()
        {
            var result = new List<TaskbarApp>();

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                yield break;
            }

            int currentPid = Process.GetCurrentProcess().Id;

            foreach (var proc in processes)
            {
                try
                {
                    if (proc.Id == currentPid)
                        continue;

                    string processName = proc.ProcessName;
                    if (string.IsNullOrWhiteSpace(processName))
                        continue;

                    // Игнор по списку из конфига (если когда-то захочешь туда добавить что-то)
                    if (ShouldIgnoreProcess(processName))
                        continue;

                    string lower = processName.ToLowerInvariant();

                    // Экранная клавиатура / IME и прочий мусор
                    if (lower.Contains("textinputhost"))
                        continue;

                    bool isSettingsApp =
                        lower.Contains("systemsettings") ||
                        lower.Contains("immersivecontrolpanel");

                    // --- главное окно процесса, как его видит .NET (и примерно так же Task Manager) ---
                    IntPtr hwnd;
                    string title;
                    try
                    {
                        hwnd = proc.MainWindowHandle;
                        title = proc.MainWindowTitle ?? string.Empty;
                    }
                    catch
                    {
                        continue;
                    }

                    // Нет главного окна или заголовка — считаем фоновым
                    if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(title))
                        continue;

                    // Окно должно быть видимым и не cloaked
                    if (!IsWindowVisible(hwnd))
                        continue;
                    if (IsWindowCloaked(hwnd))
                        continue;
                    if (IsShellLikeWindow(hwnd))
                        continue;

                    // --- стили окна ---
                    IntPtr stylePtr = IntPtr.Size == 8
                        ? GetWindowLongPtr(hwnd, GWL_STYLE)
                        : new IntPtr(GetWindowLong(hwnd, GWL_STYLE));
                    IntPtr exStylePtr = IntPtr.Size == 8
                        ? GetWindowLongPtr(hwnd, GWL_EXSTYLE)
                        : new IntPtr(GetWindowLong(hwnd, GWL_EXSTYLE));

                    uint style = (uint)stylePtr.ToInt64();
                    uint exStyle = (uint)exStylePtr.ToInt64();

                    // TOOLWINDOW — служебное окно; для Settings делаем исключение
                    if ((exStyle & WS_EX_TOOLWINDOW) != 0 && !isSettingsApp)
                        continue;

                    IntPtr owner = GetWindow(hwnd, GW_OWNER);
                    if (owner != IntPtr.Zero && (exStyle & WS_EX_APPWINDOW) == 0 && !isSettingsApp)
                        continue;

                    bool hasCaption = (style & WS_CAPTION) != 0;
                    bool hasMinimize = (style & WS_MINIMIZEBOX) != 0;
                    if (!hasCaption && !hasMinimize && !isSettingsApp)
                        continue;

                    // --- путь к exe (для иконки и запуска) ---
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

                    result.Add(new TaskbarApp(processName, exePath, title, hwnd));
                }
                catch
                {
                    // игнорируем отдельные упавшие процессы
                }
            }

            foreach (var item in result)
                yield return item;
        }
    }
}
