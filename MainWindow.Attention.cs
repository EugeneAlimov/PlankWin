using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace PlankWin
{
    public partial class MainWindow
    {
        // ===== Внимание: окно «мигает» на таскбаре (FlashWindow) — иконка на доке подпрыгивает =====
        // Прыжок сразу и потом каждые 3 с, пока окно не активируют (как в macOS), но не дольше ~30 с.
        private const int HSHELL_FLASH = 0x8006;
        private readonly Dictionary<DockAppConfig, DispatcherTimer> _attention = new();

        private void OnShellWindowFlash(IntPtr hWnd)
        {
            var app = Apps.FirstOrDefault(a => a.WindowHandles.Contains(hWnd));
            if (app == null || app.IsDeleting) return;
            if (Win32Helper.GetForegroundWindow() == hWnd) return; // пользователь и так в этом окне
            if (_attention.ContainsKey(app)) return;               // уже прыгает

            int bounces = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (s, e) =>
            {
                if (++bounces >= 10 || !Apps.Contains(app)) { StopAttention(app); return; }
                BounceForAttention(app);
            };
            _attention[app] = t;
            BounceForAttention(app);
            t.Start();
        }

        private void BounceForAttention(DockAppConfig app)
        {
            if (FindAppButton(app) is System.Windows.Controls.Button btn && btn.IsVisible)
                AnimateLaunchBounce(btn);
        }

        private void StopAttention(DockAppConfig app)
        {
            if (_attention.TryGetValue(app, out var t)) { t.Stop(); _attention.Remove(app); }
        }

        // Окно активировали или закрыли — внимание снято.
        private void ClearAttentionForWindow(IntPtr hWnd)
        {
            foreach (var app in _attention.Keys.ToList())
                if (app.WindowHandles.Contains(hWnd) || !Apps.Contains(app)) StopAttention(app);
        }
    }
}
