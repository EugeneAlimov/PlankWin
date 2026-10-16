using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlankWin
{
    // Глобальные горячие клавиши: <модификаторы>+1…9 — как клик по N-й иконке (запуск / активация /
    // сворачивание / перебор окон), <модификаторы>+0 — избранное. Регистрируются на процесс одним окном
    // (как сырой ввод) и срабатывают для панели на мониторе под курсором.
    public partial class MainWindow
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyBaseId = 0xB000;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        private static MainWindow? _hotkeyOwner;
        // Цифры, которые не удалось зарегистрировать (заняты другой программой или самой Windows).
        public static List<int> HotkeyFailures { get; } = new();

        // Варианты модификаторов: ключ хранится в global.json.
        public static readonly (string key, string label, uint mods)[] HotkeyModes =
        {
            ("off", "—", 0),
            ("winalt", "Win + Alt", MOD_WIN | MOD_ALT),
            ("ctrlalt", "Ctrl + Alt", MOD_CONTROL | MOD_ALT),
            ("winshift", "Win + Shift", MOD_WIN | MOD_SHIFT),
            ("altshift", "Alt + Shift", MOD_ALT | MOD_SHIFT),
        };

        // (Пере)регистрация по текущей настройке. Вызывается при старте и при смене настройки.
        public static void ApplyHotkeys()
        {
            UnregisterAllHotkeys();
            var owner = _hotkeyOwner ?? _trayDocks.FirstOrDefault(d => d._mainHwnd != IntPtr.Zero);
            if (owner == null) return;
            _hotkeyOwner = owner;

            var mode = HotkeyModes.FirstOrDefault(m => m.key == GlobalConfig.Hotkeys);
            if (mode.mods == 0) return;
            for (int d = 0; d <= 9; d++)
                if (!RegisterHotKey(owner._mainHwnd, HotkeyBaseId + d, mode.mods | MOD_NOREPEAT, (uint)('0' + d)))
                    HotkeyFailures.Add(d);
        }

        private static void UnregisterAllHotkeys()
        {
            HotkeyFailures.Clear();
            if (_hotkeyOwner == null || _hotkeyOwner._mainHwnd == IntPtr.Zero) return;
            for (int d = 0; d <= 9; d++) UnregisterHotKey(_hotkeyOwner._mainHwnd, HotkeyBaseId + d);
        }

        // Закрывается окно-владелец — передаём регистрацию другой панели.
        private void ReleaseHotkeysOnClose()
        {
            if (_hotkeyOwner != this) return;
            UnregisterAllHotkeys();
            _hotkeyOwner = null;
            if (_trayDocks.Any(d => d != this)) ApplyHotkeys();
        }

        // Обработка WM_HOTKEY (из WndProc).
        private bool HandleHotkeyMessage(int msg, IntPtr wParam)
        {
            if (msg != WM_HOTKEY) return false;
            int digit = wParam.ToInt32() - HotkeyBaseId;
            if (digit < 0 || digit > 9) return false;

            // Панель на мониторе под курсором (на одном мониторе — единственная).
            MainWindow target = this;
            try
            {
                Win32Helper.GetCursorPos(out var pt);
                var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
                target = _trayDocks.FirstOrDefault(d => d._profileName == scr.DeviceName) ?? this;
            }
            catch { }
            target.ActivateByHotkey(digit);
            return true;
        }

        private void ActivateByHotkey(int digit)
        {
            if (digit == 0)
            {
                var fav = Apps.FirstOrDefault(a => a.IsFavorites && !a.IsFavFiles);
                if (fav == null) return;
                ShowDock("hotkey");
                // Панель избранного позиционируется от кнопки — ждём, пока док выедет.
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                t.Tick += (s, e) => { t.Stop(); if (FindAppButton(fav) is Button fb) ToggleFavoritesPanel(fb); };
                t.Start();
                return;
            }

            // N-я «настоящая» иконка в видимом порядке (без избранного и заглушек).
            var list = Apps.Where(a => !a.IsFavorites && !a.IsPlaceholder && !a.IsDeleting).ToList();
            if (digit > list.Count) return;
            var app = list[digit - 1];
            if (FindAppButton(app) is Button btn) OnAppButtonClick(btn, new RoutedEventArgs());
        }

        private Button? FindAppButton(DockAppConfig app)
        {
            if (AppsItemsControl.ItemContainerGenerator.ContainerFromItem(app) is not DependencyObject container) return null;
            return FindDescendant<Button>(container, b => b.Tag == app);
        }

        private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t && match(t)) return t;
                var deeper = FindDescendant(c, match);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
