using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PlankWin
{
    public partial class MainWindow
    {
        private Window? _favHost;
        private DispatcherTimer? _favHideTimer;
        private Button? _favButton;
        private int _favClosedTick = 0; // момент закрытия (защита флип-флопа от клика-Deactivated)
        private bool _favMenuOpen = false; // открыто ПКМ-меню папки (не закрывать панель)
        private DispatcherTimer? _favHoverTimer;
        private Button? _favPendingButton;
        private string? _favDragFolder;
        private Border? _favDragRow;
        private double _favDragStartX;
        private double _favDragStartY;
        private int _favDragStartIndex;
        private bool _favDragging = false;
        private bool _favReordering = false;
        private readonly DragGhost _favGhost = new DragGhost();
        private bool _favResizing = false;
        private bool _favIsGrid = false;
        private string _favResizeDir = "";
        private int _favResizeMouseX, _favResizeMouseY;
        private double _favResizeW, _favResizeH, _favResizeL, _favResizeT;
        private const int FavHideDelayMs = 500;

        // Добавить/убрать кнопку "Избранное" на доке по состоянию настройки (на лету).
        public void ApplyFavoritesButton()
        {
            var existing = Apps.FirstOrDefault(a => a.IsFavorites);
            if (Settings.EnableFavorites)
            {
                if (existing == null)
                {
                    int target = System.Math.Clamp(Settings.FavoritesIndex, 0, Apps.Count);
                    Apps.Insert(target, new DockAppConfig { Name = "\u0418\u0437\u0431\u0440\u0430\u043d\u043d\u043e\u0435", IsFavorites = true });
                }
                else
                {
                    // Восстанавливаем сохранённую позицию избранного.
                    int target = System.Math.Clamp(Settings.FavoritesIndex, 0, Apps.Count - 1);
                    int cur = Apps.IndexOf(existing);
                    if (cur != target) Apps.Move(cur, target);
                }
            }
            else
            {
                if (existing != null)
                {
                    if (_favHost != null && _favHost.IsVisible) HideFavoritesPanel();
                    Apps.Remove(existing);
                }
            }
        }

                // Клик по звезде: флип-флоп открыть/закрыть.
        private void ToggleFavoritesPanel(Button btn)
        {
            // Если панель только что закрылась этим же кликом (Deactivated) — не открываем снова.
            if (Environment.TickCount - _favClosedTick < 250) return;
            if (_favHost != null && _favHost.IsVisible) HideFavoritesPanel();
            else ShowFavoritesPanel(btn);
        }

        private void EnsureFavHideTimer()
        {
            if (_favHideTimer != null) return;
            _favHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FavHideDelayMs) };
            _favHideTimer.Tick += (s, e) => { _favHideTimer!.Stop(); HideFavoritesPanel(); };
        }

        private void HideFavoritesSoon()
        {
            EnsureFavHideTimer();
            _favHideTimer!.Stop();
            _favHideTimer.Start();
        }

        // ===== Подсказка кнопки не должна висеть поверх открытого окна (избранное/превью) =====
        // Подсказка появляется как обычно; когда открывается окно — закрываем её и выключаем до закрытия окна.
        private sealed class SuppressedTip { public FrameworkElement El = null!; }
        private SuppressedTip? _tipFav, _tipPreview;

        private static SuppressedTip? SuppressTooltip(FrameworkElement? fe)
        {
            if (fe == null) return null;
            if (fe.ToolTip is ToolTip tt) tt.IsOpen = false;
            ToolTipService.SetIsEnabled(fe, false);
            return new SuppressedTip { El = fe };
        }

        private static void RestoreTooltip(ref SuppressedTip? st)
        {
            if (st == null) return;
            ToolTipService.SetIsEnabled(st.El, true);
            st = null;
        }

        private void HideFavoritesPanel()
        {
            RestoreTooltip(ref _tipFav);
            _favHideTimer?.Stop();
            if (_favHost != null)
            {
                try { _favHost.Close(); } catch { }
                _favHost = null;
            }
        }

        private void EnsureFavHoverTimer()
        {
            if (_favHoverTimer != null) return;
            _favHoverTimer = new DispatcherTimer();
            _favHoverTimer.Tick += (s, e) =>
            {
                _favHoverTimer!.Stop();
                if (_favPendingButton != null && (_favHost == null || !_favHost.IsVisible))
                    ShowFavoritesPanel(_favPendingButton);
            };
        }

        private void ShowFavoritesPanel(Button btn)
        {
            HidePreview(); // взаимоисключение: избранное и превью не показываются одновременно
            _favButton = btn;
            RestoreTooltip(ref _tipFav);
            _tipFav = SuppressTooltip(btn);
            var folders = Settings.FavoriteFolders.ToList();

            // Контент: список или сетка (по настройке FavoritesLayout).
            FrameworkElement content;
            if (folders.Count == 0)
            {
                content = new TextBlock
                {
                    Text = Loc.T("fav_empty"),
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    Margin = new Thickness(10, 8, 10, 8),
                    FontSize = 12
                };
            }
            else if (Settings.FavoritesLayout == FavoritesLayoutMode.Grid)
            {
                var wrap = new EvenWrapPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var folder in folders)
                    wrap.Children.Add(BuildFolderTile(folder));
                content = wrap;
            }
            else
            {
                var stack = new AnimatedStackPanel { Orientation = Orientation.Vertical };
                foreach (var folder in folders)
                    stack.Children.Add(BuildFolderRow(folder));
                content = stack;
            }

            bool isGrid = Settings.FavoritesLayout == FavoritesLayoutMode.Grid;
            _favIsGrid = isGrid;
            var scale = new ScaleTransform(1, 1);
            // Обводка, чтобы окно не сливалось с фоном: при акценте — как у панели, иначе тонкая контрастная линия.
            Color favBg = Colors.Gray;
            try { favBg = (Color)ColorConverter.ConvertFromString(Settings.BackgroundColor); } catch { }
            bool favLight = (0.299 * favBg.R + 0.587 * favBg.G + 0.114 * favBg.B) > 150;
            bool panelHasBorder = MainBorder != null && MainBorder.BorderThickness.Left > 0 && MainBorder.BorderBrush != null;
            Brush favBorder = panelHasBorder
                ? MainBorder!.BorderBrush
                : new SolidColorBrush(favLight ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255));

            var rootBorder = new Border
            {
                Background = HexBrush(Settings.BackgroundColor, Color.FromRgb(30, 30, 30)),
                CornerRadius = new CornerRadius(Settings.PanelCornerRadius),
                BorderBrush = favBorder,
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 25, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black },
                Padding = new Thickness(6),
                Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };
            rootBorder.MouseEnter += (s, e) => _favHideTimer?.Stop();
            rootBorder.MouseLeave += (s, e) => HideFavoritesSoon();

            _favHost = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                Content = BuildHostWithGrips(rootBorder),
                Width = isGrid ? Settings.FavGridWidth : Settings.FavListWidth,
                Height = isGrid ? Settings.FavGridHeight : Settings.FavListHeight,
                MinWidth = 160,
                MinHeight = 120
            };
            _favHost.Deactivated += (s, e) => { if (_favMenuOpen || _favDragging || _favResizing) return; _favClosedTick = Environment.TickCount; HideFavoritesPanel(); };

            _favHost.Show();
            PositionFavHost();

            // Появление: scale (с ~85%) + fade.
            var dur = new Duration(TimeSpan.FromMilliseconds(140));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var sa = new DoubleAnimation(0.85, 1, dur) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
            rootBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        }

        private Brush TextBrush()
        {
            var b = new SmartContrastConverter().Convert(Settings.BackgroundColor, typeof(Brush), "Dot", System.Globalization.CultureInfo.InvariantCulture) as Brush;
            return b ?? Brushes.White;
        }

        // Отмена перетаскивания (потеря capture, Alt+Tab и т.п.): убрать ghost, вернуть вид.
        private void CancelFavDrag(FrameworkElement el)
        {
            if (!_favDragging) return;
            CloseFavGhost();
            el.Opacity = 1.0;
            if (el.Parent is EvenWrapPanel ewp) ewp.DraggedChild = null;
            _favDragging = false;
        }

                private void CreateFavGhost(string folder)
        {
            _favGhost.Show(AppIconConverter.ExtractIconFromPath(folder), Settings.IconSize);
            UpdateFavGhost();
        }

        private void UpdateFavGhost()
        {
            double dpi = 1.0;
            var src = PresentationSource.FromVisual(_favHost);
            if (src?.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;
            _favGhost.Update(dpi);
        }

        private void CloseFavGhost()
        {
            _favGhost.Close();
        }

                private Brush HighlightBrush()
        {
            var b = new SmartContrastConverter().Convert(Settings.BackgroundColor, typeof(Brush), "Highlight", System.Globalization.CultureInfo.InvariantCulture) as Brush;
            return b ?? new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        }

        private Brush HexBrush(string hex, Color fallback)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return new SolidColorBrush(fallback); }
        }

                private Border BuildFolderRow(string folder)
        {
            string name = string.Empty;
            try { name = new System.IO.DirectoryInfo(folder).Name; } catch { }
            if (string.IsNullOrEmpty(name)) name = folder;

            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                Tag = folder
            };
            var rowStack = new StackPanel { Orientation = Orientation.Horizontal };

            var rowIcon = AppIconConverter.ExtractIconFromPath(folder);
            if (rowIcon != null)
                rowStack.Children.Add(new Image
                {
                    Source = rowIcon,
                    Width = Settings.IconSize * 0.6,
                    Height = Settings.IconSize * 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1.5, Direction = 270, Opacity = 0.45, Color = Colors.Black }
                });

            rowStack.Children.Add(new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = TextBrush(),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 250
                }
            });
            row.Child = rowStack;

            row.MouseEnter += (s, e) => { if (!_favDragging) row.Background = HighlightBrush(); };
            row.MouseLeave += (s, e) => { if (!_favDragging) row.Background = Brushes.Transparent; };

            row.MouseLeftButtonDown += (s, e) =>
            {
                _favDragFolder = folder;
                _favDragRow = row;
                _favDragStartY = e.GetPosition(_favHost).Y;
                _favDragStartIndex = Settings.FavoriteFolders.IndexOf(folder);
                _favDragging = false;
                _favHideTimer?.Stop();
                row.CaptureMouse();
            };
            row.LostMouseCapture += (s, e) => { if (!_favReordering) CancelFavDrag(row); };
            row.MouseMove += (s, e) =>
            {
                if (!row.IsMouseCaptured) return;
                double dy = e.GetPosition(_favHost).Y - _favDragStartY;
                if (!_favDragging && Math.Abs(dy) > 5) _favDragging = true;
                if (!_favDragging) return;
                if (!_favGhost.IsVisible) { CreateFavGhost(folder); row.Opacity = 0.35; }
                UpdateFavGhost();
                // Живое перемещение: переставляем строку в Children по позиции курсора,
                // AnimatedStackPanel сам плавно раздвигает соседей.
                if (row.Parent is not Panel panel) return;
                double cursorY = e.GetPosition(panel).Y;
                int curIdx = panel.Children.IndexOf(row);
                int targetIdx = curIdx;
                double dead = row.ActualHeight * 0.3; // мёртвая зона от дребезга
                if (curIdx < panel.Children.Count - 1 && panel.Children[curIdx + 1] is FrameworkElement nextEl)
                {
                    Point nc = nextEl.TranslatePoint(new Point(0, nextEl.ActualHeight / 2), panel);
                    if (cursorY > nc.Y + dead) targetIdx = curIdx + 1;
                }
                if (targetIdx == curIdx && curIdx > 0 && panel.Children[curIdx - 1] is FrameworkElement prevEl)
                {
                    Point pc = prevEl.TranslatePoint(new Point(0, prevEl.ActualHeight / 2), panel);
                    if (cursorY < pc.Y - dead) targetIdx = curIdx - 1;
                }
                if (targetIdx != curIdx)
                {
                    _favReordering = true;
                    panel.Children.Remove(row);
                    panel.Children.Insert(targetIdx, row);
                    row.CaptureMouse();
                    _favReordering = false;
                }
            };
            row.MouseLeftButtonUp += (s, e) =>
            {
                bool wasDragging = _favDragging;
                if (row.IsMouseCaptured) row.ReleaseMouseCapture();
                CloseFavGhost();
                row.Opacity = 1.0;
                if (wasDragging)
                {
                    // Зафиксировать новый порядок из Children в FavoriteFolders.
                    if (row.Parent is Panel panel)
                    {
                        var order = panel.Children.OfType<FrameworkElement>()
                            .Select(c => c.Tag as string).Where(x => x != null).Cast<string>().ToList();
                        // Синхронизируем порядок через Move (без Clear) — меньше перерисовки.
                        for (int _i = 0; _i < order.Count; _i++)
                        {
                            int _cur = Settings.FavoriteFolders.IndexOf(order[_i]);
                            if (_cur >= 0 && _cur != _i) Settings.FavoriteFolders.Move(_cur, _i);
                        }
                        PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                    }
                    _favDragging = false;
                }
                else
                {
                    try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); } catch { }
                    HideFavoritesPanel();
                }
            };
            AttachFolderContextMenu(row, folder);
            return row;
        }

        private Border BuildFolderTile(string folder)
        {
            string name = string.Empty;
            try { name = new System.IO.DirectoryInfo(folder).Name; } catch { }
            if (string.IsNullOrEmpty(name)) name = folder;

            var tile = new Border
            {
                Width = Settings.IconSize * 2,
                Padding = new Thickness(6, 8, 6, 8),
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = folder
            };
            var st = new StackPanel { Orientation = Orientation.Vertical };
            var icon = AppIconConverter.ExtractIconFromPath(folder);
            if (icon != null)
                st.Children.Add(new Image { Source = icon, Width = Settings.IconSize * 0.9, Height = Settings.IconSize * 0.9, HorizontalAlignment = HorizontalAlignment.Center, Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.5, Color = Colors.Black } });
            st.Children.Add(new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = TextBrush(),
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 36,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            });
            tile.Child = st;

            tile.MouseEnter += (s, e) => { if (!_favDragging) tile.Background = HighlightBrush(); };
            tile.MouseLeave += (s, e) => { if (!_favDragging) tile.Background = Brushes.Transparent; };

            tile.MouseLeftButtonDown += (s, e) =>
            {
                _favDragFolder = folder;
                _favDragRow = tile;
                _favDragStartX = e.GetPosition(_favHost).X;
                _favDragStartY = e.GetPosition(_favHost).Y;
                _favDragging = false;
                _favHideTimer?.Stop();
                tile.CaptureMouse();
            };
            tile.LostMouseCapture += (s, e) => { if (!_favReordering) CancelFavDrag(tile); };
            tile.MouseMove += (s, e) =>
            {
                if (!tile.IsMouseCaptured) return;
                double dx = e.GetPosition(_favHost).X - _favDragStartX;
                double dy = e.GetPosition(_favHost).Y - _favDragStartY;
                if (!_favDragging && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5)) _favDragging = true;
                if (!_favDragging) return;
                if (!_favGhost.IsVisible) { CreateFavGhost(folder); tile.Opacity = 0.35; if (tile.Parent is EvenWrapPanel ewp) ewp.DraggedChild = tile; }
                UpdateFavGhost();
                // Целевой индекс — плитка под курсором (двумерно, WrapPanel).
                if (tile.Parent is not Panel panel) return;
                Point cur = e.GetPosition(panel);
                int targetIdx = -1;
                for (int i = 0; i < panel.Children.Count; i++)
                {
                    if (panel.Children[i] is not FrameworkElement fe) continue;
                    Rect slot = LayoutInformation.GetLayoutSlot(fe);
                    double insetX = slot.Width * 0.2, insetY = slot.Height * 0.2;
                    var inner = new Rect(slot.X + insetX, slot.Y + insetY, slot.Width - 2 * insetX, slot.Height - 2 * insetY);
                    if (inner.Contains(cur)) { targetIdx = i; break; }
                }
                if (targetIdx < 0) return;
                int curIdx = panel.Children.IndexOf(tile);
                if (targetIdx != curIdx)
                {
                    _favReordering = true;
                    panel.Children.Remove(tile);
                    if (targetIdx > panel.Children.Count) targetIdx = panel.Children.Count;
                    panel.Children.Insert(targetIdx, tile);
                    tile.CaptureMouse();
                    _favReordering = false;
                }
            };
            tile.MouseLeftButtonUp += (s, e) =>
            {
                bool wasDragging = _favDragging;
                if (tile.IsMouseCaptured) tile.ReleaseMouseCapture();
                CloseFavGhost();
                tile.Opacity = 1.0;
                if (tile.Parent is EvenWrapPanel ewpUp) ewpUp.DraggedChild = null;
                if (wasDragging)
                {
                    if (tile.Parent is Panel panel)
                    {
                        var order = panel.Children.OfType<FrameworkElement>()
                            .Select(c => c.Tag as string).Where(x => x != null).Cast<string>().ToList();
                        // Синхронизируем порядок через Move (без Clear) — меньше перерисовки.
                        for (int _i = 0; _i < order.Count; _i++)
                        {
                            int _cur = Settings.FavoriteFolders.IndexOf(order[_i]);
                            if (_cur >= 0 && _cur != _i) Settings.FavoriteFolders.Move(_cur, _i);
                        }
                        PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                    }
                    _favDragging = false;
                }
                else
                {
                    try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); } catch { }
                    HideFavoritesPanel();
                }
            };
            AttachFolderContextMenu(tile, folder);
            return tile;
        }

                // ПКМ по папке -> меню опций (пока: убрать из избранного).
        private void AttachFolderContextMenu(FrameworkElement element, string folder)
        {
            element.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                var menu = new ContextMenu();
                var miRemove = new MenuItem { Header = Loc.T("fav_remove") };
                miRemove.Click += (s2, e2) =>
                {
                    Settings.FavoriteFolders.Remove(folder);
                    PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                    RefreshFavoritesPanel();
                };
                menu.Items.Add(miRemove);
                menu.Opened += (s2, e2) => { _favMenuOpen = true; _favHideTimer?.Stop(); };
                menu.Closed += (s2, e2) => { _favMenuOpen = false; };
                menu.IsOpen = true;
            };
        }

        // Пересобрать открытую панель (после удаления папки).
        private void RefreshFavoritesPanel()
        {
            var btn = _favButton;
            HideFavoritesPanel();
            if (btn != null) ShowFavoritesPanel(btn);
        }

                // ПКМ по звезде -> выбор способа открытия (синхронизировано с настройками).
        private void ShowFavoritesModeMenu(FrameworkElement target)
        {
            var menu = new ContextMenu();
            var miClick = new MenuItem
            {
                Header = Loc.T("open_click"),
                IsCheckable = true,
                IsChecked = Settings.FavoritesOpenMode == FavoritesOpenMode.Click
            };
            miClick.Click += (s, e) => { Settings.FavoritesOpenMode = FavoritesOpenMode.Click; };
            var miHover = new MenuItem
            {
                Header = Loc.T("open_hover"),
                IsCheckable = true,
                IsChecked = Settings.FavoritesOpenMode == FavoritesOpenMode.Hover
            };
            miHover.Click += (s, e) => { Settings.FavoritesOpenMode = FavoritesOpenMode.Hover; };
            menu.Items.Add(miClick);
            menu.Items.Add(miHover);
            menu.Items.Add(new Separator());

            // Выбор папок через системный диалог (можно выделить несколько).
            var miAdd = new MenuItem { Header = Loc.T("fav_addFolder") };
            miAdd.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("fav_dlgTitle"), Multiselect = true };
                if (dlg.ShowDialog(this) == true) AddFoldersToFavorites(dlg.FolderNames);
            };
            menu.Items.Add(miAdd);

            // Как у меню иконок: панель не прячется, пока меню открыто; клик вне меню его закрывает.
            menu.Opened += ContextMenu_Opened;
            menu.Closed += ContextMenu_Closed;
            menu.Closed += (s, e) => _contextMenuOpen = false; // флаг ставит OnAppButtonContextMenuOpening; штатное меню кнопки звезды не открывается и его не сбросит

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        // Добавить папки в избранное (существующие и ещё не добавленные). Возвращает число добавленных.
        public int AddFoldersToFavorites(IEnumerable<string> folders)
        {
            int added = 0;
            foreach (var f in folders)
            {
                if (string.IsNullOrWhiteSpace(f) || !System.IO.Directory.Exists(f)) continue;
                if (Settings.FavoriteFolders.Any(x => string.Equals(x.TrimEnd('\\'), f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) continue;
                Settings.FavoriteFolders.Add(f);
                added++;
            }
            if (added > 0)
            {
                PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                if (_favHost != null && _favHost.IsVisible) RefreshFavoritesPanel();
            }
            return added;
        }

        private static bool IsExplorerApp(DockAppConfig? app) =>
            app != null && string.Equals(System.IO.Path.GetFileName(app.Path), "explorer.exe", StringComparison.OrdinalIgnoreCase);

        // Текущая папка окна Проводника (через Shell.Application). При вкладках Win11 у всех вкладок
        // один HWND — берём ту, чьё имя совпадает с заголовком окна (это активная вкладка).
        private static string? GetExplorerFolderPath(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(512);
                Win32Helper.GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString();

                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return null;
                dynamic shell = Activator.CreateInstance(t)!;
                string? fallback = null;
                foreach (dynamic w in shell.Windows())
                {
                    try
                    {
                        if (new IntPtr(Convert.ToInt64(w.HWND)) != hwnd) continue;
                        string path = w.Document.Folder.Self.Path;
                        if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path)) continue; // «Этот компьютер» и т.п.
                        fallback ??= path;
                        string name = w.LocationName;
                        if (!string.IsNullOrEmpty(name) && title.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return path;
                    }
                    catch { }
                }
                return fallback;
            }
            catch { return null; }
        }

        // ПКМ по кнопке Проводника -> «Добавить папку в избранное» (папка активного окна).
        private void OnAddExplorerFolderToFavClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.DataContext is DockAppConfig app)
            {
                string? path = GetExplorerFolderPath(app.WindowHandle);
                if (path != null) AddFoldersToFavorites(new[] { path });
            }
            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

                // Обернуть панель в Grid с зонами-хватами по краям и углам (свой ресайз).
        private Grid BuildHostWithGrips(UIElement inner)
        {
            var g = new Grid();
            g.Children.Add(inner);
            const double edge = 7, corner = 12;
            AddGrip(g, "L", HorizontalAlignment.Left, VerticalAlignment.Stretch, edge, double.NaN, Cursors.SizeWE);
            AddGrip(g, "R", HorizontalAlignment.Right, VerticalAlignment.Stretch, edge, double.NaN, Cursors.SizeWE);
            AddGrip(g, "T", HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, edge, Cursors.SizeNS);
            AddGrip(g, "B", HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, edge, Cursors.SizeNS);
            AddGrip(g, "TL", HorizontalAlignment.Left, VerticalAlignment.Top, corner, corner, Cursors.SizeNWSE);
            AddGrip(g, "BR", HorizontalAlignment.Right, VerticalAlignment.Bottom, corner, corner, Cursors.SizeNWSE);
            AddGrip(g, "TR", HorizontalAlignment.Right, VerticalAlignment.Top, corner, corner, Cursors.SizeNESW);
            AddGrip(g, "BL", HorizontalAlignment.Left, VerticalAlignment.Bottom, corner, corner, Cursors.SizeNESW);
            return g;
        }

        private void AddGrip(Grid parent, string dir, HorizontalAlignment ha, VerticalAlignment va, double w, double h, Cursor cur)
        {
            var grip = new Border
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = ha,
                VerticalAlignment = va,
                Cursor = cur
            };
            if (!double.IsNaN(w)) grip.Width = w;
            if (!double.IsNaN(h)) grip.Height = h;

            // Курсор на хвате -> он в пределах окна: не закрываем панель.
            grip.MouseEnter += (s, e) => _favHideTimer?.Stop();
            grip.MouseLeave += (s, e) => { if (!_favResizing) HideFavoritesSoon(); };

            grip.MouseLeftButtonDown += (s, e) =>
            {
                if (_favHost == null) return;
                _favResizeDir = dir;
                Win32Helper.GetCursorPos(out var pt);
                _favResizeMouseX = pt.X; _favResizeMouseY = pt.Y;
                _favResizeW = _favHost.Width; _favResizeH = _favHost.Height;
                _favResizeL = _favHost.Left; _favResizeT = _favHost.Top;
                _favResizing = true;
                _favHideTimer?.Stop();
                grip.CaptureMouse();
                e.Handled = true;
            };
            grip.MouseMove += (s, e) =>
            {
                if (!grip.IsMouseCaptured || !_favResizing || _favHost == null) return;
                Win32Helper.GetCursorPos(out var pt);
                double dpi = 1.0;
                var src = PresentationSource.FromVisual(_favHost);
                if (src?.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;
                double dx = (pt.X - _favResizeMouseX) / dpi;
                double dy = (pt.Y - _favResizeMouseY) / dpi;
                const double minW = 160, minH = 120;
                if (dir.Contains("L")) { double nw = Math.Max(minW, _favResizeW - dx); _favHost.Left = _favResizeL + (_favResizeW - nw); _favHost.Width = nw; }
                if (dir.Contains("R")) { _favHost.Width = Math.Max(minW, _favResizeW + dx); }
                if (dir.Contains("T")) { double nh = Math.Max(minH, _favResizeH - dy); _favHost.Top = _favResizeT + (_favResizeH - nh); _favHost.Height = nh; }
                if (dir.Contains("B")) { _favHost.Height = Math.Max(minH, _favResizeH + dy); }
            };
            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
                if (_favResizing && _favHost != null)
                {
                    _favResizing = false;
                    if (_favIsGrid) { Settings.FavGridWidth = _favHost.Width; Settings.FavGridHeight = _favHost.Height; }
                    else { Settings.FavListWidth = _favHost.Width; Settings.FavListHeight = _favHost.Height; }
                    PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                }
            };
            parent.Children.Add(grip);
        }

                private void PositionFavHost()
        {
            if (_favButton == null || _favHost == null) return;
            try
            {
                var p = _favButton.PointToScreen(new Point(0, 0)); // пиксели
                double dpi = 1.0;
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;

                double btnW = _favButton.ActualWidth * dpi;
                double btnH = _favButton.ActualHeight * dpi;
                double wpx = _favHost.ActualWidth * dpi, hpx = _favHost.ActualHeight * dpi;
                double left = p.X, top = p.Y;

                switch (Settings.Position)
                {
                    case DockPosition.Left: left = p.X + btnW + 6; top = p.Y + btnH / 2 - hpx / 2; break;
                    case DockPosition.Right: left = p.X - wpx - 6; top = p.Y + btnH / 2 - hpx / 2; break;
                    case DockPosition.Top: top = p.Y + btnH + 6; left = p.X + btnW / 2 - wpx / 2; break;
                    case DockPosition.Bottom: top = p.Y - hpx - 6; left = p.X + btnW / 2 - wpx / 2; break;
                }

                ClampToMonitor(ref left, ref top, wpx, hpx, 0);
                _favHost.Left = left / dpi;
                _favHost.Top = top / dpi;
            }
            catch { }
        }

        // Удерживает всплывающее окно (физ. пиксели) в рабочей области монитора панели с отступом 8 px.
        // inset — прозрачное поле окна (под тень), которое может выходить за край.
        private void ClampToMonitor(ref double left, ref double top, double wpx, double hpx, double insetPx)
        {
            var scr = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName)
                      ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (scr == null) return;
            var wa = scr.WorkingArea;
            const double m = 8;
            double minL = wa.Left + m - insetPx, maxL = wa.Right - m + insetPx - wpx;
            double minT = wa.Top + m - insetPx, maxT = wa.Bottom - m + insetPx - hpx;
            if (maxL >= minL) left = Math.Clamp(left, minL, maxL);
            if (maxT >= minT) top = Math.Clamp(top, minT, maxT);
        }
    }
}
