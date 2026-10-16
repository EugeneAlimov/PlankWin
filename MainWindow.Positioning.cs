using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media.Animation;
using System.Windows.Forms;
using System.Windows.Controls;

namespace PlankWin
{
    public partial class MainWindow
    {
        // 1. Возвращает координаты самого СТЕКЛА на экране (для расчетов Drag&Drop)
        public Rect SimulateGlassRect(int iconCount)
        {
            Rect workArea = GetTargetScreenRect();
            bool isVertical = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;
            double margin = Settings.IsFloating ? Settings.EdgeMargin : 0;

            // СИНХРОНИЗАЦИЯ С DOCK MORPH ENGINE:
            // Учитываем динамический отступ выделения, который добавляет XAML
            double k = Settings.IconSize / 44.0;
            double morphCellLength = Settings.IconSize + Math.Max(10.0, 10.0 * k);
            double buttonStep = morphCellLength + Settings.IconMargin; double contentSize = (Math.Max(1, iconCount) * buttonStep) + (Settings.PanelPadding * 2);

            // Панель не должна превышать область Viewbox (окно - Margin ZoomOverflow*2 = экран - ZoomOverflow),
            // иначе Viewbox (DownOnly) сжимает панель и она визуально смещается.
            double maxW = workArea.Width - (margin * 2) - ZoomOverflow;
            double maxH = workArea.Height - (margin * 2) - ZoomOverflow;

            double glassW = isVertical ? Settings.Thickness : (Settings.IsAdaptive ? Math.Min(contentSize, maxW) : maxW);
            double glassH = isVertical ? (Settings.IsAdaptive ? Math.Min(contentSize, maxH) : maxH) : Settings.Thickness;

            Point target = GetGlassTarget(workArea, margin, glassW, glassH);
            return new Rect(target.X, target.Y, glassW, glassH);
        }

        // 2. Универсальная математика выравнивания (по центру, слева, справа)
        private Point GetGlassTarget(Rect workArea, double margin, double glassW, double glassH)
        {
            bool isVertical = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;
            double x = 0, y = 0;

            if (isVertical)
            {
                x = _visualPosition == DockPosition.Left ? workArea.Left + margin : workArea.Right - glassW - margin;
                y = _visualAlignment switch
                {
                    DockAlignment.Start => workArea.Top + margin,
                    DockAlignment.End => workArea.Bottom - glassH - margin,
                    _ => workArea.Top + (workArea.Height - glassH) / 2.0
                };
            }
            else
            {
                y = _visualPosition == DockPosition.Top ? workArea.Top + margin : workArea.Bottom - glassH - margin;
                x = _visualAlignment switch
                {
                    DockAlignment.Start => workArea.Left + margin,
                    DockAlignment.End => workArea.Right - glassW - margin,
                    _ => workArea.Left + (workArea.Width - glassW) / 2.0
                };
            }
            return new Point(x, y);
        }

        // 3. Вычисляем координаты ОКНА (Стекло + отступ для тени 40px)
        private void CalculateCoordinates(Rect workArea, double margin, double glassW, double glassH, out double windowX, out double windowY)
        {
            Point glassTarget = GetGlassTarget(workArea, margin, glassW, glassH);
            // ZoomOverflow компенсируется только по толщине (там есть Margin), по длине Margin=0.
            bool isVert = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;
            double _shadowPad = _appsOverflow ? 0.0 : 22.0; // запас по длине для тени/свечения вокруг всей панели
            windowX = glassTarget.X - (isVert ? _crossPad : _shadowPad);
            windowY = glassTarget.Y - (isVert ? _shadowPad : _crossPad);
        }

        // 4. Идеальное скрытие за край монитора
        private void ComputeHiddenPosition()
        {
            _hiddenLeft = _shownLeft;
            _hiddenTop = _shownTop;

            double offset = Settings.Thickness + (Settings.IsFloating ? Settings.EdgeMargin : 0) + ZoomOverflow + 2;

            if (_visualPosition == DockPosition.Top) _hiddenTop -= offset;
            else if (_visualPosition == DockPosition.Bottom) _hiddenTop += offset;
            else if (_visualPosition == DockPosition.Left) _hiddenLeft -= offset;
            else if (_visualPosition == DockPosition.Right) _hiddenLeft += offset;
        }

        // 5. Главный метод позиционирования и анимации
        public void PositionDock(bool skipAnimation = false)
        {
            // ИСПРАВЛЕНИЕ РАССИНХРОНА РЕЗЕРВИРОВАНИЯ:
            // appbar меняет рабочую область (work area). Раньше UpdateAppBar() вызывался
            // в КОНЦЕ метода — уже ПОСЛЕ того, как позиция рассчитана по старой work area.
            // Из-за этого при включении/выключении резервирования панель считала позицию
            // по неактуальной области и съезжала. Теперь обновляем appbar ПЕРВЫМ делом,
            // чтобы GetTargetScreenRect() ниже прочитал уже корректную work area.
            UpdateAppBar();

            Rect workArea = GetTargetScreenRect();

            bool isVertical = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;

            // СИНХРОНИЗАЦИЯ С DOCK MORPH ENGINE: Учитываем динамический отступ выделения
            double k = Settings.IconSize / 44.0;
            double morphCellLength = Settings.IconSize + Math.Max(10.0, 10.0 * k);
            double buttonStep = morphCellLength + Settings.IconMargin; int activeCount = Math.Max(1, Apps.Count(a => !a.IsDeleting));
            // Сумма AppearScale вместо счётчика: анимируемая иконка (AppearScale 0..1) плавно добавляет/убирает свой вклад в размер панели.
            double sumScale = Apps.Where(a => !a.IsDeleting).Sum(a => a.AppearScale);
            if (sumScale < 0.0001) sumScale = 1;
            double contentSize = (sumScale * buttonStep) + (Settings.PanelPadding * 2);

            double newWidth, newHeight;
            double effectiveEdgeMargin = Settings.IsFloating ? Settings.EdgeMargin : 0;

            double availLen = isVertical ? (workArea.Height - effectiveEdgeMargin * 2) : (workArea.Width - effectiveEdgeMargin * 2);
            double effIcon = Settings.IconSize;
            // Переполнение — в любом режиме длины (адаптивный или во всю сторону): сначала чуть ужимаем иконки,
            // дальше — прокрутка с шевронами.
            if (contentSize > availLen && availLen > 0)
            {
                // Точный размер иконки, чтобы activeCount штук влезли в availLen.
                // availLen = activeCount * (effIcon + max(10, 10*effIcon/44) + IconMargin) + padding*2
                double freeForIcons = availLen - (Settings.PanelPadding * 2);
                double perCell = (freeForIcons / activeCount) - Settings.IconMargin;
                // ветка max = 10*effIcon/44:  perCell = effIcon * (1 + 10/44)
                double solved = perCell / (1.0 + 10.0 / 44.0);
                // ветка max = 10 (мелкие иконки): perCell = effIcon + 10
                if (solved < 44.0) solved = perCell - 10.0;
                effIcon = Math.Clamp(solved, Settings.IconSize * 0.85, Settings.IconSize);
                double ek = effIcon / 44.0;
                double eStep = effIcon + Math.Max(10.0, 10.0 * ek) + Settings.IconMargin;
                contentSize = (sumScale * eStep) + (Settings.PanelPadding * 2);
            }
            Settings.EffectiveIconSize = effIcon;
            // Резиновый запас по толщине: лента с отражением ~1.4*icon+50. Запас = её выступ за панель.
            double _lentaCross = 1.8 * effIcon + 70.0;
            _crossPad = Math.Max(ZoomOverflow, ((_lentaCross - Settings.Thickness) / 2.0) + 50.0); // +запас на hover-зум

            if (isVertical)
            {
                newWidth = Settings.Thickness;
                newHeight = Settings.IsAdaptive ? Math.Min(contentSize, workArea.Height - (effectiveEdgeMargin * 2)) : workArea.Height - (effectiveEdgeMargin * 2);
            }
            else
            {
                newHeight = Settings.Thickness;
                newWidth = Settings.IsAdaptive ? Math.Min(contentSize, workArea.Width - (effectiveEdgeMargin * 2)) : workArea.Width - (effectiveEdgeMargin * 2);
            }

            double visibleLen = isVertical ? newHeight : newWidth;
            double innerLen = visibleLen - (Settings.PanelPadding * 2);
            double lentaLen = contentSize - (Settings.PanelPadding * 2);
            _appsOverflow = lentaLen > innerLen + 1;
            _appsVertical = isVertical;
            if (_appsOverflow)
            {
                // Фиксированный зазор при переполнении — место под шеврон + визуальный отступ до иконок.
                double ovPad = 20.0;
                innerLen = visibleLen - (ovPad * 2);
                _appsPad = ovPad;
                _appsMaxScroll = lentaLen - innerLen;
                if (_appsScrollOffset > _appsMaxScroll) _appsScrollOffset = _appsMaxScroll;
                double shift = _appsScrollOffset - _appsMaxScroll;
                if (isVertical) AppsScrollTransform.Y = shift; else AppsScrollTransform.X = shift;
                _appsVisibleLen = innerLen;
                AppsScrollHost.Width = newWidth;
                AppsScrollHost.Height = newHeight;
                System.Windows.Controls.Canvas.SetTop(AppsItemsControl, isVertical ? ovPad : 0);
                System.Windows.Controls.Canvas.SetLeft(AppsItemsControl, isVertical ? 0 : ovPad);
                UpdateAppsClip();
                UpdateChevrons();
            }
            else
            {
                _appsMaxScroll = 0;
                _appsScrollOffset = 0;
                AppsScrollTransform.X = 0;
                AppsScrollTransform.Y = 0;
                AppsScrollHost.ClearValue(FrameworkElement.WidthProperty);
                AppsScrollHost.ClearValue(FrameworkElement.HeightProperty);
                AppsScrollHost.Clip = null;
                AppsScrollHost.OpacityMask = null;
                ChevronStart.Visibility = System.Windows.Visibility.Collapsed;
                ChevronEnd.Visibility = System.Windows.Visibility.Collapsed;
                _appsPad = Settings.PanelPadding;
                // Компенсация смещения Canvas (+pad): ставим в 0, фактический старт становится = pad, зазоры симметричны.
                System.Windows.Controls.Canvas.SetTop(AppsItemsControl, 0);
                System.Windows.Controls.Canvas.SetLeft(AppsItemsControl, 0);
            }

            ApplyItemsOrientation();

            this.ClearValue(Window.WidthProperty);
            this.ClearValue(Window.HeightProperty);
            this.SizeToContent = SizeToContent.WidthAndHeight;

            double startW = MainBorder.ActualWidth > 0 ? MainBorder.ActualWidth : newWidth;
            double startH = MainBorder.ActualHeight > 0 ? MainBorder.ActualHeight : newHeight;

            CalculateCoordinates(workArea, effectiveEdgeMargin, newWidth, newHeight, out _shownLeft, out _shownTop);
            ComputeHiddenPosition();

            double targetX = _isHidden ? _hiddenLeft : _shownLeft;
            double targetY = _isHidden ? _hiddenTop : _shownTop;
            double targetOpacity = _isHidden ? 0.0 : 1.0;

            double startX = double.IsNaN(Left) ? targetX : Left;
            double startY = double.IsNaN(Top) ? targetY : Top;

            if (_isFirstStartup)
            {
                startX = _hiddenLeft;
                startY = _hiddenTop;
                this.Left = _hiddenLeft;
                this.Top = _hiddenTop;
                MainBorder.Width = newWidth;
                MainBorder.Height = newHeight;
                _isFirstStartup = false;
            }
            else
            {
                bool isResizingWhileVisible = !_isHidden && this.Opacity > 0.01 && (startW != newWidth || startH != newHeight);

                if (isResizingWhileVisible)
                {
                    // ИДЕАЛЬНОЕ РАСШИРЕНИЕ БЕЗ ПРЫЖКОВ
                    // Берем ТЕКУЩИЕ физические координаты окна. Окно плавно поедет к targetX/Y 
                    // ровно в тот же момент, когда стекло плавно изменит размер. 
                    // В итоге центр окна останется неподвижным!
                    startX = double.IsNaN(this.Left) ? targetX : this.Left;
                    startY = double.IsNaN(this.Top) ? targetY : this.Top;
                }
                else if (!_isHidden && this.Opacity < 0.01)
                {
                    // Панель выезжает из тени
                    startX = _hiddenLeft;
                    startY = _hiddenTop;
                    this.Left = startX;
                    this.Top = startY;
                }
                else if (_isHidden && this.Opacity > 0.99)
                {
                    // Панель прячется в тень
                    startX = _shownLeft;
                    startY = _shownTop;
                    this.Left = startX;
                    this.Top = startY;
                }
            }

            if (skipAnimation)
            {
                this.BeginAnimation(LeftProperty, null);
                this.BeginAnimation(TopProperty, null);
                this.BeginAnimation(OpacityProperty, null);
                MainBorder.BeginAnimation(WidthProperty, null);
                MainBorder.BeginAnimation(HeightProperty, null);

                this.Left = targetX;
                this.Top = targetY;
                this.Opacity = targetOpacity;
                MainBorder.Width = newWidth;
                MainBorder.Height = newHeight;

                // UpdateAppBar() перенесён в начало PositionDock (синхронизация work area).
                ForceTopmost();
                return;
            }

            var ease = _isHidden ? new QuarticEase { EasingMode = EasingMode.EaseIn } : new QuarticEase { EasingMode = EasingMode.EaseOut };
            var duration = _isHidden ? new Duration(TimeSpan.FromMilliseconds(450)) : new Duration(TimeSpan.FromMilliseconds(350));

            MainBorder.BeginAnimation(WidthProperty, new DoubleAnimation(startW, newWidth, duration) { EasingFunction = ease });
            MainBorder.BeginAnimation(HeightProperty, new DoubleAnimation(startH, newHeight, duration) { EasingFunction = ease });

            if (startX != targetX) BeginAnimation(LeftProperty, new DoubleAnimation(startX, targetX, duration) { EasingFunction = ease });
            else BeginAnimation(LeftProperty, null);

            if (startY != targetY) BeginAnimation(TopProperty, new DoubleAnimation(startY, targetY, duration) { EasingFunction = ease });
            else BeginAnimation(TopProperty, null);

            BeginAnimation(OpacityProperty, new DoubleAnimation(this.Opacity, targetOpacity, duration) { EasingFunction = ease });

            // UpdateAppBar() перенесён в начало PositionDock (синхронизация work area).
            ForceTopmost();
        }

        private void ApplyItemsOrientation()
        {
            bool isVertical = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;
            // Зум-запас только по толщине; по длине не нужен, иначе окно шире экрана и Viewbox сжимает.
            double _sp = _appsOverflow ? 0.0 : 22.0;
            ZoomMarginGrid.Margin = isVertical
                ? new Thickness(_crossPad, _sp, _crossPad, _sp)
                : new Thickness(_sp, _crossPad, _sp, _crossPad);

            // 1. ВДОЛЬ ПАНЕЛИ
            System.Windows.HorizontalAlignment mainHAlign = (Settings.IsAdaptive || _visualAlignment == DockAlignment.Center)
                ? System.Windows.HorizontalAlignment.Center
                : (_visualAlignment == DockAlignment.Start ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right);

            System.Windows.VerticalAlignment mainVAlign = (Settings.IsAdaptive || _visualAlignment == DockAlignment.Center)
                ? System.Windows.VerticalAlignment.Center
                : (_visualAlignment == DockAlignment.Start ? System.Windows.VerticalAlignment.Top : System.Windows.VerticalAlignment.Bottom);

            // 2. ПОПЕРЕК ПАНЕЛИ
            System.Windows.HorizontalAlignment crossHAlign = _visualPosition == DockPosition.Left ? System.Windows.HorizontalAlignment.Left :
                                                             _visualPosition == DockPosition.Right ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Center;

            System.Windows.VerticalAlignment crossVAlign = _visualPosition == DockPosition.Top ? System.Windows.VerticalAlignment.Top :
                                                           _visualPosition == DockPosition.Bottom ? System.Windows.VerticalAlignment.Bottom : System.Windows.VerticalAlignment.Center;

            // 3. ШАБЛОНЫ И ОТСТУПЫ (ИСПРАВЛЕНИЕ: Защита от пересоздания контейнеров)
            var targetTemplate = TryFindResource(isVertical ? "VerticalPanel" : "HorizontalPanel") as ItemsPanelTemplate;
            if (AppsItemsControl.ItemsPanel != targetTemplate)
            {
                AppsItemsControl.ItemsPanel = targetTemplate;
            }

            if (isVertical)
                AppsItemsControl.Margin = new Thickness(0, Settings.PanelPadding, 0, Settings.PanelPadding);
            else
                AppsItemsControl.Margin = new Thickness(Settings.PanelPadding, 0, Settings.PanelPadding, 0);

            // 4. СИНХРОННАЯ ПРИВЯЗКА
            if (MainViewbox != null)
            {
                MainViewbox.HorizontalAlignment = isVertical ? crossHAlign : mainHAlign;
                MainViewbox.VerticalAlignment = isVertical ? mainVAlign : crossVAlign;
            }

            AppsItemsControl.HorizontalAlignment = isVertical ? crossHAlign : mainHAlign;
            AppsItemsControl.VerticalAlignment = isVertical ? mainVAlign : crossVAlign;

            MainBorder.HorizontalAlignment = isVertical ? crossHAlign : mainHAlign;
            MainBorder.VerticalAlignment = isVertical ? mainVAlign : crossVAlign;
        }

        private void ForceTopmost()
        {
            Topmost = true;
            if (!Settings.ReserveScreenSpace || Settings.AutoHide)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) Win32Helper.SetWindowPos(hwnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0, Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
            }
        }

        private bool _isAppBarRegistered = false;
        private string? _lastAppBarKey; // последний выставленный резерв (край|толщина|монитор)
        private int? _appBarOuterEdge;  // внешняя граница резерва в физ. пикселях (null — резерва нет)

        public void UpdateAppBar(bool forceRemove = false)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (Settings.ReserveScreenSpace && !Settings.AutoHide && !forceRemove && !_isForceHidden)
            {
                Win32Helper.APPBARDATA abd = new Win32Helper.APPBARDATA();
                abd.cbSize = Marshal.SizeOf(abd); abd.hWnd = hwnd;
                if (!_isAppBarRegistered) { Win32Helper.SHAppBarMessage(Win32Helper.ABM_NEW, ref abd); _isAppBarRegistered = true; }

                // ИСПРАВЛЕНИЕ 6: UIPosition (чтобы система резервировала место там, где панель реально находится)
                abd.uEdge = _visualPosition switch { DockPosition.Left => Win32Helper.ABE_LEFT, DockPosition.Top => Win32Helper.ABE_TOP, DockPosition.Right => Win32Helper.ABE_RIGHT, _ => Win32Helper.ABE_BOTTOM };
                // Резерв на СВОЁМ мониторе (физические пиксели). Раньше бралась область основного
                // экрана — при двух панелях обе резервировали место на основном, и Windows
                // перекладывала ярлыки рабочего стола между мониторами.
                var scr = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName) ?? System.Windows.Forms.Screen.PrimaryScreen;
                if (scr == null) return;
                var sb = scr.Bounds;
                double dpiK = 1.0;
                var psrc = PresentationSource.FromVisual(this);
                if (psrc?.CompositionTarget != null) dpiK = psrc.CompositionTarget.TransformToDevice.M11;

                // Полочка: иконки выступают за панель внутрь экрана — резервируем и их (иначе налезают на окна).
                int thickness = (int)Math.Round((Settings.IsShelfMode
                    ? Settings.Thickness + _crossPad + Settings.EdgeMargin
                    : Settings.Thickness + Settings.EdgeMargin) * dpiK);

                void Fit()
                {
                    if (abd.uEdge == Win32Helper.ABE_TOP) abd.rc.Bottom = abd.rc.Top + thickness;
                    else if (abd.uEdge == Win32Helper.ABE_BOTTOM) abd.rc.Top = abd.rc.Bottom - thickness;
                    else if (abd.uEdge == Win32Helper.ABE_LEFT) abd.rc.Right = abd.rc.Left + thickness;
                    else if (abd.uEdge == Win32Helper.ABE_RIGHT) abd.rc.Left = abd.rc.Right - thickness;
                }
                // Резерв уже стоит с теми же параметрами — не дёргаем систему. Каждый SETPOS
                // заставляет Explorer заново раскладывать ярлыки рабочего стола (перетасовка).
                string key = $"{_profileName}|{abd.uEdge}|{thickness}|{sb}";
                if (key == _lastAppBarKey) return;

                abd.rc.Left = sb.Left; abd.rc.Top = sb.Top; abd.rc.Right = sb.Right; abd.rc.Bottom = sb.Bottom;
                Fit();
                Win32Helper.SHAppBarMessage(Win32Helper.ABM_QUERYPOS, ref abd);
                Fit(); // QUERYPOS мог сдвинуть край (таскбар и др.) — восстанавливаем толщину от нового края
                Win32Helper.SHAppBarMessage(Win32Helper.ABM_SETPOS, ref abd);
                _lastAppBarKey = key;
                // Внешний край нашего резерва (после согласования с таскбаром и др.) — для позиционирования.
                _appBarOuterEdge = abd.uEdge switch
                {
                    Win32Helper.ABE_LEFT => abd.rc.Left,
                    Win32Helper.ABE_RIGHT => abd.rc.Right,
                    Win32Helper.ABE_TOP => abd.rc.Top,
                    _ => abd.rc.Bottom
                };
            }
            else
            {
                if (_isAppBarRegistered)
                {
                    Win32Helper.APPBARDATA abd = new Win32Helper.APPBARDATA();
                    abd.cbSize = Marshal.SizeOf(abd); abd.hWnd = hwnd;
                    Win32Helper.SHAppBarMessage(Win32Helper.ABM_REMOVE, ref abd); _isAppBarRegistered = false;
                    _lastAppBarKey = null;
                    _appBarOuterEdge = null;
                }
            }
        }

        private Rect GetTargetScreenRect()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var targetScreen = screens.FirstOrDefault(s => s.DeviceName == _profileName) ?? System.Windows.Forms.Screen.PrimaryScreen;

            // Защита от потери монитора
            if (targetScreen == null) return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

            // Рабочая область (без таскбара и других appbar). При резерве наш собственный край
            // возвращаем до границы экрана: панель стоит ВНУТРИ своего резерва. Раньше бралась
            // вся область экрана — и панель центрировалась с учётом таскбара, съезжая вниз.
            var bounds = targetScreen.WorkingArea;
            if (Settings.ReserveScreenSpace && !Settings.AutoHide)
            {
                var full = targetScreen.Bounds;
                // Край резерва, который согласовала система (напр. над таскбаром), иначе — край экрана.
                switch (_visualPosition)
                {
                    case DockPosition.Left: bounds = System.Drawing.Rectangle.FromLTRB(_appBarOuterEdge ?? full.Left, bounds.Top, bounds.Right, bounds.Bottom); break;
                    case DockPosition.Right: bounds = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, _appBarOuterEdge ?? full.Right, bounds.Bottom); break;
                    case DockPosition.Top: bounds = System.Drawing.Rectangle.FromLTRB(bounds.Left, _appBarOuterEdge ?? full.Top, bounds.Right, bounds.Bottom); break;
                    default: bounds = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, _appBarOuterEdge ?? full.Bottom); break;
                }
            }

            // МАГИЯ: Правильно конвертируем аппаратные пиксели WinForms в логические пиксели WPF (с учетом масштаба 125%, 150% и т.д.)
            double dpiX = 1.0, dpiY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }
            else
            {
                using (var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    dpiX = graphics.DpiX / 96.0;
                    dpiY = graphics.DpiY / 96.0;
                }
            }

            return new Rect(bounds.X / dpiX, bounds.Y / dpiY, bounds.Width / dpiX, bounds.Height / dpiY);
        }
    }
}