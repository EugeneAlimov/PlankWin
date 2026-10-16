using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace PlankWin
{
    // Бейджи-счётчики на иконках. Перехватить ITaskbarList3 (оверлеи таскбара) чужих программ нельзя,
    // поэтому берём число из заголовков окон — так делают мессенджеры и веб-почта:
    // «Telegram (5)», «(3) WhatsApp», «Inbox (12) - …@gmail.com - Gmail».
    public partial class MainWindow
    {
        private DispatcherTimer? _badgeTimer;

        // «(N) Заголовок» — веб-приложения во вкладке браузера (WhatsApp, YouTube, X, Facebook, Discord…).
        private static readonly Regex BadgeLeading = new(@"^\((\d{1,4})\)\s+\S", RegexOptions.Compiled);
        // Мессенджеры: имя программы и число в скобках («Telegram (5)», «Viber (2)»).
        private static readonly Regex BadgeMessenger = new(@"^(?:Telegram|Viber|Signal|Skype|Slack|Discord|WhatsApp|Element|Teams)\b[^()]*\((\d{1,4})\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Входящие веб-почты («Inbox (12)», «Входящие (3)»…).
        private static readonly Regex BadgeInbox = new(@"^(?:Inbox|Входящие|Вхідні|Posteingang|Beérkezett üzenetek|Odebrane)\s*\((\d{1,6})\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private void StartBadgeTimer()
        {
            if (_badgeTimer != null) return;
            _badgeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
            _badgeTimer.Tick += (s, e) => UpdateBadges();
            _badgeTimer.Start();
        }

        private void StopBadgeTimer()
        {
            _badgeTimer?.Stop();
            _badgeTimer = null;
        }

        private void UpdateBadges()
        {
            bool on = GlobalConfig.ShowBadges;
            var sb = new StringBuilder(512);
            foreach (var app in Apps.ToList())
            {
                int total = 0;
                if (on && app.IsRunning && !app.IsFavorites && !IsExplorerApp(app)) // у Проводника «(2)» — это имена папок
                {
                    foreach (var h in app.WindowHandles.ToList())
                    {
                        sb.Clear();
                        Win32Helper.GetWindowText(h, sb, sb.Capacity);
                        total += ParseBadge(sb.ToString());
                    }
                }
                if (app.BadgeCount != total) app.BadgeCount = total;
            }
        }

        public static int ParseBadge(string title)
        {
            if (string.IsNullOrEmpty(title)) return 0;
            foreach (var rx in new[] { BadgeMessenger, BadgeInbox, BadgeLeading })
            {
                var m = rx.Match(title);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) return n;
            }
            return 0;
        }
    }
}
