using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PlankWin
{
    // Отчёты об ошибках без серверов: необработанное исключение пишется в crash.log (+ флаг crash.pending),
    // при следующем запуске показываем окно с текстом отчёта: «Скопировать» (переслать автору в мессенджер)
    // или «Открыть Issue на GitHub» (заполненный черновик в браузере). Ничего не уходит без действия пользователя.
    public static class CrashReporter
    {
        private const string IssueUrl = "https://github.com/EugeneAlimov/PlankWin/issues/new";
        private static string LogPath => Path.Combine(PlankConfig.GetDataDir(), "crash.log");
        private static string PendingPath => Path.Combine(PlankConfig.GetDataDir(), "crash.pending");
        private static bool _writing;

        public static void Install(Application app)
        {
            // Ошибка в UI-потоке: пишем отчёт и продолжаем работу — панель без дока хуже, чем панель после сбоя.
            app.DispatcherUnhandledException += (s, e) => { Write("UI", e.Exception); e.Handled = true; };
            // Фоновые потоки: процесс всё равно завершится, успеваем записать.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { if (e.ExceptionObject is Exception ex) Write("Background", ex); };
            TaskScheduler.UnobservedTaskException += (s, e) => { Write("Task", e.Exception); e.SetObserved(); };
        }

        public static string Describe(string source, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"PlankWin {Updater.CurrentText}{(Updater.IsDevBuild ? " (dev)" : "")}");
            sb.AppendLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Источник: {source}");
            sb.AppendLine($"Windows: {Environment.OSVersion.Version} ({(Win32Helper.IsWin11 ? "11" : "10")}), {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
            try
            {
                var scr = System.Windows.Forms.Screen.AllScreens;
                sb.AppendLine($"Мониторы: {scr.Length} ({string.Join(", ", Array.ConvertAll(scr, x => $"{x.Bounds.Width}×{x.Bounds.Height}{(x.Primary ? "*" : "")}"))})");
            }
            catch { }
            sb.AppendLine($"Язык: {Loc.Lang}");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            return sb.ToString();
        }

        private static void Write(string source, Exception ex)
        {
            if (_writing) return; // ошибка внутри записи отчёта — не уходим в рекурсию
            _writing = true;
            try
            {
                string entry = Describe(source, ex);
                // Лог не растёт бесконечно: держим последние ~200 КБ.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 200_000) File.Delete(LogPath);
                File.AppendAllText(LogPath, entry + Environment.NewLine + new string('-', 60) + Environment.NewLine);
                // Для окна при следующем запуске — только первая ошибка сеанса (дальше обычно лавина следствий).
                if (!File.Exists(PendingPath)) File.WriteAllText(PendingPath, entry);
            }
            catch { }
            finally { _writing = false; }
        }

        // Вызывается при старте: была ошибка в прошлом сеансе — показываем окно отчёта.
        public static void ShowPendingIfAny()
        {
            string text;
            try
            {
                if (!File.Exists(PendingPath)) return;
                text = File.ReadAllText(PendingPath);
                File.Delete(PendingPath);
            }
            catch { return; }
            if (string.IsNullOrWhiteSpace(text)) return;
            try { BuildWindow(text).Show(); } catch { }
        }

        private static Window BuildWindow(string report)
        {
            Brush Res(string k, Brush fb) => Application.Current.TryFindResource(k) as Brush ?? fb;
            var fg = Res("MenuTextBrush", Brushes.Black);

            var w = new Window
            {
                Title = Loc.T("crash_title"),
                Width = 620, Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Res("MenuBgBrush", Brushes.White),
                Foreground = fg,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                ShowInTaskbar = true,
                Topmost = true
            };

            var root = new DockPanel { Margin = new Thickness(16) };

            var intro = new TextBlock { Text = Loc.T("crash_text"), TextWrapping = TextWrapping.Wrap, Foreground = fg, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            Button Btn(string text) { var b = new Button { Content = text, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 }; buttons.Children.Add(b); return b; }

            var copy = Btn(Loc.T("crash_copy"));
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(report); copy.Content = Loc.T("crash_copied"); } catch { }
            };
            var issue = Btn(Loc.T("crash_issue"));
            issue.Click += (s, e) =>
            {
                // URL ограничен по длине: в черновик идёт начало отчёта, полный текст — кнопкой «Скопировать».
                string body = report.Length > 2500 ? report.Substring(0, 2500) + "\n…" : report;
                // Заголовок — первая строка исключения (она идёт после пустой строки-разделителя).
                var lines = report.Split('\n');
                int ei = Array.FindIndex(lines, l => l.Trim().Length == 0) + 1;
                string firstLine = ei > 0 && ei < lines.Length ? lines[ei].Trim() : "Crash";
                string title = "Crash: " + (firstLine.Length > 90 ? firstLine.Substring(0, 90) : firstLine);
                string url = $"{IssueUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString("```\n" + body + "\n```")}";
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            };
            var close = Btn(Loc.T("crash_close"));
            close.IsCancel = true;
            close.Click += (s, e) => w.Close();
            root.Children.Add(buttons);

            var box = new TextBox
            {
                Text = report,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                Foreground = fg,
                BorderBrush = Res("MenuBorderBrush", Brushes.Gray)
            };
            root.Children.Add(box);

            w.Content = root;
            return w;
        }
    }
}
