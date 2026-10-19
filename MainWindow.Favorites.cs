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

        // Добавить/убрать кнопки «Избранное» (звезда, папки) и «Избранные файлы» по настройкам (на лету).
        public void ApplyFavoritesButton()
        {
            ApplyFavButton(files: false, Settings.EnableFavorites, Settings.FavoritesIndex, Loc.T("fav_folders_title"));
            ApplyFavButton(files: true, Settings.EnableFavoriteFiles, Settings.FavFilesIndex, Loc.T("fav_files_title"));
        }

        private void ApplyFavButton(bool files, bool enabled, int index, string title)
        {
            var existing = Apps.FirstOrDefault(a => a.IsFavorites && a.IsFavFiles == files);
            if (enabled)
            {
                if (existing == null)
                {
                    int target = System.Math.Clamp(index, 0, Apps.Count);
                    Apps.Insert(target, new DockAppConfig { Name = title, IsFavorites = true, IsFavFiles = files });
                }
                else
                {
                    // Восстанавливаем сохранённую позицию.
                    int target = System.Math.Clamp(index, 0, Apps.Count - 1);
                    int cur = Apps.IndexOf(existing);
                    if (cur != target) Apps.Move(cur, target);
                }
            }
            else if (existing != null)
            {
                if (_favHost != null && _favHost.IsVisible && _favFiles == files) HideFavoritesPanel();
                Apps.Remove(existing);
            }
        }

        // Клик по звезде: флип-флоп открыть/закрыть.
        private void ToggleFavoritesPanel(Button btn)
        {
            // Если панель только что закрылась этим же кликом (Deactivated) — не открываем снова.
            if (Environment.TickCount - _favClosedTick < 250) return;
            bool wantFiles = btn.Tag is DockAppConfig d && d.IsFavFiles;
            if (_favHost != null && _favHost.IsVisible)
            {
                HideFavoritesPanel();
                if (wantFiles != _favFiles) ShowFavoritesPanel(btn); // открыта другая панель — переключаемся
            }
            else ShowFavoritesPanel(btn);
        }

        private void EnsureFavHideTimer()
        {
            if (_favHideTimer != null) return;
            _favHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FavHideDelayMs) };
            _favHideTimer.Tick += (s, e) => { _favHideTimer!.Stop(); if (_favDialogOpen) return; HideFavoritesPanel(); };
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
                if (_favPendingButton == null) return;
                bool wantFiles = _favPendingButton.Tag is DockAppConfig d && d.IsFavFiles;
                if (_favHost != null && _favHost.IsVisible)
                {
                    if (wantFiles == _favFiles) return;   // уже открыта нужная
                    HideFavoritesPanel();                 // открыта соседняя — переключаемся
                }
                ShowFavoritesPanel(_favPendingButton, activate: false);
            };
        }

        // activate=false — открытие по наведению. Наведение не даёт процессу права на передний план:
        // Windows активирует окно и тут же отбирает активацию обратно, Deactivated закрывал панель ещё
        // невидимой (Opacity 0) — выглядело как «не открывается с первого раза». По наведению окно
        // показываем без активации, закрытие — по уходу курсора; активируется оно при клике внутри.
        private int _favShownTick;

        private void ShowFavoritesPanel(Button btn, bool activate = true)
        {
            HidePreview(); // взаимоисключение: избранное и превью не показываются одновременно
            _favButton = btn;
            _favFiles = btn.Tag is DockAppConfig fd && fd.IsFavFiles;
            RestoreTooltip(ref _tipFav);
            _tipFav = SuppressTooltip(btn);
            FrameworkElement content = BuildFavContent();

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
                Child = _favScroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };
            rootBorder.MouseEnter += (s, e) => _favHideTimer?.Stop();
            rootBorder.MouseLeave += (s, e) => HideFavoritesSoon();
            // Перетаскивание файлов/папок прямо в открытую панель.
            rootBorder.AllowDrop = true;
            rootBorder.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                _favHideTimer?.Stop();
                e.Handled = true;
            };
            rootBorder.Drop += (s, e) =>
            {
                // Пересборка панели — после выхода из обработчика Drop (окно в нём закрывать нельзя).
                if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) Dispatcher.BeginInvoke(new Action(() => AddToFavoritesRouted(dropped)));
                e.Handled = true;
            };

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
                MinHeight = 120,
                ShowActivated = activate
            };
            _favShownTick = Environment.TickCount;
            _favHost.Deactivated += (s, e) => { if (Environment.TickCount - _favShownTick < 400) return; if (_favMenuOpen || _favDragging || _favResizing || _favDialogOpen) return; _favClosedTick = Environment.TickCount; HideFavoritesPanel(); };

            // Показываем невидимым, ставим на место и проявляем после первой отрисовки содержимого —
            // иначе первый кадр прозрачного окна пустой и в углу экрана (моргание).
            _favHost.Opacity = 0;
            _favHost.Show();
            PositionFavHost();
            var hostRef = _favHost;
            _favHost.Dispatcher.BeginInvoke(new Action(() => { if (hostRef == _favHost && hostRef != null) hostRef.Opacity = 1; }),
                DispatcherPriority.Render);

            // Появление: scale (с ~85%) + fade.
            var dur = new Duration(TimeSpan.FromMilliseconds(140));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var sa = new DoubleAnimation(0.85, 1, dur) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
            rootBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        }

        // Содержимое панели (список или сетка) — отдельно, чтобы обновлять на месте без переоткрытия окна.
        private FrameworkElement BuildFavContent()
        {
            var folders = FavList.ToList();

            // Контент: список или сетка (по настройке FavoritesLayout).
            FrameworkElement content;
            if (folders.Count == 0)
            {
                content = new TextBlock
                {
                    Text = Loc.T(_favFiles ? "fav_files_empty" : "fav_empty"),
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
            return content;
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
            _favGhost.Show(FavItemIcon(folder), Settings.IconSize);
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
            string name = FavItemName(folder);

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

            var rowIcon = FavItemIcon(folder);
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
                Child = FavLabel(new TextBlock
                {
                    Text = name,
                    Foreground = TextBrush(),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 250,
                    // Имена файлов из документации длинные — до трёх строк.
                    TextWrapping = _favFiles ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MaxHeight = _favFiles ? 58 : double.PositiveInfinity
                }, folder, center: false)
            });
            row.Child = rowStack;
            ApplyFavItemTipAndState(row, folder);

            row.MouseEnter += (s, e) => { if (!_favDragging) row.Background = HighlightBrush(); };
            row.MouseLeave += (s, e) => { if (!_favDragging) row.Background = Brushes.Transparent; };

            row.MouseLeftButtonDown += (s, e) =>
            {
                _favDragFolder = folder;
                _favDragRow = row;
                _favDragStartY = e.GetPosition(_favHost).Y;
                _favDragStartIndex = FavList.IndexOf(folder);
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
                            int _cur = FavList.IndexOf(order[_i]);
                            if (_cur >= 0 && _cur != _i) FavList.Move(_cur, _i);
                        }
                        PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                    }
                    _favDragging = false;
                }
                else
                {
                    OpenFavItem(folder);
                }
            };
            AttachFolderContextMenu(row, folder);
            return row;
        }

        private Border BuildFolderTile(string folder)
        {
            string name = FavItemName(folder);

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
            var icon = FavItemIcon(folder);
            if (icon != null)
                st.Children.Add(new Image { Source = icon, Width = Settings.IconSize * 0.9, Height = Settings.IconSize * 0.9, HorizontalAlignment = HorizontalAlignment.Center, Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.5, Color = Colors.Black } });
            st.Children.Add(new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = FavLabel(new TextBlock
                {
                    Text = name,
                    Foreground = TextBrush(),
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = _favFiles ? 50 : 36,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }, folder, center: true)
            });
            tile.Child = st;
            ApplyFavItemTipAndState(tile, folder);

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
                            int _cur = FavList.IndexOf(order[_i]);
                            if (_cur >= 0 && _cur != _i) FavList.Move(_cur, _i);
                        }
                        PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                    }
                    _favDragging = false;
                }
                else
                {
                    OpenFavItem(folder);
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
                if (_favFiles)
                {
                    // Файл: открыть, показать в папке, убрать.
                    var miOpen = new MenuItem { Header = Loc.T("f_open") };
                    miOpen.Click += (s2, e2) => OpenFavItem(folder);
                    var miShow = new MenuItem { Header = Loc.T("f_showInFolder") };
                    miShow.Click += (s2, e2) => { try { RecentDocs.ShowInFolder(folder); } catch { } HideFavoritesPanel(); };
                    menu.Items.Add(miOpen);
                    menu.Items.Add(miShow);
                    menu.Items.Add(new Separator());
                    var miCopyF = new MenuItem { Header = Loc.T("f_copy") };
                    miCopyF.Click += (s2, e2) => CopyPathsToClipboard(new[] { folder });
                    miCopyF.IsEnabled = System.IO.File.Exists(folder);
                    menu.Items.Add(miCopyF);
                    menu.Items.Add(new Separator());
                    var miRename = new MenuItem { Header = Loc.T("f_rename") };
                    // После закрытия меню: модальный диалог из обработчика клика оставил бы меню висеть.
                    miRename.Click += (s2, e2) => Dispatcher.BeginInvoke(new Action(() => RenameFavFile(folder)));
                    menu.Items.Add(miRename);
                    if (Settings.FavoriteFileNames.ContainsKey(folder))
                    {
                        var miRestore = new MenuItem { Header = Loc.T("f_restoreName") };
                        miRestore.Click += (s2, e2) =>
                        {
                            Settings.FavoriteFileNames.Remove(folder);
                            PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                            RefreshFavoritesPanel();
                        };
                        menu.Items.Add(miRestore);
                    }
                    menu.Items.Add(new Separator());
                }
                else
                {
                    // Папка: копировать её саму, вставить в неё файлы из буфера обмена.
                    bool exists = System.IO.Directory.Exists(folder);
                    var miCopyD = new MenuItem { Header = Loc.T("f_copy"), IsEnabled = exists };
                    miCopyD.Click += (s2, e2) => CopyPathsToClipboard(new[] { folder });
                    var (clip, move) = GetClipboardFiles();
                    var miPaste = new MenuItem
                    {
                        Header = Loc.T(move ? "f_moveInto" : "f_pasteInto") + (clip.Length > 1 ? $" ({clip.Length})" : ""),
                        IsEnabled = exists && clip.Length > 0
                    };
                    miPaste.Click += (s2, e2) => PasteIntoFolder(folder);
                    menu.Items.Add(miCopyD);
                    menu.Items.Add(miPaste);
                    menu.Items.Add(new Separator());
                }
                var list = FavList;
                var miRemove = new MenuItem { Header = Loc.T("fav_remove") };
                miRemove.Click += (s2, e2) =>
                {
                    list.Remove(folder);
                    Settings.FavoriteFileNames.Remove(folder);
                    PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                    RefreshFavoritesPanel();
                };
                menu.Items.Add(miRemove);
                menu.Opened += (s2, e2) => { _favMenuOpen = true; _favHideTimer?.Stop(); };
                menu.Closed += (s2, e2) => { _favMenuOpen = false; };
                menu.IsOpen = true;
            };
        }

        // ===== Буфер обмена: копировать файл/папку, вставить в папку =====
        // Формат как у Проводника: FileDrop + "Preferred DropEffect" (1 — копирование, 2 — вырезание).
        private static void CopyPathsToClipboard(string[] paths)
        {
            try
            {
                var sc = new System.Collections.Specialized.StringCollection();
                sc.AddRange(paths);
                var data = new DataObject();
                data.SetFileDropList(sc);
                data.SetData("Preferred DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(1)));
                Clipboard.SetDataObject(data, true);
            }
            catch { }
        }

        private static (string[] files, bool move) GetClipboardFiles()
        {
            try
            {
                if (!Clipboard.ContainsFileDropList()) return (Array.Empty<string>(), false);
                var files = Clipboard.GetFileDropList().Cast<string>().Where(p => !string.IsNullOrEmpty(p)).ToArray();
                bool move = false;
                if (Clipboard.GetData("Preferred DropEffect") is System.IO.MemoryStream ms && ms.Length >= 4)
                {
                    var b = new byte[4];
                    ms.Position = 0;
                    ms.Read(b, 0, 4);
                    move = (BitConverter.ToInt32(b, 0) & 2) != 0;
                }
                return (files, move);
            }
            catch { return (Array.Empty<string>(), false); }
        }

        // Копирование/перемещение через оболочку Windows: её прогресс, вопросы о замене, отмена.
        // Отдельный STA-поток — длинное копирование не подвешивает док.
        private void PasteIntoFolder(string folder)
        {
            var (files, move) = GetClipboardFiles();
            if (files.Length == 0 || !System.IO.Directory.Exists(folder)) return;
            HideFavoritesPanel();
            var t = new System.Threading.Thread(() =>
            {
                bool anyMoved = false;
                foreach (var src in files)
                {
                    try
                    {
                        bool isDir = System.IO.Directory.Exists(src);
                        if (!isDir && !System.IO.File.Exists(src)) continue;
                        string name = System.IO.Path.GetFileName(src.TrimEnd('\\'));
                        string dst = System.IO.Path.Combine(folder, name);
                        bool same = string.Equals(System.IO.Path.GetFullPath(src).TrimEnd('\\'),
                            System.IO.Path.GetFullPath(dst).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                        if (same)
                        {
                            if (move) continue;                 // вырезали и вставили туда же — нечего делать
                            dst = UniqueCopyPath(folder, name, isDir); // копия рядом: «имя (2).ext»
                        }
                        var ui = Microsoft.VisualBasic.FileIO.UIOption.AllDialogs;
                        var cancel = Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing;
                        if (isDir)
                        {
                            if (move) Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(src, dst, ui, cancel);
                            else Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(src, dst, ui, cancel);
                        }
                        else
                        {
                            if (move) Microsoft.VisualBasic.FileIO.FileSystem.MoveFile(src, dst, ui, cancel);
                            else Microsoft.VisualBasic.FileIO.FileSystem.CopyFile(src, dst, ui, cancel);
                        }
                        if (move) anyMoved = true;
                    }
                    catch { }
                }
                // Как Проводник: вырезанное после вставки из буфера убирается.
                if (anyMoved) Dispatcher.BeginInvoke(new Action(() => { try { Clipboard.Clear(); } catch { } }));
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        private static string UniqueCopyPath(string folder, string name, bool isDir)
        {
            string stem = isDir ? name : System.IO.Path.GetFileNameWithoutExtension(name);
            string ext = isDir ? "" : System.IO.Path.GetExtension(name);
            for (int i = 2; i < 1000; i++)
            {
                string p = System.IO.Path.Combine(folder, $"{stem} ({i}){ext}");
                if (!System.IO.File.Exists(p) && !System.IO.Directory.Exists(p)) return p;
            }
            return System.IO.Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){ext}");
        }

        // Пересобрать открытую панель (после удаления папки).
        private ScrollViewer? _favScroll;

        private void RefreshFavoritesPanel()
        {
            // Открыта — меняем только содержимое (окно, позиция и прокрутка остаются).
            if (_favHost != null && _favHost.IsVisible && _favScroll != null)
            {
                double off = _favScroll.VerticalOffset;
                _favScroll.Content = BuildFavContent();
                _favScroll.Dispatcher.BeginInvoke(new Action(() => _favScroll?.ScrollToVerticalOffset(off)), DispatcherPriority.Loaded);
                return;
            }
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

            // Выбор папок/файлов через системный диалог (можно выделить несколько).
            bool filesBtn = target.DataContext is DockAppConfig tb && tb.IsFavFiles;
            var miAdd = new MenuItem { Header = Loc.T(filesBtn ? "fav_addFiles" : "fav_addFolder") };
            miAdd.Click += (s, e) =>
            {
                if (filesBtn)
                {
                    var fdlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("fav_dlgTitle"), Multiselect = true };
                    if (fdlg.ShowDialog(this) == true) AddFilesToFavorites(fdlg.FileNames);
                }
                else
                {
                    var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("fav_dlgTitle"), Multiselect = true };
                    if (dlg.ShowDialog(this) == true) AddFoldersToFavorites(dlg.FolderNames);
                }
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

        // ===== Общие для папок и файлов =====
        private bool _favFiles; // открыта панель избранных файлов (а не папок)

        private System.Collections.ObjectModel.ObservableCollection<string> FavList =>
            _favFiles ? Settings.FavoriteFiles : Settings.FavoriteFolders;

        private string FavItemName(string path)
        {
            if (_favFiles && Settings.FavoriteFileNames.TryGetValue(path, out var alias) && !string.IsNullOrWhiteSpace(alias)) return alias;
            string name = string.Empty;
            try
            {
                name = _favFiles ? System.IO.Path.GetFileNameWithoutExtension(path) : new System.IO.DirectoryInfo(path).Name;
            }
            catch { }
            return string.IsNullOrEmpty(name) ? path : name;
        }

        // Иконка: у папки — её собственная, у файла — программы, которая его открывает (по ассоциации), с кэшем по расширению.
        private static readonly System.Collections.Generic.Dictionary<string, ImageSource?> _extIconCache = new(StringComparer.OrdinalIgnoreCase);
        private ImageSource? FavItemIcon(string path)
        {
            if (!_favFiles) return AppIconConverter.ExtractIconFromPath(path);
            string ext = System.IO.Path.GetExtension(path);
            if (_extIconCache.TryGetValue(ext, out var cached) && cached != null) return cached;
            ImageSource? img = null;
            try
            {
                string? exe = RecentDocs.GetAssocExe(ext);
                if (exe != null) img = AppIconConverter.ExtractIconFromPath(exe);
            }
            catch { }
            img ??= System.IO.File.Exists(path) ? AppIconConverter.ExtractIconFromPath(path) : null; // иконка типа файла
            if (img != null) _extIconCache[ext] = img;
            return img;
        }

        // Подсказка: полное имя и путь; отсутствующий файл — бледный.
        private void ApplyFavItemTipAndState(FrameworkElement el, string path)
        {
            // Сетевые пути не проверяем: недоступная шара подвесила бы открытие панели.
            bool exists = RecentDocs.IsNetworkPath(path) || (_favFiles ? System.IO.File.Exists(path) : System.IO.Directory.Exists(path));
            string tip = _favFiles ? System.IO.Path.GetFileName(path) + "\n" + System.IO.Path.GetDirectoryName(path) : path;
            if (!exists) { tip += "\n" + Loc.T("f_missing"); el.Opacity = 0.45; }
            el.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(el, 700);
        }

        // Подпись пункта: при своём имени — под ним мелко настоящее имя файла (чтобы найти оригинал).
        private FrameworkElement FavLabel(TextBlock main, string path, bool center)
        {
            if (!_favFiles || !Settings.FavoriteFileNames.ContainsKey(path)) return main;
            var sp = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(main);
            sp.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileName(path),
                Foreground = TextBrush(),
                Opacity = 0.55,
                FontSize = center ? 10 : 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
                MaxWidth = main.MaxWidth,
                Margin = new Thickness(0, 1, 0, 0)
            });
            return sp;
        }

        private bool _favDialogOpen;

        // «Переименовать для панели…»: своё имя хранится в настройках, файл не трогается.
        private void RenameFavFile(string path)
        {
            string current = Settings.FavoriteFileNames.TryGetValue(path, out var a) ? a : System.IO.Path.GetFileNameWithoutExtension(path);
            _favDialogOpen = true;
            _favHideTimer?.Stop();
            string? result;
            try { result = AskText(Loc.T("f_rename"), System.IO.Path.GetFileName(path), current); }
            finally { _favDialogOpen = false; }
            if (result == null) return;
            result = result.Trim();
            if (result.Length == 0 || result == System.IO.Path.GetFileNameWithoutExtension(path)) Settings.FavoriteFileNames.Remove(path);
            else Settings.FavoriteFileNames[path] = result;
            PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
            RefreshFavoritesPanel();
        }

        // Маленький диалог ввода в тон теме. null — отмена.
        private string? AskText(string title, string hint, string initial)
        {
            Brush Res(string k, Brush fb) => Application.Current.TryFindResource(k) as Brush ?? fb;
            var fg = Res("MenuTextBrush", Brushes.Black);
            var w = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = Res("MenuBgBrush", Brushes.White),
                Foreground = fg,
                Topmost = true,
                ShowInTaskbar = false
            };
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = hint, Foreground = fg, Opacity = 0.65, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 6) });
            var tb = new TextBox { Text = initial, FontSize = 14, Padding = new Thickness(4, 3, 4, 3) };
            sp.Children.Add(tb);
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = Loc.T("crash_close"), IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 4, 10, 4) };
            ok.Click += (s, e) => w.DialogResult = true;
            bp.Children.Add(ok); bp.Children.Add(cancel);
            sp.Children.Add(bp);
            w.Content = sp;
            w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            return w.ShowDialog() == true ? tb.Text : null;
        }

        private void OpenFavItem(string path)
        {
            if (_favFiles && !System.IO.File.Exists(path)) return;
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
            HideFavoritesPanel();
        }

        public int AddFilesToFavorites(IEnumerable<string> files)
        {
            int added = 0;
            foreach (var f in files)
            {
                if (string.IsNullOrWhiteSpace(f) || !System.IO.File.Exists(f)) continue;
                if (Settings.FavoriteFiles.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) continue;
                Settings.FavoriteFiles.Add(f);
                added++;
            }
            if (added > 0)
            {
                PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                if (_favHost != null && _favHost.IsVisible) RefreshFavoritesPanel();
            }
            return added;
        }

        // Брошенное на любую из кнопок или в панель: папки — в избранные папки, файлы — в избранные файлы.
        // Ярлыки (.lnk) программ сюда не относятся — их место на самой панели.
        public void AddToFavoritesRouted(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            AddFoldersToFavorites(list.Where(System.IO.Directory.Exists));
            if (Settings.EnableFavoriteFiles) AddFilesToFavorites(list.Where(System.IO.File.Exists));
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
        // Путь к файлу, открытому в окне. В заголовке обычно только имя («Отчёт.docx - Word»),
        // поэтому ищем: 1) полный путь прямо в заголовке (Notepad++, TIA…), 2) имя файла среди «Недавнего» программы.
        private string? FindOpenFilePath(DockAppConfig app, IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) hwnd = app.WindowHandle;
            if (hwnd == IntPtr.Zero && app.WindowHandles.Count > 0) hwnd = app.WindowHandles[0];
            var sb = new System.Text.StringBuilder(1024);
            Win32Helper.GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (title.Length == 0) return null;

            // 1) Полный путь в заголовке.
            var m = System.Text.RegularExpressions.Regex.Match(title, @"[A-Za-z]:\\[^<>|""*?\r\n]+?\.[A-Za-z0-9]{1,8}(?=$|[\s\]\)\-—–*•])");
            if (m.Success && System.IO.File.Exists(m.Value.Trim())) return m.Value.Trim();

            // 2) Имя файла из «Недавнего».
            List<RecentItem> recent;
            try { recent = RecentDocs.Get(app); } catch { return null; }
            string? best = null; int bestScore = 0;
            foreach (var r in recent)
            {
                if (r.IsCommand || r.IsUrl || !System.IO.File.Exists(r.Path)) continue;
                string fn = System.IO.Path.GetFileName(r.Path), stem = System.IO.Path.GetFileNameWithoutExtension(r.Path);
                int score = title.Contains(fn, StringComparison.OrdinalIgnoreCase) ? 2
                          : (stem.Length >= 3 && title.Contains(stem, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                if (r.IsOpenNow) score += 3; // TIA: открытый сейчас проект
                if (score > bestScore) { bestScore = score; best = r.Path; } // при равенстве — более свежий (раньше в списке)
            }
            return best;
        }

        private void OnAddOpenFileToFavClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.CommandParameter is string path) AddFilesToFavorites(new[] { path });
            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

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