using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PlankWin
{
    public partial class MainWindow
    {
        private Window? _previewHost;
        private IntPtr _previewHostHwnd = IntPtr.Zero;
        private readonly List<IntPtr> _previewThumbs = new();
        private DispatcherTimer? _previewHoverTimer;
        private DispatcherTimer? _previewHideTimer;
        private DockAppConfig? _previewApp;
        private Button? _previewButton;
        private IntPtr _peekPrevForeground = IntPtr.Zero;   // окно, активное до Aero Peek (для возврата)
        private IntPtr _peekRestoredIconic = IntPtr.Zero; // свёрнутое окно, временно развёрнутое для peek
        private Window? _peekOverlay;            // матовый слой (снимок экрана + блюр + затемнение)
        private IntPtr _peekOverlayHwnd = IntPtr.Zero;
        private BitmapSource? _peekSnapshot;     // снимок экрана (готовится при открытии превью)
        private DispatcherTimer? _peekDebounceTimer;
        private IntPtr _peekPendingHwnd = IntPtr.Zero;
        private int _peekMenuClosedTick = 0; // момент закрытия ПКМ-меню (для игнора клика-закрытия)
        private const int PeekDebounceMs = 100;  // задержка перед подъёмом окна
        private readonly Dictionary<IntPtr, BitmapSource> _snapshotCache = new();

        private const int PreviewHoverDelayMs = 450;
        private const int PreviewHideDelayMs = 250;
        private const double ThumbW = 240;   // DIP
        private const double ThumbH = 140;
        private const double Pad = 8;
        private const double Gap = 8;
        private const double TitleH = 24;    // строка заголовка под миниатюрой
        private const double CardPad = 6;    // поле карточки вокруг миниатюры
        private const double ShadowM = 14;   // прозрачное поле окна под тень (как у панели)

        // Системный радиус из ресурсов App (Win11: 8/6/4, Win10: 0).
        private static CornerRadius SysR(string key) =>
            Application.Current.Resources[key] is CornerRadius cr ? cr : new CornerRadius(0);

        private void EnsurePreviewInfra()
        {
            if (_previewHoverTimer != null) return;
            _previewHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewHoverDelayMs) };
            _previewHoverTimer.Tick += (s, e) => { _previewHoverTimer!.Stop(); ShowPreview(); };
            _previewHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewHideDelayMs) };
            _previewHideTimer.Tick += (s, e) => { _previewHideTimer!.Stop(); HidePreview(); };
        }

        private void OnAppButtonMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DockAppConfig app) return;

            // Кнопка "Избранное" в режиме наведения — открываем панель избранного по hover.
            if (app.IsFavorites)
            {
                // Вернулись на кнопку уже открытой панели (например, из самой панели) — отменяем отложенное
                // закрытие, иначе панель закрывается под курсором и по наведению уже не открывается.
                if (_favHost != null && _favHost.IsVisible && _favFiles == app.IsFavFiles) _favHideTimer?.Stop();
                // Открываем, если панели нет или открыта панель другой кнопки (звезда <-> файлы).
                if (Settings.FavoritesOpenMode == FavoritesOpenMode.Hover && (_favHost == null || !_favHost.IsVisible || _favFiles != app.IsFavFiles))
                {
                    _favPendingButton = btn;
                    EnsureFavHoverTimer();
                    _favHoverTimer!.Stop();
                    _favHoverTimer.Interval = TimeSpan.FromMilliseconds(Settings.PreviewDelayMs);
                    _favHoverTimer.Start();
                }
                return;
            }

            if (!Settings.EnablePreviews) return;
            if (!app.IsRunning || app.WindowHandles.Count < 1) return;
            EnsurePreviewInfra();
            _previewApp = app;
            _previewButton = btn;
            _previewHideTimer!.Stop();
            _previewHoverTimer!.Stop();
            _previewHoverTimer.Interval = TimeSpan.FromMilliseconds(Settings.PreviewDelayMs);
            _previewHoverTimer.Start();
        }

        private void OnAppButtonMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DockAppConfig app && app.IsFavorites)
            {
                _favHoverTimer?.Stop(); // отменить открытие, если курсор ушёл
                if (Settings.FavoritesOpenMode == FavoritesOpenMode.Hover) HideFavoritesSoon();
                return;
            }
            _previewHoverTimer?.Stop();
            HidePreviewSoon();
        }

        private int _previewMenusOpen = 0; // открытые ПКМ-меню карточек превью

        // Перестановка карточек превью.
        private int _pvDragIndex = -1;
        private bool _pvDragging;
        private Point _pvDragStart;
        private bool _previewNoAnim; // перестройка превью без анимации появления
        private bool _pvFinishing;   // идёт завершение перестановки (не путать с потерей захвата)
        private int _pvDropIndex;    // куда встанет перетаскиваемая карточка

        // Курсор над окном превью (по экранному прямоугольнику — IsMouseOver ненадёжен, пока меню держит захват мыши).
        private bool IsCursorOverPreviewHost()
        {
            if (_previewHost == null || !_previewHost.IsVisible || _previewHostHwnd == IntPtr.Zero) return false;
            Win32Helper.GetCursorPos(out var pt);
            return Win32Helper.GetWindowRect(_previewHostHwnd, out var r)
                   && pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
        }

        private void HidePreviewSoon()
        {
            if (_previewMenusOpen > 0) return; // пока открыто меню карточки — превью не прячем
            EnsurePreviewInfra();
            _previewHideTimer!.Stop();
            _previewHideTimer.Start();
        }

        private void ClearThumbs()
        {
            foreach (var t in _previewThumbs)
            {
                try { Win32Helper.DwmUnregisterThumbnail(t); } catch { }
            }
            _previewThumbs.Clear();
        }

        private void HidePreview()
        {
            RestoreTooltip(ref _tipPreview);
            _previewHideTimer?.Stop();
            HidePeekOverlay();
            _peekSnapshot = null;
            if (_peekRestoredIconic != IntPtr.Zero && Win32Helper.IsWindow(_peekRestoredIconic))
            {
                Win32Helper.ShowWindow(_peekRestoredIconic, Win32Helper.SW_MINIMIZE);
                _peekRestoredIconic = IntPtr.Zero;
            }
            if (_peekPrevForeground != IntPtr.Zero)
            {
                RaiseWindowSoft(_peekPrevForeground);
                _peekPrevForeground = IntPtr.Zero;
            }
            AnimatePreviewClose();
        }

        // ===== Плавное закрытие превью =====
        // DWM-миниатюры нельзя масштабировать WPF-трансформом, но можно гасить их прозрачность.
        // Поэтому: миниатюры быстро гаснут (~110 мс), подложка одновременно чуть сжимается и
        // растворяется (~180 мс, EaseIn — медленный старт, пока миниатюры ещё видны), затем Hide.
        private DispatcherTimer? _previewCloseTimer;
        private bool _previewClosing;

        private void AnimatePreviewClose()
        {
            if (_previewHost == null || !_previewHost.IsVisible || _previewHost.Content is not FrameworkElement root)
            {
                ClearThumbs();
                _previewHost?.Hide();
                return;
            }
            if (_previewClosing) return;
            _previewClosing = true;

            const double totalMs = 180, thumbMs = 110;
            var dur = new Duration(TimeSpan.FromMilliseconds(totalMs));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, dur) { EasingFunction = ease });
            if (root.RenderTransform is ScaleTransform sc)
            {
                var sa = new DoubleAnimation(1, 0.92, dur) { EasingFunction = ease };
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
            }

            var start = DateTime.Now;
            _previewCloseTimer?.Stop();
            _previewCloseTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _previewCloseTimer.Tick += (s, e) =>
            {
                double t = (DateTime.Now - start).TotalMilliseconds;
                byte op = (byte)(255 * Math.Max(0, 1 - t / thumbMs));
                foreach (var thumb in _previewThumbs)
                {
                    var pr = new Win32Helper.DWM_THUMBNAIL_PROPERTIES { dwFlags = Win32Helper.DWM_TNP_OPACITY, opacity = op, fVisible = true };
                    try { Win32Helper.DwmUpdateThumbnailProperties(thumb, ref pr); } catch { }
                }
                if (t >= totalMs) FinishPreviewClose();
            };
            _previewCloseTimer.Start();
        }

        private void FinishPreviewClose()
        {
            _previewCloseTimer?.Stop();
            _previewCloseTimer = null;
            _previewClosing = false;
            _previewMenusOpen = 0; // страховка от «залипшего» счётчика
            ClearThumbs();
            _previewHost?.Hide();
            if (_previewHost?.Content is FrameworkElement root)
            {
                root.BeginAnimation(UIElement.OpacityProperty, null);
                root.Opacity = 1;
            }
        }

        // Новое превью во время анимации закрытия: обрываем её (иначе таймер спрячет новое окно).
        private void CancelPreviewClose()
        {
            if (!_previewClosing) return;
            _previewCloseTimer?.Stop();
            _previewCloseTimer = null;
            _previewClosing = false;
            ClearThumbs();
        }

        private bool _contextMenuOpen = false;

        private void CreatePreviewHost()
        {
            _previewHost = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                AllowsTransparency = true,
                Background = Brushes.Transparent
            };
            _previewHost.MouseEnter += (s, e) => _previewHideTimer?.Stop();
            _previewHost.MouseLeave += (s, e) => HidePreviewSoon();
        }

        // Прогрев: создаём окно превью заранее и показываем его за экраном, чтобы первый реальный показ не мигал.
        private void WarmupPreview()
        {
            try
            {
                if (_previewHost == null) CreatePreviewHost();
                _previewHost!.Left = -10000; _previewHost.Top = -10000;
                _previewHost.Width = 120; _previewHost.Height = 120;
                _previewHost.Content = new Border { Background = new SolidColorBrush(Color.FromRgb(22, 22, 22)), CornerRadius = SysR("SysRadiusL") };
                _previewHost.Show();
                _previewHostHwnd = new WindowInteropHelper(_previewHost).Handle;
                _previewHost.Hide();
            }
            catch { }
        }

        private void ShowPreview()
        {
            if (_contextMenuOpen) return; // ПКМ-меню имеет приоритет
            CancelPreviewClose();
            HideFavoritesPanel(); // взаимоисключение с панелью избранного
            if (_previewApp == null || _previewButton == null) return;
            var handles = _previewApp.WindowHandles.ToList();
            if (!_previewApp.IsRunning || handles.Count < 1) return;

            // Сразу убираем старые thumbnails прошлого превью (иначе первые ~190 мс висят
            // старые скрины, пока не сработает отложенная регистрация новых).
            ClearThumbs();

            // Готовим снимок экрана заранее (хост ещё не показан -> фон без превью).
            _peekSnapshot = CaptureScreen();

            int n = handles.Count;
            double cardW = ThumbW + 2 * CardPad;
            double cardH = CardPad + ThumbH + TitleH;

            // Палитра от фона панели: текст/hover контрастные, акцент — системный.
            Color bgC = Colors.Gray;
            try { bgC = (Color)ColorConverter.ConvertFromString(Settings.BackgroundColor); } catch { }
            bool lightBg = (0.299 * bgC.R + 0.587 * bgC.G + 0.114 * bgC.B) > 150;
            var textBrush = new SolidColorBrush(lightBg ? Color.FromRgb(26, 26, 26) : Colors.White);
            var hoverBrush = new SolidColorBrush(lightBg ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(28, 255, 255, 255));
            var accentBrush = Application.Current.Resources["AccentBrush"] as Brush ?? new SolidColorBrush(Color.FromRgb(0, 120, 215));
            ImageSource? appIcon = null;
            try { appIcon = new AppIconConverter().Convert(_previewApp, typeof(ImageSource), null!, System.Globalization.CultureInfo.CurrentCulture) as ImageSource; } catch { }

            // Ориентация карточек: по настройке, либо авто (боковой док -> столбец, верх/низ -> ряд).
            // Сетка — как авто по направлению, но линии короче: квадрат (9 окон -> 3×3, 4 -> 2×2).
            bool squareGrid = Settings.PreviewOrientation == PreviewOrientationMode.Grid;
            bool vertical = Settings.PreviewOrientation switch
            {
                PreviewOrientationMode.Horizontal => false,
                PreviewOrientationMode.Vertical => true,
                _ => Settings.Position == DockPosition.Left || Settings.Position == DockPosition.Right
            };
            // Сетка: сколько карточек влезает в линию вдоль края монитора; остальные — переносом на
            // следующую линию (от дока). Линии выравниваем: 8 окон при 7 местах -> 4+4, а не 7+1.
            double mainStep = (vertical ? cardH : cardW) + Gap;   // шаг вдоль линии
            double crossStep = (vertical ? cardW : cardH) + Gap;  // шаг между линиями
            int perLine = n;
            try
            {
                var scr = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(sc => sc.DeviceName == _profileName)
                          ?? System.Windows.Forms.Screen.PrimaryScreen;
                double dpiS = 1.0;
                var srcS = PresentationSource.FromVisual(this);
                if (srcS?.CompositionTarget != null) dpiS = srcS.CompositionTarget.TransformToDevice.M11;
                if (scr != null)
                {
                    double avail = (vertical ? scr.WorkingArea.Height : scr.WorkingArea.Width) / dpiS - 16; // поля 8+8 как в ClampToMonitor
                    int maxPer = Math.Max(1, (int)Math.Floor((avail - 2 * Pad + Gap) / mainStep));
                    if (squareGrid) maxPer = Math.Min(maxPer, (int)Math.Ceiling(Math.Sqrt(n)));
                    int linesNeeded = (int)Math.Ceiling(n / (double)maxPer);
                    perLine = (int)Math.Ceiling(n / (double)linesNeeded);
                }
            }
            catch { }
            int lines = (int)Math.Ceiling(n / (double)perLine);

            // Левый-верхний угол карточки k в сетке.
            Point SlotPos(int k)
            {
                double a = Pad + (k % perLine) * mainStep, b = Pad + (k / perLine) * crossStep;
                return vertical ? new Point(b, a) : new Point(a, b);
            }
            // Слот, в который попадает центр перетаскиваемой карточки.
            int SlotFromCenter(Point c)
            {
                double a = vertical ? c.Y : c.X, b = vertical ? c.X : c.Y;
                int pos = Math.Clamp((int)Math.Floor((a - Pad + Gap / 2) / mainStep), 0, perLine - 1);
                int line = Math.Clamp((int)Math.Floor((b - Pad + Gap / 2) / crossStep), 0, lines - 1);
                return Math.Clamp(line * perLine + pos, 0, n - 1);
            }

            double gridMain = Pad * 2 + perLine * mainStep - Gap;
            double gridCross = Pad * 2 + lines * crossStep - Gap;
            double winW = vertical ? gridCross : gridMain;
            double winH = vertical ? gridMain : gridCross;

            // Canvas: рамки карточек, заголовки, прозрачные кликабельные области.
            var canvas = new Canvas { Width = winW, Height = winH };
            var cardRects = new List<Rect>(); // DIP-прямоугольники областей thumbnail (внутри рамки)

            // Живая перестановка: у каждой карточки общий TranslateTransform для всех её частей;
            // DWM-миниатюру двигаем отдельно (rcDestination). Соседи плавно раздвигаются, перетаскиваемая
            // карточка едет за курсором.
            var cardParts = new List<UIElement>[n];
            var cardTT = new TranslateTransform[n];
            for (int k = 0; k < n; k++) { cardParts[k] = new List<UIElement>(); cardTT[k] = new TranslateTransform(); }
            var thumbByCard = new Dictionary<int, IntPtr>();
            var pvOff = new Vector[n];
            Point pvPointer = default;
            double pvDpi = 1.0;
            DispatcherTimer? pvAnim = null;

            void AddPart(int k, UIElement el) { el.RenderTransform = cardTT[k]; cardParts[k].Add(el); }

            void ApplyCardOffset(int k)
            {
                cardTT[k].X = pvOff[k].X; cardTT[k].Y = pvOff[k].Y;
                if (thumbByCard.TryGetValue(k, out var th) && k < cardRects.Count)
                {
                    var r0 = cardRects[k];
                    double dx = pvOff[k].X, dy = pvOff[k].Y;
                    var props = new Win32Helper.DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = Win32Helper.DWM_TNP_RECTDESTINATION,
                        rcDestination = new Win32Helper.RECT
                        {
                            Left = (int)Math.Round((r0.Left + dx) * pvDpi),
                            Top = (int)Math.Round((r0.Top + dy) * pvDpi),
                            Right = (int)Math.Round((r0.Right + dx) * pvDpi),
                            Bottom = (int)Math.Round((r0.Bottom + dy) * pvDpi)
                        }
                    };
                    try { Win32Helper.DwmUpdateThumbnailProperties(th, ref props); } catch { }
                }
            }

            void ReorderTick(int src)
            {
                // Перетаскиваемая — строго за курсором (в пределах сетки).
                var p0 = SlotPos(src);
                double maxX = canvas.Width - Pad - cardW, maxY = canvas.Height - Pad - cardH;
                var off = pvPointer - _pvDragStart;
                pvOff[src] = new Vector(Math.Clamp(p0.X + off.X, Pad, Math.Max(Pad, maxX)) - p0.X,
                                        Math.Clamp(p0.Y + off.Y, Pad, Math.Max(Pad, maxY)) - p0.Y);
                // Слот — по центру перетаскиваемой карточки.
                int to = SlotFromCenter(new Point(p0.X + pvOff[src].X + cardW / 2, p0.Y + pvOff[src].Y + cardH / 2));
                for (int j = 0; j < n; j++)
                {
                    if (j != src)
                    {
                        int jj = j < src ? j : j - 1;
                        if (jj >= to) jj++;
                        Vector target = SlotPos(jj) - SlotPos(j);
                        pvOff[j] += (target - pvOff[j]) * 0.35;
                        if ((target - pvOff[j]).Length < 0.5) pvOff[j] = target;
                    }
                    ApplyCardOffset(j);
                }
                _pvDropIndex = to;
            }

            void StopReorderAnim(bool reset)
            {
                pvAnim?.Stop(); pvAnim = null;
                if (reset)
                {
                    for (int j = 0; j < n; j++)
                    {
                        pvOff[j] = new Vector(); ApplyCardOffset(j);
                        foreach (var part in cardParts[j]) Panel.SetZIndex(part, 0);
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                var slot0 = SlotPos(i);
                double cx = slot0.X, cy = slot0.Y;

                // Карточка: в покое невидима (сливается с подложкой), при наведении — лёгкий фон + рамка акцента.
                var frame = new Border
                {
                    Width = cardW,
                    Height = cardH,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    CornerRadius = SysR("SysRadiusM"),
                    Background = Brushes.Transparent
                };
                Canvas.SetLeft(frame, cx);
                Canvas.SetTop(frame, cy);
                canvas.Children.Add(frame);
                AddPart(i, frame);

                // Заголовок
                var sb = new System.Text.StringBuilder(256);
                Win32Helper.GetWindowText(handles[i], sb, sb.Capacity);
                string title = sb.ToString();
                // Строка заголовка: иконка приложения + название окна (место справа — под крестик).
                var titleRow = new DockPanel { Width = ThumbW, Height = TitleH, LastChildFill = true, IsHitTestVisible = false };
                // Иконка самого окна (у Проводника — своя у каждой папки), иначе — иконка приложения.
                var winIcon = GetWindowIconImage(handles[i]) ?? appIcon;
                if (winIcon != null)
                {
                    var ic = new Image { Source = winIcon, Width = 14, Height = 14, Margin = new Thickness(1, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                    RenderOptions.SetBitmapScalingMode(ic, BitmapScalingMode.HighQuality);
                    DockPanel.SetDock(ic, Dock.Left);
                    titleRow.Children.Add(ic);
                }
                var tb = new TextBlock
                {
                    Text = title,
                    Foreground = textBrush,
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 24, 0)
                };
                titleRow.Children.Add(tb);
                Canvas.SetLeft(titleRow, cx + CardPad);
                Canvas.SetTop(titleRow, cy + CardPad + ThumbH);
                canvas.Children.Add(titleRow);
                AddPart(i, titleRow);

                // Свёрнутое окно: DWM-превью недоступно -> кэш-снимок или иконка приложения.
                if (Win32Helper.IsIconic(handles[i]))
                {
                    FrameworkElement content;
                    if (_snapshotCache.TryGetValue(handles[i], out var cached))
                    {
                        content = new Image { Source = cached, Stretch = Stretch.Uniform, Width = ThumbW, Height = ThumbH };
                        Canvas.SetLeft(content, cx + CardPad);
                        Canvas.SetTop(content, cy + CardPad);
                    }
                    else
                    {
                        var icon = AppIconConverter.ExtractIconFromPath(_previewApp?.Path ?? string.Empty);
                        if (icon != null)
                        {
                            content = new Image { Source = icon, Stretch = Stretch.Uniform, Width = 48, Height = 48 };
                            Canvas.SetLeft(content, cx + CardPad + (ThumbW - 48) / 2);
                            Canvas.SetTop(content, cy + CardPad + (ThumbH - 48) / 2);
                        }
                        else
                        {
                            content = new TextBlock { Text = Loc.T("preview_minimized"), Foreground = textBrush, Opacity = 0.6, FontSize = 12 };
                            Canvas.SetLeft(content, cx + CardPad + 8);
                            Canvas.SetTop(content, cy + CardPad + ThumbH / 2 - 8);
                        }
                    }
                    canvas.Children.Add(content);
                    AddPart(i, content);
                }

                // Кликабельная область поверх thumbnail (Grid, чтобы крестик был ДОЧЕРНИМ
                // и наведение на него не роняло hover карточки).
                var hit = new Grid
                {
                    Width = cardW,
                    Height = cardH,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Tag = handles[i]
                };
                // Крестик "закрыть окно" в правом верхнем углу (виден при наведении).
                // Крестик в стиле Win11: прозрачный, при наведении — красный #C42B1C с белым глифом.
                var closeGlyph = new TextBlock
                {
                    Text = "\uE8BB", // Segoe MDL2/Fluent: ChromeClose
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = textBrush,
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var closeBtn = new Border
                {
                    Width = 22,
                    Height = 20,
                    Margin = new Thickness(0, 0, CardPad - 2, (TitleH - 20) / 2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom, // строка заголовка (не под thumbnail)
                    CornerRadius = SysR("SysRadiusS"),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Opacity = 0,
                    Child = closeGlyph
                };
                closeBtn.MouseEnter += (s, e) => { closeBtn.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)); closeGlyph.Foreground = Brushes.White; };
                closeBtn.MouseLeave += (s, e) => { closeBtn.Background = Brushes.Transparent; closeGlyph.Foreground = textBrush; };
                hit.Children.Add(closeBtn);
                IntPtr hwndLocal = handles[i];
                int idxLocal = i;

                // --- Перетаскивание карточки (порядок окон) ---
                hit.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    if (closeBtn.IsMouseOver) return; // крестик — не драг
                    _pvDragIndex = idxLocal;
                    _pvDragStart = e.GetPosition(canvas);
                    _pvDragging = false;
                    hit.CaptureMouse();
                };
                hit.MouseMove += (s, e) =>
                {
                    if (_pvDragIndex != idxLocal || e.LeftButton != MouseButtonState.Pressed) return;
                    var p = e.GetPosition(canvas);
                    if (!_pvDragging)
                    {
                        if (Math.Abs(p.X - _pvDragStart.X) < 6 && Math.Abs(p.Y - _pvDragStart.Y) < 6) return;
                        _pvDragging = true;
                        _peekDebounceTimer?.Stop();
                        frame.Opacity = 0.85;
                        frame.BorderBrush = accentBrush;
                        foreach (var part in cardParts[idxLocal]) Panel.SetZIndex(part, 50); // поверх соседей
                        pvDpi = 1.0;
                        var psrc = PresentationSource.FromVisual(canvas);
                        if (psrc?.CompositionTarget != null) pvDpi = psrc.CompositionTarget.TransformToDevice.M11;
                        pvAnim = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
                        pvAnim.Tick += (s2, e2) => ReorderTick(idxLocal);
                        pvAnim.Start();
                    }
                    pvPointer = p;
                };
                hit.LostMouseCapture += (s, e) =>
                {
                    if (_pvDragIndex != idxLocal) return;
                    if (_pvDragging && !_pvFinishing)
                    {
                        // Захват потерян посреди перестановки (Alt+Tab и т.п.) — откатываем.
                        _pvDragging = false;
                        StopReorderAnim(reset: true);
                        frame.Opacity = 1;
                    }
                    if (!_pvDragging) _pvDragIndex = -1;
                };

                hit.MouseEnter += (s, e) =>
                {
                    if (_pvDragging) return; // во время перестановки не подсвечиваем и не поднимаем окна
                    frame.Background = hoverBrush;     // подсветка сразу
                    frame.BorderBrush = accentBrush;
                    closeBtn.Opacity = 1; // показать крестик
                    // Подъём окна и слой — с небольшой задержкой (debounce): мимолётное
                    // проскальзывание по карточке не дёргает окна.
                    _peekPendingHwnd = hwndLocal;
                    EnsurePeekDebounce();
                    _peekDebounceTimer!.Stop();
                    _peekDebounceTimer.Start();
                };
                hit.MouseLeave += (s, e) =>
                {
                    frame.Background = Brushes.Transparent;
                    frame.BorderBrush = Brushes.Transparent;
                    closeBtn.Opacity = 0; // скрыть крестик
                    // Если не успели поднять это окно — отменяем ожидание.
                    if (_peekPendingHwnd == hwndLocal) _peekDebounceTimer?.Stop();
                };
                hit.MouseLeftButtonUp += (s, e) =>
                {
                    if (_pvDragging && _pvDragIndex == idxLocal)
                    {
                        // Завершение перестановки: переносим окно в WindowHandles и перестраиваем превью.
                        _pvFinishing = true;
                        ReorderTick(idxLocal);
                        int to = _pvDropIndex;
                        StopReorderAnim(reset: false);
                        if (hit.IsMouseCaptured) hit.ReleaseMouseCapture();
                        _pvFinishing = false;
                        _pvDragging = false; _pvDragIndex = -1;
                        frame.Opacity = 1;
                        var app = _previewApp;
                        if (to == idxLocal) StopReorderAnim(reset: true); // вернули на место — просто собрать обратно
                        if (app != null && to != idxLocal && idxLocal < app.WindowHandles.Count)
                        {
                            var h = app.WindowHandles[idxLocal];
                            app.WindowHandles.RemoveAt(idxLocal);
                            app.WindowHandles.Insert(Math.Clamp(to, 0, app.WindowHandles.Count), h);
                            app.ActiveWindowIndex = app.WindowHandles.IndexOf(app.WindowHandle);
                            app.NotifyWindowsChanged();
                            _previewNoAnim = true;
                            ShowPreview();
                            _previewNoAnim = false;
                        }
                        e.Handled = true;
                        return;
                    }
                    if (hit.IsMouseCaptured) hit.ReleaseMouseCapture();
                    _pvDragIndex = -1;
                    // Если этим же кликом только что закрылось ПКМ-меню — не активируем окно.
                    if (Environment.TickCount - _peekMenuClosedTick < 300) return;
                    try
                    {
                        if (Win32Helper.IsIconic(hwndLocal)) Win32Helper.ShowWindow(hwndLocal, 9);
                        Win32Helper.SetForegroundWindow(hwndLocal);
                    }
                    catch { }
                    _peekPrevForeground = IntPtr.Zero; // окно активировано кликом - не возвращаем прежнее
                    _peekRestoredIconic = IntPtr.Zero; // оставляем развёрнутым
                    HidePreview();
                };
                closeBtn.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true; // не активировать окно
                    try { Win32Helper.PostMessage(hwndLocal, Win32Helper.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                    _peekPrevForeground = IntPtr.Zero;
                    _peekRestoredIconic = IntPtr.Zero;
                    HidePreview(); // вариант 2: закрываем всё превью
                };
                // ПКМ по карточке -> меню действий для этого окна.
                hit.MouseRightButtonUp += (s, e) =>
                {
                    e.Handled = true; // не активировать окно
                    var menu = new ContextMenu();

                    var miClose = new MenuItem { Header = Loc.T("m_close") };
                    miClose.Click += (s2, e2) =>
                    {
                        try { Win32Helper.PostMessage(hwndLocal, Win32Helper.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                        _peekPrevForeground = IntPtr.Zero; _peekRestoredIconic = IntPtr.Zero;
                        HidePreview();
                    };

                    var miMin = new MenuItem { Header = Loc.T("m_minimize") };
                    miMin.Click += (s2, e2) =>
                    {
                        try { Win32Helper.ShowWindow(hwndLocal, Win32Helper.SW_MINIMIZE); } catch { }
                        HidePreview();
                    };

                    bool zoomed = false; try { zoomed = Win32Helper.IsZoomed(hwndLocal); } catch { }
                    var miMax = new MenuItem { Header = zoomed ? Loc.T("m_restore") : Loc.T("m_maximize") };
                    miMax.Click += (s2, e2) =>
                    {
                        try { Win32Helper.ShowWindow(hwndLocal, zoomed ? Win32Helper.SW_RESTORE : Win32Helper.SW_MAXIMIZE); } catch { }
                        _peekPrevForeground = IntPtr.Zero; _peekRestoredIconic = IntPtr.Zero;
                        HidePreview();
                    };

                    menu.Items.Add(miClose);
                    menu.Items.Add(miMin);
                    menu.Items.Add(miMax);

                    // Карточка Проводника -> добавить папку этого окна в избранное.
                    // Карточка окна с документом -> добавить файл этого окна в избранные файлы.
                    if (Settings.EnableFavoriteFiles && _previewApp != null && !IsExplorerApp(_previewApp))
                    {
                        string? fp = null;
                        try { fp = FindOpenFilePath(_previewApp, hwndLocal); } catch { }
                        if (fp != null && !Settings.FavoriteFiles.Contains(fp, StringComparer.OrdinalIgnoreCase))
                        {
                            var miFile = new MenuItem { Header = Loc.T("m_addFileFav") };
                            miFile.Click += (s2, e2) => AddFilesToFavorites(new[] { fp });
                            menu.Items.Add(new Separator());
                            menu.Items.Add(miFile);
                        }
                    }
                    if (Settings.EnableFavorites && IsExplorerApp(_previewApp))
                    {
                        string? folder = GetExplorerFolderPath(hwndLocal);
                        if (folder != null)
                        {
                            var miFav = new MenuItem { Header = Loc.T("m_addFolderFav") };
                            miFav.Click += (s2, e2) => AddFoldersToFavorites(new[] { folder });
                            menu.Items.Add(new Separator());
                            menu.Items.Add(miFav);
                        }
                    }
                    menu.Items.Add(new Separator()); // отделяем опасное действие

                    var miCloseAll = new MenuItem { Header = Loc.T("m_closeAll") };
                    miCloseAll.Click += (s2, e2) =>
                    {
                        var app = _previewApp;
                        if (app != null)
                            foreach (var h in app.WindowHandles.ToList())
                            {
                                try { Win32Helper.PostMessage(h, Win32Helper.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                            }
                        HidePreview();
                    };
                    menu.Items.Add(miCloseAll);

                    // Пока меню открыто — не закрываем превью; после закрытия возобновляем.
                    // Счётчик открытых меню карточек: при ПКМ по второй карточке первое меню
                    // закрывается ПОСЛЕ открытия второго, и его Closed не должен прятать превью.
                    menu.Opened += (s2, e2) => { _previewMenusOpen++; _previewHideTimer?.Stop(); };
                    menu.Closed += (s2, e2) =>
                    {
                        _previewMenusOpen = Math.Max(0, _previewMenusOpen - 1);
                        _peekMenuClosedTick = Environment.TickCount;
                        // Закрываем превью только если курсор ушёл с хоста; если он на карточке — оставляем.
                        if (_previewMenusOpen == 0 && !IsCursorOverPreviewHost()) HidePreviewSoon();
                    };
                    menu.IsOpen = true;
                };
                Canvas.SetLeft(hit, cx);
                Canvas.SetTop(hit, cy);
                canvas.Children.Add(hit);
                AddPart(i, hit);

                cardRects.Add(new Rect(cx + CardPad, cy + CardPad, ThumbW, ThumbH));
            }

            // Хост-окно
            if (_previewHost == null) CreatePreviewHost();
            var previewScale = new ScaleTransform(1, 1);
            // Подложка — продолжение панели: тот же объёмный градиент (с акцентом), обводка и тень.
            // Прозрачность не ниже 0.85, чтобы текст и миниатюры читались на любом фоне.
            Brush rootBg;
            try
            {
                rootBg = (Brush)new WingGradientConverter().Convert(new object[]
                {
                    Settings.BackgroundColor, Math.Max(Settings.BackgroundOpacity, 0.85), Settings.Position.ToString(),
                    Settings.UseAccentShadow, Settings.AccentIntensity
                }, typeof(Brush), null!, System.Globalization.CultureInfo.CurrentCulture);
            }
            catch { rootBg = new SolidColorBrush(bgC); }

            double rootBT = (MainBorder != null && MainBorder.BorderThickness.Left > 0) ? MainBorder.BorderThickness.Left : 1.0;
            Brush rootBB = (MainBorder != null && MainBorder.BorderThickness.Left > 0 && MainBorder.BorderBrush != null)
                ? MainBorder.BorderBrush
                : new SolidColorBrush(lightBg ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255));

            var rootBorder = new Border
            {
                Background = rootBg,
                BorderBrush = rootBB,
                BorderThickness = new Thickness(rootBT),
                CornerRadius = SysR("SysRadiusL"),
                Margin = new Thickness(ShadowM),
                Child = canvas,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = previewScale
            };
            if (PanelShadow != null)
                rootBorder.Effect = new DropShadowEffect { Color = PanelShadow.Color, Opacity = PanelShadow.Opacity, BlurRadius = PanelShadow.BlurRadius, ShadowDepth = 0 };

            // Миниатюры DWM позиционируются в координатах окна: учитываем поле под тень и обводку.
            double rectOff = ShadowM + rootBT;
            for (int k = 0; k < cardRects.Count; k++) cardRects[k] = new Rect(cardRects[k].X + rectOff, cardRects[k].Y + rectOff, cardRects[k].Width, cardRects[k].Height);

            canvas.Width = winW - 2 * rootBT;
            canvas.Height = winH - 2 * rootBT;
            _previewHost!.Content = rootBorder;
            _previewHost.Width = winW + 2 * ShadowM;
            _previewHost.Height = winH + 2 * ShadowM;

            // Позиция рядом с кнопкой (по стороне дока); поле под тень не увеличивает видимый зазор.
            PositionPreviewHost(winW + 2 * ShadowM, winH + 2 * ShadowM, ShadowM);

            // Показываем окно невидимым, ждём отрисовки содержимого, затем делаем видимым —
            // иначе первый композиционный кадр прозрачного окна пустой (просвечивает фон).
            _previewHost.Opacity = 0;
            _previewHost.Show();
            RestoreTooltip(ref _tipPreview);
            _tipPreview = SuppressTooltip(_previewButton); // подсказка не висит поверх превью
            _previewHostHwnd = new WindowInteropHelper(_previewHost).Handle;
            _previewHost.Dispatcher.BeginInvoke(new Action(() => { if (_previewHost != null) _previewHost.Opacity = 1; }), System.Windows.Threading.DispatcherPriority.Render);

            // Появление панели превью: scale (с ~67%) + fade, мягкий EaseOut.
            var appearDur = new Duration(TimeSpan.FromMilliseconds(260));
            var appearEase = new QuarticEase { EasingMode = EasingMode.EaseOut };
            var sa = new DoubleAnimation(_previewNoAnim ? 1 : 0.67, 1, appearDur) { EasingFunction = appearEase }; // при перестройке после перестановки — без «подпрыгивания»
            previewScale.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
            previewScale.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
            // Без fade прозрачности: тёмный фон виден сразу (иначе первый кадр прозрачный). Появление — только через scale.
            _peekPrevForeground = Win32Helper.GetForegroundWindow();

            // DPI
            double dpiThumb = 1.0;
            var src = PresentationSource.FromVisual(_previewHost);
            if (src?.CompositionTarget != null) dpiThumb = src.CompositionTarget.TransformToDevice.M11;

            // Регистрация thumbnails — ПОСЛЕ анимации появления: DWM-скрин нельзя масштабировать
            // WPF-трансформом, поэтому показываем его, когда панель уже выросла до полного размера.
            void RegisterThumbs()
            {
                if (_previewHost == null || !_previewHost.IsVisible || _previewClosing) return; // закрывается — не регистрируем
                ClearThumbs();
                thumbByCard.Clear();
                for (int i = 0; i < n; i++)
                {
                    if (Win32Helper.IsIconic(handles[i])) continue; // свёрнутые показаны кэшем/иконкой выше
                    int hr = Win32Helper.DwmRegisterThumbnail(_previewHostHwnd, handles[i], out IntPtr thumb);
                    if (hr != 0 || thumb == IntPtr.Zero) continue;
                    _previewThumbs.Add(thumb);
                    thumbByCard[i] = thumb;

                    var r = cardRects[i];
                    var props = new Win32Helper.DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = Win32Helper.DWM_TNP_RECTDESTINATION | Win32Helper.DWM_TNP_VISIBLE |
                                  Win32Helper.DWM_TNP_OPACITY | Win32Helper.DWM_TNP_SOURCECLIENTAREAONLY,
                        rcDestination = new Win32Helper.RECT
                        {
                            Left = (int)Math.Round(r.Left * dpiThumb),
                            Top = (int)Math.Round(r.Top * dpiThumb),
                            Right = (int)Math.Round(r.Right * dpiThumb),
                            Bottom = (int)Math.Round(r.Bottom * dpiThumb)
                        },
                        opacity = 0,
                        fVisible = true,
                        fSourceClientAreaOnly = true
                    };
                    Win32Helper.DwmUpdateThumbnailProperties(thumb, ref props);
                }
            }
            // Показываем thumbnails по завершении анимации появления (надёжнее, чем Completed
            // на анимации, применённой к двум свойствам). Таймер чуть длиннее анимации.
            var regTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_previewNoAnim ? 20 : 270) };
            regTimer.Tick += (s2, e2) => { regTimer.Stop(); if (_previewClosing) return; RegisterThumbs(); FadeInThumbs(); };
            regTimer.Start();
        }

        // Кэш снимка окна (для показа свёрнутых окон в превью).
        // Плавное проявление DWM-миниатюр (их нельзя масштабировать, но opacity анимируется).
        private void FadeInThumbs()
        {
            if (_previewThumbs.Count == 0) return;
            var start = DateTime.Now;
            const double durMs = 280;
            var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            fadeTimer.Tick += (s, e) =>
            {
                double tt = (DateTime.Now - start).TotalMilliseconds / durMs;
                bool done = tt >= 1;
                if (done) tt = 1;
                byte op = (byte)(255 * (1 - Math.Pow(1 - tt, 3))); // EaseOut
                foreach (var thumb in _previewThumbs)
                {
                    var pr = new Win32Helper.DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = Win32Helper.DWM_TNP_OPACITY,
                        opacity = op,
                        fVisible = true
                    };
                    try { Win32Helper.DwmUpdateThumbnailProperties(thumb, ref pr); } catch { }
                }
                if (done) fadeTimer.Stop();
            };
            fadeTimer.Start();
        }

        public void CacheWindowSnapshot(IntPtr hWnd)
        {
            var snap = CaptureWindow(hWnd);
            if (snap != null) _snapshotCache[hWnd] = snap;
        }

        public void RemovePreviewSnapshot(IntPtr hWnd)
        {
            _snapshotCache.Remove(hWnd);
        }

        // Кисть оттенка матового слоя из HEX-настройки.
        private Brush TintBrush()
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Settings.PeekTint)); }
            catch { return Brushes.Black; }
        }

        // Кривая анимации матового слоя по настройке (одна на появление и исчезновение).
        private IEasingFunction? MakeEasing()
        {
            return Settings.PeekEasing switch
            {
                PeekEasingMode.Linear => null,
                PeekEasingMode.EaseIn => new CubicEase { EasingMode = EasingMode.EaseIn },
                PeekEasingMode.EaseOut => new CubicEase { EasingMode = EasingMode.EaseOut },
                PeekEasingMode.EaseInOut => new CubicEase { EasingMode = EasingMode.EaseInOut },
                PeekEasingMode.Sine => new SineEase { EasingMode = EasingMode.EaseInOut },
                _ => new CubicEase { EasingMode = EasingMode.EaseOut }
            };
        }

        // Захват экрана монитора панели -> BitmapSource (для матового слоя).
        private BitmapSource? CaptureScreen()
        {
            try
            {
                var scr = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName)
                          ?? System.Windows.Forms.Screen.PrimaryScreen;
                if (scr == null) return null;
                var b = scr.WorkingArea; // рабочая область (без полосы таскбара)
                using var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(b.Left, b.Top, 0, 0, new System.Drawing.Size(b.Width, b.Height));
                IntPtr hbmp = bmp.GetHbitmap();
                try
                {
                    var s = Imaging.CreateBitmapSourceFromHBitmap(
                        hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    s.Freeze();
                    return s;
                }
                finally { Win32Helper.DeleteObject(hbmp); }
            }
            catch { return null; }
        }

        // Показать матовый слой поверх всех окон (под целевым). Первый раз — с fade-in.
        private void EnsurePeekDebounce()
        {
            if (_peekDebounceTimer != null) return;
            _peekDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PeekDebounceMs) };
            _peekDebounceTimer.Tick += (s, e) =>
            {
                _peekDebounceTimer!.Stop();
                DoPeek(_peekPendingHwnd);
            };
        }

        // Собственно peek: свернуть предыдущее временно развёрнутое, поднять целевое окно
        // (или развернуть свёрнутое) и подложить под него матовый слой.
        private void DoPeek(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32Helper.IsWindow(hwnd)) return;
            if (_peekRestoredIconic != IntPtr.Zero && _peekRestoredIconic != hwnd
                && Win32Helper.IsWindow(_peekRestoredIconic))
            {
                Win32Helper.ShowWindow(_peekRestoredIconic, Win32Helper.SW_MINIMIZE);
                _peekRestoredIconic = IntPtr.Zero;
            }
            if (Win32Helper.IsIconic(hwnd))
            {
                Win32Helper.ShowWindow(hwnd, Win32Helper.SW_RESTORE);
                _peekRestoredIconic = hwnd;
            }
            else
            {
                RaiseWindowSoft(hwnd);
            }
            ShowPeekOverlay(hwnd);
        }

        private void ShowPeekOverlay(IntPtr target)
        {
            if (_peekOverlay != null && _peekOverlay.IsVisible)
            {
                PlaceOverlayUnder(target);
                return;
            }
            var snap = _peekSnapshot ?? CaptureScreen();
            if (snap == null) return;
            var scr = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName)
                      ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (scr == null) return;
            var b = scr.WorkingArea; // рабочая область (без полосы таскбара)
            double dpi = 1.0;
            var psrc = PresentationSource.FromVisual(this);
            if (psrc?.CompositionTarget != null) dpi = psrc.CompositionTarget.TransformToDevice.M11;

            // Растягиваем снимок за границы экрана на радиус блюра, чтобы размытые края
            // (светлая окантовка) ушли за видимую область; Grid обрезает по границам окна.
            double bleed = Settings.PeekBlur * 2;
            var img = new Image
            {
                Source = snap,
                Stretch = Stretch.Fill,
                Margin = new Thickness(-bleed),
                Effect = new BlurEffect { Radius = Settings.PeekBlur, KernelType = KernelType.Gaussian }
            };
            var dark = new Rectangle { Fill = TintBrush(), Opacity = Settings.PeekDim };
            var grid = new Grid { ClipToBounds = true };
            grid.Children.Add(img);
            grid.Children.Add(dark);

            if (_peekOverlay == null)
            {
                _peekOverlay = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    AllowsTransparency = true,   // нужно для fade (Window.Opacity)
                    Topmost = false
                };
            }
            _peekOverlay.Content = grid;
            _peekOverlay.Left = b.Left / dpi; _peekOverlay.Top = b.Top / dpi;
            _peekOverlay.Width = b.Width / dpi; _peekOverlay.Height = b.Height / dpi;
            _peekOverlay.Opacity = 0;
            _peekOverlay.Show();
            _peekOverlayHwnd = new WindowInteropHelper(_peekOverlay).Handle;
            int ex = Win32Helper.GetWindowLong(_peekOverlayHwnd, Win32Helper.GWL_EXSTYLE);
            Win32Helper.SetWindowLong(_peekOverlayHwnd, Win32Helper.GWL_EXSTYLE,
                ex | Win32Helper.WS_EX_TRANSPARENT | Win32Helper.WS_EX_TOOLWINDOW);
            PlaceOverlayUnder(target); // сразу под целевым окном, до проявления
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(Settings.PeekFadeMs)))
            {
                EasingFunction = MakeEasing()
            };
            _peekOverlay.BeginAnimation(Window.OpacityProperty, fade);
        }

        // Поместить матовый слой СРАЗУ ПОД целевым окном: над ним остаётся только это окно,
        // всё остальное (включая предыдущее поднятое) уходит под слой.
        private void PlaceOverlayUnder(IntPtr target)
        {
            if (_peekOverlayHwnd == IntPtr.Zero || target == IntPtr.Zero) return;
            Win32Helper.SetWindowPos(_peekOverlayHwnd, target, 0, 0, 0, 0,
                Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
        }

        private void HidePeekOverlay()
        {
            if (_peekOverlay == null) return;
            // Уничтожаем окно слоя (не Hide), чтобы следующий показ создавался с нуля со
            // свежим снимком — иначе при переиспользовании мелькает старый кадр (двойное мигание).
            // Плавное исчезновение, симметрично появлению. Поле обнуляем сразу, чтобы новый
            // показ создал новое окно, а это гаснет независимо и закрывается по завершении.
            var closing = _peekOverlay;
            _peekOverlay = null;
            _peekOverlayHwnd = IntPtr.Zero;
            var fadeOut = new DoubleAnimation(closing.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(Settings.PeekFadeMs)))
            {
                EasingFunction = MakeEasing()
            };
            fadeOut.Completed += (s, e) => { try { closing.Close(); } catch { } };
            closing.BeginAnimation(Window.OpacityProperty, fadeOut);
        }

        // Поднять окно на передний план без смены фокуса, обходя foreground-lock
        // (иначе SetWindowPos для окна чужого процесса не поднимает его над активным).
        private void RaiseWindowSoft(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !Win32Helper.IsWindow(hwnd) || Win32Helper.IsIconic(hwnd)) return;
                IntPtr fg = Win32Helper.GetForegroundWindow();
                uint fgThread = Win32Helper.GetWindowThreadProcessId(fg, out _);
                uint curThread = Win32Helper.GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != curThread)
                    attached = Win32Helper.AttachThreadInput(curThread, fgThread, true);
                Win32Helper.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
                Win32Helper.BringWindowToTop(hwnd);
                if (attached)
                    Win32Helper.AttachThreadInput(curThread, fgThread, false);
            }
            catch { }
        }

        // Снимок окна через PrintWindow -> BitmapSource (null, если не удалось/свёрнуто).
        private BitmapSource? CaptureWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !Win32Helper.IsWindow(hWnd) || Win32Helper.IsIconic(hWnd)) return null;
            if (!Win32Helper.GetWindowRect(hWnd, out var rc)) return null;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return null;

            IntPtr srcDC = Win32Helper.GetWindowDC(hWnd);
            if (srcDC == IntPtr.Zero) return null;
            IntPtr memDC = Win32Helper.CreateCompatibleDC(srcDC);
            IntPtr bmp = Win32Helper.CreateCompatibleBitmap(srcDC, w, h);
            IntPtr old = Win32Helper.SelectObject(memDC, bmp);
            try
            {
                bool ok = Win32Helper.PrintWindow(hWnd, memDC, Win32Helper.PW_RENDERFULLCONTENT);
                if (!ok) return null;
                var s = Imaging.CreateBitmapSourceFromHBitmap(
                    bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                s.Freeze();
                return s;
            }
            catch { return null; }
            finally
            {
                Win32Helper.SelectObject(memDC, old);
                Win32Helper.DeleteObject(bmp);
                Win32Helper.DeleteDC(memDC);
                Win32Helper.ReleaseDC(hWnd, srcDC);
            }
        }

        // Иконка конкретного окна: WM_GETICON (сначала маленькая — для строки заголовка 14 px), затем иконки класса.
        private static ImageSource? GetWindowIconImage(IntPtr hwnd)
        {
            try
            {
                IntPtr h = IntPtr.Zero, r;
                foreach (int kind in new[] { Win32Helper.ICON_SMALL2, Win32Helper.ICON_SMALL, Win32Helper.ICON_BIG })
                {
                    if (Win32Helper.SendMessageTimeout(hwnd, Win32Helper.WM_GETICON, (IntPtr)kind, IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out r) != IntPtr.Zero && r != IntPtr.Zero) { h = r; break; }
                }
                if (h == IntPtr.Zero) h = Win32Helper.GetClassLongPtr(hwnd, Win32Helper.GCLP_HICONSM);
                if (h == IntPtr.Zero) h = Win32Helper.GetClassLongPtr(hwnd, Win32Helper.GCLP_HICON);
                if (h == IntPtr.Zero) return null;
                var img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        private void PositionPreviewHost(double winW, double winH, double inset = 0)
        {
            if (_previewButton == null || _previewHost == null) return;
            try
            {
                var p = _previewButton.PointToScreen(new Point(0, 0)); // пиксели
                double dpi = 1.0;
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;

                double btnW = _previewButton.ActualWidth * dpi;
                double btnH = _previewButton.ActualHeight * dpi;
                double wpx = winW * dpi, hpx = winH * dpi;
                double left = p.X, top = p.Y;
                double gap = 6 - inset * dpi; // видимый зазор 6 px от кнопки до подложки

                switch (Settings.Position)
                {
                    case DockPosition.Left: left = p.X + btnW + gap; top = p.Y + btnH / 2 - hpx / 2; break;
                    case DockPosition.Right: left = p.X - wpx - gap; top = p.Y + btnH / 2 - hpx / 2; break;
                    case DockPosition.Top: top = p.Y + btnH + gap; left = p.X + btnW / 2 - wpx / 2; break;
                    case DockPosition.Bottom: top = p.Y - hpx - gap; left = p.X + btnW / 2 - wpx / 2; break;
                }

                ClampToMonitor(ref left, ref top, wpx, hpx, inset * dpi);
                // перевод пикселей -> DIP для Window.Left/Top
                _previewHost.Left = left / dpi;
                _previewHost.Top = top / dpi;
            }
            catch { }
        }
    }
}