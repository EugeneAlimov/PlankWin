using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Linq;
using System;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Media;
using System.Collections.Generic;

namespace PlankWin
{
    public partial class MainWindow
    {
        private double _edgePushAccumulator = 0;
        private const double EdgePushThreshold = 200;


        private Window? _glowWindow;
        private Border? _glowBorder;
        private double _currentGlowProgress = 0;
        private bool _lastOverlappingState = false;

        // ВНЕШНИЙ ЗАПРЕТ ПОКАЗА (задел под будущий API).
        // Когда true — панель НЕ выезжает даже по подведению курсора к краю.
        // Предназначен для внешнего управления (например, видеоплеер в полноэкранном
        // режиме выставит этот флаг, чтобы панель не вылезала поверх видео).
        // Управляется извне через SetExternalHideSuppression(); по умолчанию выключен.
        private bool _isSuppressedExternally = false;

        /// <summary>
        /// Внешний API: запретить/разрешить показ панели по курсору.
        /// Например, полноэкранный плеер вызывает SetExternalHideSuppression(true)
        /// на время воспроизведения и SetExternalHideSuppression(false) по выходу.
        /// </summary>
        public void SetExternalHideSuppression(bool suppressed)
        {
            _isSuppressedExternally = suppressed;
        }

        private void SetupAutoHide()
        {
            if (Settings.AutoHide)
            {
                if (_autoHideTimer == null)
                {
                    _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoTimerIntervalMs) };
                    _autoHideTimer.Tick += AutoHideTimer_Tick;
                }
                _autoHideTimer.Start();

                bool isSettingsOpen = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().Any();
                if (!isSettingsOpen)
                {
                    _isHidden = true;
                    PositionDock(skipAnimation: false);
                }
            }
            else
            {
                if (_autoHideTimer != null) _autoHideTimer.Stop();
                _isHidden = false;
                PositionDock(skipAnimation: false);
                UpdateGlow(0);
            }
        }

        // =========================================================
        // ПРИГЛУШЕННЫЙ ГЕНЕРАТОР РАДУГИ
        // =========================================================
        private Color GetSpectrumColor(double progress)
        {
            // progress (0.0 - 1.0) переводим в Hue (0 - 300 градусов: от красного до фиолетового)
            double hue = progress * 300.0;

            int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            // v = 210 (вместо 255) делает цвета более густыми, "неоновыми" и не режущими глаз
            byte v = 210;
            byte p = 0;
            byte q = (byte)Convert.ToInt32(v * (1 - f));
            byte t = (byte)Convert.ToInt32(v * f);

            if (hi == 0) return Color.FromArgb(255, v, t, p);
            else if (hi == 1) return Color.FromArgb(255, q, v, p);
            else if (hi == 2) return Color.FromArgb(255, p, v, t);
            else if (hi == 3) return Color.FromArgb(255, p, q, v);
            else if (hi == 4) return Color.FromArgb(255, t, p, v);
            else return Color.FromArgb(255, v, p, q);
        }

        private void UpdateGlow(double progress)
        {
            if (progress <= 0)
            {
                if (_glowWindow != null) _glowWindow.Opacity = 0;
                return;
            }

            Color currentColor = GetSpectrumColor(progress);

            if (_glowWindow == null)
            {
                _glowBorder = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Effect = new DropShadowEffect
                    {
                        BlurRadius = 20,  // Мягкое размытие
                        ShadowDepth = 10,
                        Opacity = 0.5     // Приглушенная тень, чтобы не слепила
                    }
                };

                double glowMargin = 20;
                var glowGrid = new Grid { Margin = new Thickness(glowMargin) };
                glowGrid.Children.Add(_glowBorder);

                _glowWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    IsHitTestVisible = false,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Content = glowGrid,
                    Opacity = 0
                };

                var hwnd = new WindowInteropHelper(_glowWindow).EnsureHandle();
                int exStyle = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE);
                Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE, exStyle | Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_TRANSPARENT | 0x08000000);

                _glowWindow.Show();
            }

            // ПРИМЕНЯЕМ ЦВЕТ: Красим и саму линию, и её тень
            if (_glowBorder != null)
            {
                // Делаем линию слегка прозрачной, чтобы она смотрелась как лазер
                _glowBorder.Background = new SolidColorBrush(Color.FromArgb(230, currentColor.R, currentColor.G, currentColor.B));
                if (_glowBorder.Effect is DropShadowEffect shadow)
                {
                    shadow.Color = currentColor;
                }
            }

            // Плавное появление всего окна свечения
            _glowWindow.Opacity = progress;

            if (MainBorder != null && _glowBorder != null)
            {
                bool isVertical = Settings.Position == DockPosition.Left || Settings.Position == DockPosition.Right;

                // Физика: Утолщение от 3 до 6 пикселей
                double currentThickness = 3 + (2 * progress);

                _glowBorder.Width = isVertical ? currentThickness : MainBorder.ActualWidth;
                _glowBorder.Height = isVertical ? MainBorder.ActualHeight : currentThickness;

                var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName) ?? System.Windows.Forms.Screen.PrimaryScreen;
                if (screen != null)
                {
                    double dpiX = 1.0, dpiY = 1.0;
                    var source = PresentationSource.FromVisual(this);
                    if (source?.CompositionTarget != null) { dpiX = source.CompositionTarget.TransformToDevice.M11; dpiY = source.CompositionTarget.TransformToDevice.M22; }

                    double margin = 20;
                    double viewboxW = MainViewbox?.ActualWidth ?? MainBorder.ActualWidth;
                    double viewboxH = MainViewbox?.ActualHeight ?? MainBorder.ActualHeight;

                    double panelLeft = _shownLeft + ZoomOverflow + (viewboxW - MainBorder.ActualWidth) / 2;
                    double panelTop = _shownTop + ZoomOverflow + (viewboxH - MainBorder.ActualHeight) / 2;

                    if (Settings.Position == DockPosition.Bottom)
                    {
                        _glowWindow.Left = panelLeft - margin;
                        _glowWindow.Top = (screen.Bounds.Bottom / dpiY) - margin - _glowBorder.Height;
                    }
                    else if (Settings.Position == DockPosition.Top)
                    {
                        _glowWindow.Left = panelLeft - margin;
                        _glowWindow.Top = (screen.Bounds.Top / dpiY) - margin;
                    }
                    else if (Settings.Position == DockPosition.Left)
                    {
                        _glowWindow.Left = (screen.Bounds.Left / dpiX) - margin;
                        _glowWindow.Top = panelTop - margin;
                    }
                    else if (Settings.Position == DockPosition.Right)
                    {
                        _glowWindow.Left = (screen.Bounds.Right / dpiX) - margin - _glowBorder.Width;
                        _glowWindow.Top = panelTop - margin;
                    }
                }
            }
        }

        // Импульс в накопитель за каждое raw-событие движения, приходящее пока курсор
        // прижат к самому краю экрана. Аналог «сенсор ещё шлёт смещение в упор».
        private const double AbsPushImpulse = 60;

        public void ProcessRawMouseInput(int rawX, int rawY, ushort usFlags)
        {
            bool isAbsolute = (usFlags & Win32Helper.MOUSE_MOVE_ABSOLUTE) != 0;

            if (!_isHidden || _isTransitioning || _isForceHidden || !Settings.AutoHide) return;

            Win32Helper.GetCursorPos(out var pt);
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName) ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

            int edgeTolerance = 4;
            bool isPushing = false;

            if (isAbsolute)
            {
                // В абсолютном режиме (VM/RDP/планшет) rawX/rawY — это позиция, нормированная
                // средой (в VirtualBox край приходит НЕ ровно 0/65535, а со сдвигом), поэтому
                // на сырые координаты опираться нельзя. Определяем упор в край по ПИКСЕЛЬНОЙ
                // позиции курсора (GetCursorPos + границы экрана), а сам факт прихода
                // raw-события трактуем как «мышь продолжает толкать». Пока курсор стоит у
                // самого края (позиция почти не меняется) и события идут — копим импульс.
                bool atEdge = false;
                switch (Settings.Position)
                {
                    case DockPosition.Bottom: atEdge = pt.Y >= screen.Bounds.Bottom - edgeTolerance; break;
                    case DockPosition.Top: atEdge = pt.Y <= screen.Bounds.Top + edgeTolerance; break;
                    case DockPosition.Left: atEdge = pt.X <= screen.Bounds.Left + edgeTolerance; break;
                    case DockPosition.Right: atEdge = pt.X >= screen.Bounds.Right - edgeTolerance; break;
                }

                // «Толкает вдоль оси дока» — курсор почти не движется по перпендикулярной оси
                // (чтобы прокрутка вдоль правого края НЕ считалась упором): для лево/право
                // важно, что X упёрт, а Y может гулять — но при прокрутке X не у края, поэтому
                // atEdge уже отсекает скролл. Импульс даём за каждое событие у края.
                if (atEdge)
                {
                    _edgePushAccumulator += AbsPushImpulse;
                    isPushing = true;
                }
            }
            else
            {
                // Обычная мышь: rawX/rawY — это смещение (дельта).
                int deltaX = rawX, deltaY = rawY;
                switch (Settings.Position)
                {
                    case DockPosition.Bottom: if (pt.Y >= screen.Bounds.Bottom - edgeTolerance && deltaY > 0) { _edgePushAccumulator += deltaY; isPushing = true; } break;
                    case DockPosition.Top: if (pt.Y <= screen.Bounds.Top + edgeTolerance && deltaY < 0) { _edgePushAccumulator += Math.Abs(deltaY); isPushing = true; } break;
                    case DockPosition.Left: if (pt.X <= screen.Bounds.Left + edgeTolerance && deltaX < 0) { _edgePushAccumulator += Math.Abs(deltaX); isPushing = true; } break;
                    case DockPosition.Right: if (pt.X >= screen.Bounds.Right - edgeTolerance && deltaX > 0) { _edgePushAccumulator += deltaX; isPushing = true; } break;
                }
            }

            if (isPushing)
            {
                _currentGlowProgress = 0.4 + (0.6 * Math.Clamp(_edgePushAccumulator / EdgePushThreshold, 0, 1.0));
                UpdateGlow(_currentGlowProgress);

                if (_edgePushAccumulator >= EdgePushThreshold)
                {
                    _edgePushAccumulator = 0;
                    _currentGlowProgress = 0;
                    UpdateGlow(0);
                    ShowDock("edge-push");
                }
            }
        }

        private void AutoHideTimer_Tick(object? sender, EventArgs e)
        {
            if (_isForceHidden || _isTransitioning) return;

            bool isSettingsOpen = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().Any();
            if ((_activeIconMenu != null && _activeIconMenu.IsOpen) || _isDragging || _isExternalDragActive || _openContextMenusCount > 0 || isSettingsOpen)
            {
                _visibleTimeMs = 0;
                if (_isHidden) { ShowDock("menu-or-drag"); }
                return;
            }

            Win32Helper.GetCursorPos(out var pt);
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName) ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

            // Показ по клику на панель задач. Опрос (без глобальных хуков): если док скрыт,
            // курсор над прямоугольником таскбара (Shell_TrayWnd), этот таскбар на ТЕКУЩЕМ
            // экране (для мультимонитора) и в этот момент нажата левая кнопка — показываем док.
            // Клик держится дольше тика (100 мс), поэтому не теряется.
            if (_isHidden && (Win32Helper.GetAsyncKeyState(Win32Helper.VK_LBUTTON) & 0x8000) != 0)
            {
                IntPtr tray = Win32Helper.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero && Win32Helper.GetWindowRect(tray, out var tr))
                {
                    bool cursorOnTray = pt.X >= tr.Left && pt.X <= tr.Right && pt.Y >= tr.Top && pt.Y <= tr.Bottom;
                    bool trayOnThisScreen = tr.Right > screen.Bounds.Left && tr.Left < screen.Bounds.Right
                                          && tr.Bottom > screen.Bounds.Top && tr.Top < screen.Bounds.Bottom;
                    if (cursorOnTray && trayOnThisScreen)
                    {
                        ShowDock("taskbar-click");
                        return;
                    }
                }
            }

            double dpiX = 1.0, dpiY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null) { dpiX = source.CompositionTarget.TransformToDevice.M11; dpiY = source.CompositionTarget.TransformToDevice.M22; }

            int hoverZone = 4;
            bool isMouseNearEdge = false;

            switch (Settings.Position)
            {
                case DockPosition.Bottom: isMouseNearEdge = pt.Y >= (screen.Bounds.Bottom - hoverZone); break;
                case DockPosition.Top: isMouseNearEdge = pt.Y <= (screen.Bounds.Top + hoverZone); break;
                case DockPosition.Left: isMouseNearEdge = pt.X <= (screen.Bounds.Left + hoverZone); break;
                case DockPosition.Right: isMouseNearEdge = pt.X >= (screen.Bounds.Right - hoverZone); break;
            }

            // ВНЕШНИЙ ЗАПРЕТ: если активно внешнее подавление (напр. полноэкранное видео),
            // подавляем срабатывание вызова по краю — панель не выезжает по курсору.
            if (_isSuppressedExternally)
                isMouseNearEdge = false;

            // ============================================================
            //  ЛОГИКА ТРЁХ РЕЖИМОВ ВИДИМОСТИ
            // ============================================================
            //  AlwaysVisible — никогда не прячем (shouldHide = false).
            //  AutoHide      — всегда спрятана (shouldHide = true), выезжает
            //                  только по курсору у края (обрабатывается ниже).
            //  SmartHide     — прячем, если активное окно перекрывает панель
            //                  ИЛИ если в фокусе рабочий стол (доступ к ярлыкам).
            //                  Проверяем ТОЛЬКО активное окно (dodge active).
            bool shouldHide;
            switch (Settings.VisibilityMode)
            {
                case DockVisibilityMode.AlwaysVisible:
                    shouldHide = false;
                    break;

                case DockVisibilityMode.AutoHide:
                    shouldHide = true;
                    break;

                case DockVisibilityMode.SmartHide:
                default:
                    shouldHide = ShouldHideForSmartMode();
                    break;
            }

            _lastOverlappingState = shouldHide;

            if (_isHidden)
            {
                // РАНЬШЕ здесь скрытая панель ВЫДЁРГИВАЛАСЬ показанной при shouldHide=false.
                // Это давало самоподхват (панель стала foreground -> «не прятать» -> держится)
                // и показ на пустом рабочем столе/по клику, хотя там панель должна прятаться,
                // открывая доступ к ярлыкам. Теперь скрытую панель показывают только ЯВНЫЕ
                // триггеры (упор в край, клик по таскбару, меню/перетаскивание). Исключение —
                // режим AlwaysVisible: в нём панель обязана быть видимой всегда.
                if (Settings.VisibilityMode == DockVisibilityMode.AlwaysVisible)
                {
                    _edgePushAccumulator = 0;
                    _currentGlowProgress = 0;
                    UpdateGlow(0);
                    ShowDock("always-visible");
                }
                else if (isMouseNearEdge)
                {
                    if (_currentGlowProgress < 0.4)
                    {
                        _currentGlowProgress += 0.05;
                        if (_currentGlowProgress > 0.4) _currentGlowProgress = 0.4;
                        UpdateGlow(_currentGlowProgress);
                    }

                    if (_edgePushAccumulator > 0)
                    {
                        _edgePushAccumulator -= 15;
                        if (_edgePushAccumulator < 0) _edgePushAccumulator = 0;
                        _currentGlowProgress = 0.4 + (0.6 * Math.Clamp(_edgePushAccumulator / EdgePushThreshold, 0, 1.0));
                        UpdateGlow(_currentGlowProgress);
                    }

                }
                else
                {
                    if (_currentGlowProgress > 0)
                    {
                        _currentGlowProgress -= 0.05;
                        if (_currentGlowProgress < 0) _currentGlowProgress = 0;
                        UpdateGlow(_currentGlowProgress);
                    }
                    _edgePushAccumulator = 0;
                }
            }
            else
            {
                double viewboxW = MainViewbox?.ActualWidth ?? 0;
                double viewboxH = MainViewbox?.ActualHeight ?? 0;
                double visibleW = Math.Max(MainBorder.ActualWidth, viewboxW);
                double visibleH = Math.Max(MainBorder.ActualHeight, viewboxH);

                double dockLeft = _shownLeft + ZoomOverflow; double dockTop = _shownTop + ZoomOverflow;
                double dockRight = dockLeft + visibleW; double dockBottom = dockTop + visibleH;

                switch (Settings.Position)
                {
                    case DockPosition.Bottom: dockBottom += Settings.EdgeMargin + 20; dockTop -= 20; dockLeft -= 20; dockRight += 20; break;
                    case DockPosition.Top: dockTop -= (Settings.EdgeMargin + 20); dockBottom += 20; dockLeft -= 20; dockRight += 20; break;
                    case DockPosition.Left: dockLeft -= (Settings.EdgeMargin + 20); dockRight += 20; dockTop -= 20; dockBottom += 20; break;
                    case DockPosition.Right: dockRight += Settings.EdgeMargin + 20; dockLeft -= 20; dockTop -= 20; dockBottom += 20; break;
                }

                double mouseX = pt.X / dpiX; double mouseY = pt.Y / dpiY;
                bool isMouseOverDock = mouseX >= dockLeft && mouseX <= dockRight && mouseY >= dockTop && mouseY <= dockBottom;

                if (isMouseOverDock || isMouseNearEdge || !shouldHide)
                {
                    _visibleTimeMs = 0;
                }
                else
                {
                    _visibleTimeMs += AutoTimerIntervalMs;
                    if (_visibleTimeMs >= MinVisibleMs) HideDock();
                }
            }
        }

        private void HideDock()
        {
            if (_isHidden || _isTransitioning) return;
            _isHidden = true;
            PositionDock();
        }

        private void ShowDock(string reason = "?")
        {
            if (!_isHidden || _isTransitioning || _isForceHidden) return;

            // ВРЕМЕННАЯ ДИАГНОСТИКА: кто и в каком контексте показал док.
            Win32Helper.GetCursorPos(out var dpt);
            IntPtr fg = Win32Helper.GetForegroundWindow();
            string fgCls = fg != IntPtr.Zero ? Win32Helper.GetWindowClassName(fg) : "(null)";
            System.Diagnostics.Debug.WriteLine($"🟢 [SHOW-DIAG] reason={reason} cursor=({dpt.X},{dpt.Y}) fgClass='{fgCls}'");

            _isHidden = false;
            _visibleTimeMs = 0;
            PositionDock();
        }

        // ============================================================
        //  УМНОЕ СКРЫТИЕ (SmartHide) — чистая логика "dodge active".
        //  Прячем панель, если выполнено ХОТЯ БЫ ОДНО:
        //    1) активное (foreground) окно геометрически перекрывает панель;
        //    2) в фокусе рабочий стол (Progman/WorkerW) — для доступа к ярлыкам.
        //  Проверяется ТОЛЬКО активное окно. Неактивные окна под панелью
        //  игнорируются намеренно (пользователь работает в одном окне,
        //  а под панелью может лежать другое — панель остаётся доступной).
        // ============================================================
        private bool ShouldHideForSmartMode()
        {
            try
            {
                IntPtr foreground = Win32Helper.GetForegroundWindow();

                // Правило 2: фокус на рабочем столе -> прячем (доступ к ярлыкам).
                if (foreground != IntPtr.Zero)
                {
                    string className = Win32Helper.GetWindowClassName(foreground);
                    if (className == "Progman" || className == "WorkerW")
                        return true;

                    // Правило 1: активное окно перекрывает панель -> прячем.
                    // IsWindowOverlappingPanel уже корректно отсекает системные окна
                    // (включая Progman/WorkerW, трей и т.п.) и делает DPI-коррекцию.
                    if (IsWindowOverlappingPanel(foreground))
                        return true;
                }

                // Правило 3: на мониторе панели НЕТ ни одного видимого неминимизированного
                // окна (все свёрнуты по одному или скрыты) -> прячем, чтобы дать доступ
                // к ярлыкам рабочего стола без дополнительного клика по столу.
                // Проверка выполняется только в этой "пограничной" ветке (когда выше не
                // сработали правила 1-2), поэтому не нагружает каждый тик таймера.
                if (!HasAnyVisibleAppWindowOnPanelScreen())
                    return true;

                // Иначе — есть открытое окно, панель не перекрыта, стол не в фокусе: показываем.
                return false;
            }
            catch
            {
                // При любой ошибке безопаснее НЕ прятать (панель остаётся доступной).
                return false;
            }
        }

        // Лёгкая проверка: есть ли хотя бы одно "нормальное" видимое неминимизированное
        // окно приложения на том же мониторе, где находится панель.
        // Возвращает true, как только нашли ПЕРВОЕ такое окно (ранний выход — без полного прохода).
        private bool HasAnyVisibleAppWindowOnPanelScreen()
        {
            var panelScreen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName)
                              ?? System.Windows.Forms.Screen.PrimaryScreen;

            bool found = false;

            Win32Helper.EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    // Быстрые отсевы (от дешёвых к дорогим):
                    if (!Win32Helper.IsWindowVisible(hWnd)) return true;   // невидимо
                    if (Win32Helper.IsIconic(hWnd)) return true;            // свёрнуто
                    if (IsWindowCloaked(hWnd)) return true;                 // cloaked (UWP в фоне и т.п.)
                    if (hWnd == new WindowInteropHelper(this).Handle) return true; // сама панель

                    int exStyle = Win32Helper.GetWindowLong(hWnd, Win32Helper.GWL_EXSTYLE);
                    if ((exStyle & Win32Helper.WS_EX_TOOLWINDOW) != 0) return true; // служебное окно

                    string cls = Win32Helper.GetWindowClassName(hWnd);
                    if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" ||
                        cls == "TrayNotifyWnd" || cls == "Windows.UI.Core.CoreWindow" ||
                        cls == "XamlExplorerHostIslandWindow" || cls == "NotifyIconOverflowWindow")
                        return true;

                    // Окно должно иметь ненулевой размер.
                    if (!Win32Helper.GetWindowRect(hWnd, out var rc)) return true;
                    if (rc.Right - rc.Left <= 0 || rc.Bottom - rc.Top <= 0) return true;

                    // Окно должно находиться (центром) на мониторе панели.
                    if (panelScreen != null)
                    {
                        int cx = (rc.Left + rc.Right) / 2;
                        int cy = (rc.Top + rc.Bottom) / 2;
                        var b = panelScreen.Bounds;
                        bool onPanelScreen = cx >= b.Left && cx < b.Right && cy >= b.Top && cy < b.Bottom;
                        if (!onPanelScreen) return true;
                    }

                    // Нашли нормальное видимое окно — дальше можно не искать.
                    found = true;
                    return false; // прекращаем перечисление
                }
                catch { return true; }
            }, IntPtr.Zero);

            return found;
        }

        // ✅ ВСПОМОГАТЕЛЬНЫЙ МЕТОД: Проверка перекрытия одного окна с панелью
        private bool IsWindowOverlappingPanel(IntPtr hWnd)
        {
            try
            {
                if (hWnd == IntPtr.Zero || !Win32Helper.IsWindow(hWnd) || !Win32Helper.IsWindowVisible(hWnd) || Win32Helper.IsIconic(hWnd))
                    return false;

                // Исключаем системные окна
                string className = Win32Helper.GetWindowClassName(hWnd);
                bool isIgnoredSystemWindow = className == "LivePreviewSurface" ||
                                             className == "ThumbnailDeviceHelperWnd" ||
                                             className == "Shell_TrayWnd" ||
                                             className == "TrayNotifyWnd" ||
                                             className == "NotifyIconOverflowWindow" ||
                                             className == "TaskSwitchWnd" ||
                                             className == "MultitaskingViewFrame" ||
                                             className == "Progman" ||
                                             className == "WorkerW";

                if (isIgnoredSystemWindow)
                    return false;

                // Исключаем собственную панель
                if (hWnd == new WindowInteropHelper(this).Handle)
                    return false;

                // Проверка заголовка
                var sb = new System.Text.StringBuilder(256);
                Win32Helper.GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString() == "LivePreviewSurface")
                    return false;

                // Получаем координаты окна
                if (!Win32Helper.GetWindowRect(hWnd, out var windowRect))
                    return false;

                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null)
                    return false;

                double dpiX = source.CompositionTarget.TransformToDevice.M11;
                double dpiY = source.CompositionTarget.TransformToDevice.M22;

                double windowLeft = windowRect.Left / dpiX;
                double windowTop = windowRect.Top / dpiY;
                double windowRight = windowRect.Right / dpiX;
                double windowBottom = windowRect.Bottom / dpiY;

                // Получаем координаты области панели
                double viewboxW = MainViewbox?.ActualWidth ?? 0;
                double viewboxH = MainViewbox?.ActualHeight ?? 0;
                double visibleW = Math.Max(MainBorder.ActualWidth, viewboxW);
                double visibleH = Math.Max(MainBorder.ActualHeight, viewboxH);

                double panelLeft = (_shownLeft + ZoomOverflow) - 10;
                double panelTop = (_shownTop + ZoomOverflow) - 10;
                double panelRight = panelLeft + visibleW + 20;
                double panelBottom = panelTop + visibleH + 20;

                // Проверка пересечения
                bool isOverlapping = (windowLeft < panelRight && windowRight > panelLeft &&
                                     windowTop < panelBottom && windowBottom > panelTop);

                return isOverlapping;
            }
            catch (Exception ex)
            {
                //System.Diagnostics.Debug.WriteLine($"IsWindowOverlappingPanel error for {hWnd}: {ex.Message}");
                return false;
            }
        }

    }
}