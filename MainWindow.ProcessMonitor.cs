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
            if (!string.IsNullOrWhiteSpace(app.ProcessName) &&
                string.Equals(app.ProcessName, tb.ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;

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

            if (!string.IsNullOrWhiteSpace(app.Name) &&
                !string.IsNullOrWhiteSpace(tb.Title) &&
                string.Equals(app.Name, tb.Title, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private void UpdateRunningState()
        {
            var taskbarApps = new List<TaskbarApp>(EnumerateTaskbarWindows());

            // Обновляем IsRunning и WindowHandle для всех
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
        /// Сравнение без учета регистра, по подстроке.
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
        /// Перечисляет процессы, у которых есть главное окно (как в разделе Apps диспетчера задач).
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

                    // Игнор по настройкам (Nahimic и т.п.)
                    if (ShouldIgnoreProcess(processName))
                        continue;

                    string lower = processName.ToLowerInvariant();

                    // Явно игнорируем служебную экранную клавиатуру / IME
                    if (lower.Contains("textinputhost"))
                        continue;

                    // Спец-флаг: это системное приложение "Настройки"
                    bool isSettingsApp =
                        lower.Contains("systemsettings") ||
                        lower.Contains("immersivecontrolpanel");

                    // Главное окно процесса (как его видит .NET / Task Manager)
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

                    // Нет главного окна или заголовка — считаем фоновым процессом
                    if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(title))
                        continue;

                    // Окно должно быть видимым и не быть shell-окном
                    if (!IsWindowVisible(hwnd))
                        continue;
                    if (IsShellLikeWindow(hwnd))
                        continue;

                    // Стили окна — фильтр "как на панели задач"
                    IntPtr stylePtr = IntPtr.Size == 8
                        ? GetWindowLongPtr(hwnd, GWL_STYLE)
                        : new IntPtr(GetWindowLong(hwnd, GWL_STYLE));
                    IntPtr exStylePtr = IntPtr.Size == 8
                        ? GetWindowLongPtr(hwnd, GWL_EXSTYLE)
                        : new IntPtr(GetWindowLong(hwnd, GWL_EXSTYLE));

                    uint style = (uint)stylePtr.ToInt64();
                    uint exStyle = (uint)exStylePtr.ToInt64();

                    // TOOLWINDOW — служебное окно; но для Settings делаем исключение
                    if ((exStyle & WS_EX_TOOLWINDOW) != 0 && !isSettingsApp)
                        continue;

                    IntPtr owner = GetWindow(hwnd, GW_OWNER);
                    if (owner != IntPtr.Zero && (exStyle & WS_EX_APPWINDOW) == 0 && !isSettingsApp)
                        continue;

                    bool hasCaption = (style & WS_CAPTION) != 0;
                    bool hasMinimize = (style & WS_MINIMIZEBOX) != 0;
                    if (!hasCaption && !hasMinimize && !isSettingsApp)
                        continue;

                    // Путь к exe (для иконки и запуска)
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
                    // игнорируем упавшие процессы
                }
            }

            foreach (var item in result)
                yield return item;
        }
    }
}
