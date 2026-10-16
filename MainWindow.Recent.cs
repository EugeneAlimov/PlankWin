using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PlankWin
{
    // Подменю «Недавнее» у кнопки приложения: закреплённые сверху, затем недавние из провайдера.
    public partial class MainWindow
    {
        private const string GlyphPin = "", GlyphPinned = "", GlyphFolder = "";

        // Вызывается из ContextMenu_Opened: показывает/прячет пункт и наполняет подменю.
        private void UpdateRecentMenu(ContextMenu menu, DockAppConfig app)
        {
            var host = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "Recent"));
            if (host == null) return;

            // Задачи прошлого открытия убираем — меню общее для шаблона кнопки.
            foreach (var old in menu.Items.OfType<MenuItem>().Where(i => Equals(i.Tag, "RecentTask")).ToList())
                menu.Items.Remove(old);

            var all = RecentDocs.FindProvider(app) != null ? RecentDocs.Get(app) : new List<RecentItem>();

            // Задачи программы («Новая вкладка», «Приватное окно»…) — действия, а не история:
            // ставим прямо в меню, над «Недавним».
            int at = menu.Items.IndexOf(host);
            foreach (var t in all.Where(r => r.IsTask))
            {
                var ti = t;
                var mi = new MenuItem { Header = ti.Label, Tag = "RecentTask", Icon = string.Empty, ToolTip = ti.Tooltip };
                mi.Click += (s, e) =>
                {
                    CloseActiveIconMenu();
                    try { RecentDocs.Launch(app, ti); if (Settings.AutoHide) HideDock(); }
                    catch (Exception ex) { MessageBox.Show($"{Loc.T("err_launch")}\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error); }
                };
                menu.Items.Insert(at++, mi);
            }

            bool any = BuildRecentItems(host, app, all);
            host.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool BuildRecentItems(MenuItem host, DockAppConfig app, List<RecentItem>? all = null)
        {
            host.Items.Clear();
            if (RecentDocs.FindProvider(app) == null) return false;

            var recent = (all ?? RecentDocs.Get(app)).Where(r => !r.IsTask).ToList();
            var pins = app.RecentPins ?? new List<string>();

            var pinnedItems = pins.Select(p =>
                recent.FirstOrDefault(r => string.Equals(r.Key, p, StringComparison.OrdinalIgnoreCase))
                ?? RecentItem.FromKey(p)).ToList();
            var rest = recent.Where(r => !pins.Contains(r.Key, StringComparer.OrdinalIgnoreCase)).ToList();

            foreach (var it in pinnedItems) host.Items.Add(MakeRecentMenuItem(host, app, it, pinned: true));
            string? group = null;
            bool first = true;
            foreach (var it in rest)
            {
                bool newGroup = it.Group != null && it.Group != group;
                if ((pinnedItems.Count > 0 || !first) && (first || newGroup)) host.Items.Add(new Separator());
                if (newGroup) host.Items.Add(GroupHeader(it.Group!));
                group = it.Group;
                first = false;
                host.Items.Add(MakeRecentMenuItem(host, app, it, pinned: false));
            }

            return host.Items.Count > 0;
        }

        // Заголовок секции: неактивный пункт мелким приглушённым шрифтом.
        private static MenuItem GroupHeader(string text) => new MenuItem
        {
            Header = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold },
            IsEnabled = false,
            Focusable = false
        };

        private MenuItem MakeRecentMenuItem(MenuItem host, DockAppConfig app, RecentItem it, bool pinned)
        {
            bool exists = it.Exists;
            var textBrush = (Application.Current.TryFindResource("MenuTextBrush") as SolidColorBrush)?.Color ?? Colors.Black;

            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 340,
                Opacity = exists ? 1.0 : 0.45
            };
            text.Inlines.Add(new System.Windows.Documents.Run(it.Label) { FontWeight = it.IsOpenNow ? FontWeights.SemiBold : FontWeights.Normal });
            if (!string.IsNullOrEmpty(it.Note))
                text.Inlines.Add(new System.Windows.Documents.Run("  · " + it.Note)
                    { Foreground = new SolidColorBrush(Color.FromArgb(0x90, textBrush.R, textBrush.G, textBrush.B)), FontSize = 11.5 });

            var folderBtn = MiniGlyphButton(GlyphFolder, Loc.T("rc_folder"), () =>
            {
                CloseActiveIconMenu();
                try { RecentDocs.ShowInFolder(it.Path); } catch { }
            });
            var pinBtn = MiniGlyphButton(pinned ? GlyphPinned : GlyphPin, Loc.T(pinned ? "rc_unpin" : "rc_pin"), () =>
            {
                TogglePin(app, it.Key);
                BuildRecentItems(host, app); // подменю остаётся открытым, просто перестраиваем
            });

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, -8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (!it.IsCommand && !it.IsUrl) tools.Children.Add(folderBtn);
            tools.Children.Add(pinBtn);
            // Кнопки видны только под курсором; у закреплённых булавка видна всегда.
            folderBtn.Opacity = 0;
            pinBtn.Opacity = pinned ? 0.6 : 0;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            Grid.SetColumn(tools, 1);
            grid.Children.Add(tools);

            var mi = new MenuItem
            {
                Header = grid,
                Icon = it.IsOpenNow ? "" : string.Empty, // галочка у открытого сейчас
                ToolTip = !exists ? it.Path + "\n" + Loc.T("rc_missing")
                        : it.Tooltip ?? (it.IsCommand ? null : it.Path)
            };
            mi.MouseEnter += (s, e) => { folderBtn.Opacity = 0.75; pinBtn.Opacity = 0.75; };
            mi.MouseLeave += (s, e) => { folderBtn.Opacity = 0; pinBtn.Opacity = pinned ? 0.6 : 0; };
            mi.Click += (s, e) =>
            {
                if (!exists) return;
                CloseActiveIconMenu();
                try
                {
                    if (it.IsOpenNow && it.Window != IntPtr.Zero)
                    {
                        if (Win32Helper.IsIconic(it.Window)) Win32Helper.ShowWindow(it.Window, 9); // SW_RESTORE
                        Win32Helper.SetForegroundWindow(it.Window);
                    }
                    else RecentDocs.Launch(app, it);
                    if (Settings.AutoHide) HideDock();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"{Loc.T("err_launch")}\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            return mi;
        }

        // Маленькая кнопка-глиф внутри пункта меню. Клик по ней не должен срабатывать как клик по пункту.
        private static Border MiniGlyphButton(string glyph, string tip, Action action)
        {
            var b = new Border
            {
                Width = 24, Height = 22,
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            b.MouseEnter += (s, e) => { b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)); b.Opacity = 1; };
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; action(); };
            return b;
        }

        private void TogglePin(DockAppConfig app, string path)
        {
            var pins = app.RecentPins ??= new List<string>();
            int i = pins.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) pins.RemoveAt(i); else pins.Add(path);
            if (pins.Count == 0) app.RecentPins = null;
            SaveAppsConfig();
        }

        private void CloseActiveIconMenu()
        {
            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        // Курсор над открытым подменю (у него своё всплывающее окно вне прямоугольника главного меню).
        private static bool IsPointInOpenSubmenu(ItemsControl menu, int x, int y)
        {
            foreach (var mi in menu.Items.OfType<MenuItem>())
            {
                if (!mi.IsSubmenuOpen) continue;
                if (mi.Template?.FindName("PART_Popup", mi) is System.Windows.Controls.Primitives.Popup pp && pp.Child != null &&
                    PresentationSource.FromVisual(pp.Child) is System.Windows.Interop.HwndSource src)
                {
                    Win32Helper.GetWindowRect(src.Handle, out var r);
                    if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom) return true;
                }
                if (IsPointInOpenSubmenu(mi, x, y)) return true;
            }
            return false;
        }
    }
}
