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

        public void ProcessRawMouseInput(int deltaX, int deltaY)
        {
            if (!_isHidden || _isTransitioning || _isForceHidden || !Settings.AutoHide) return;

            Win32Helper.GetCursorPos(out var pt);
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName) ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

            int edgeTolerance = 4;
            bool isPushing = false;

            switch (Settings.Position)
            {
                case DockPosition.Bottom: if (pt.Y >= screen.Bounds.Bottom - edgeTolerance && deltaY > 0) { _edgePushAccumulator += deltaY; isPushing = true; } break;
                case DockPosition.Top: if (pt.Y <= screen.Bounds.Top + edgeTolerance && deltaY < 0) { _edgePushAccumulator += Math.Abs(deltaY); isPushing = true; } break;
                case DockPosition.Left: if (pt.X <= screen.Bounds.Left + edgeTolerance && deltaX < 0) { _edgePushAccumulator += Math.Abs(deltaX); isPushing = true; } break;
                case DockPosition.Right: if (pt.X >= screen.Bounds.Right - edgeTolerance && deltaX > 0) { _edgePushAccumulator += deltaX; isPushing = true; } break;
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
                    ShowDock();
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
                if (_isHidden) { ShowDock(); }
                return;
            }

            Win32Helper.GetCursorPos(out var pt);
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _profileName) ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;

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

            // ✅ ГЛАВНОЕ ИСПРАВЛЕНИЕ: Используем улучшенную проверку связанных окон
            bool isOverlapping = false;
            if (Settings.AutoHide)
            {
                isOverlapping = IsAnyRelatedWindowOverlappingPanel();
            }

            _lastOverlappingState = isOverlapping;
            bool shouldHide = Settings.AutoHide && isOverlapping;

            if (_isHidden)
            {
                if (!shouldHide)
                {
                    _edgePushAccumulator = 0;
                    _currentGlowProgress = 0;
                    UpdateGlow(0);
                    ShowDock();
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

        private void ShowDock()
        {
            if (!_isHidden || _isTransitioning || _isForceHidden) return;
            _isHidden = false;
            _visibleTimeMs = 0;
            PositionDock();
        }

        // ✅ ГЛАВНЫЙ МЕТОД: Проверка связанных окон процесса
        private bool IsAnyRelatedWindowOverlappingPanel()
        {
            try
            {
                IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
                string windowTitle = "";
                string className = "";

                // Получаем информацию об активном окне для диагностики
                var sb = new System.Text.StringBuilder(256);
                Win32Helper.GetWindowText(foregroundWindow, sb, sb.Capacity);
                windowTitle = sb.ToString();
                className = Win32Helper.GetWindowClassName(foregroundWindow);

                //System.Diagnostics.Debug.WriteLine($"🔍 [AUTO-HIDE] Checking foreground window: '{windowTitle}' (class: {className}, handle: {foregroundWindow})");

                // 1. Проверяем основное окно
                bool foregroundOverlaps = IsWindowOverlappingPanel(foregroundWindow);
                //System.Diagnostics.Debug.WriteLine($"🔍 [AUTO-HIDE] Foreground window overlaps: {foregroundOverlaps}");

                if (foregroundOverlaps)
                {
                    //System.Diagnostics.Debug.WriteLine($"✅ [AUTO-HIDE] Foreground window overlaps panel - returning TRUE");
                    return true;
                }

                // 2. УЛУЧШЕННАЯ ЛОГИКА: Более умная проверка связанных окон
                string foregroundProcessPath = GetProcessExecutablePath(foregroundWindow);
                //System.Diagnostics.Debug.WriteLine($"🔍 [AUTO-HIDE] Process path: {foregroundProcessPath}");

                if (!string.IsNullOrEmpty(foregroundProcessPath))
                {
                    // Проверяем тип процесса для специальной обработки
                    bool isUwpApp = IsUwpApplication(foregroundProcessPath);
                    //System.Diagnostics.Debug.WriteLine($"🔍 [AUTO-HIDE] Is UWP app: {isUwpApp}");

                    // Для UWP приложений - более строгий подход
                    if (isUwpApp)
                    {
                        //System.Diagnostics.Debug.WriteLine($"📱 [AUTO-HIDE] UWP app detected - using simplified logic");

                        // У UWP только проверяем foreground окно и его прямого owner
                        IntPtr uwpOwner = Win32Helper.GetWindow(foregroundWindow, Win32Helper.GW_OWNER);
                        //System.Diagnostics.Debug.WriteLine($"🔍 [AUTO-HIDE] UWP owner window: {uwpOwner}");

                        if (uwpOwner != IntPtr.Zero && IsWindowOverlappingPanel(uwpOwner))
                        {
                            System.Diagnostics.Debug.WriteLine($"✅ [AUTO-HIDE] UWP owner overlaps - returning TRUE");
                            return true;
                        }

                        // Для UWP не сканируем все окна процесса - слишком много служебных окон
                        //System.Diagnostics.Debug.WriteLine($"❌ [AUTO-HIDE] UWP app - no overlap found, returning FALSE");
                        return false;
                    }

                    System.Diagnostics.Debug.WriteLine($"🖥️ [AUTO-HIDE] Win32 app detected - using full scan logic");

                    // Для обычных Win32 приложений - полное сканирование связанных окон
                    var relatedWindows = new List<IntPtr>();
                    int mainWindowCount = 0;

                    Win32Helper.EnumWindows((hWnd, lParam) =>
                    {
                        try
                        {
                            if (Win32Helper.IsWindowVisible(hWnd))
                            {
                                string windowProcessPath = GetProcessExecutablePath(hWnd);
                                if (windowProcessPath == foregroundProcessPath)
                                {
                                    string winClassName = Win32Helper.GetWindowClassName(hWnd);

                                    // Считаем только значимые окна (не служебные)
                                    if (!IsSystemOrServiceWindow(winClassName))
                                    {
                                        relatedWindows.Add(hWnd);

                                        // Проверяем является ли окно главным
                                        if (IsMainWindow(hWnd))
                                        {
                                            mainWindowCount++;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        return true;
                    }, IntPtr.Zero);

                    //System.Diagnostics.Debug.WriteLine($"🔍 [AUTO-HIDE] Found {relatedWindows.Count} related windows ({mainWindowCount} main windows)");

                    // Проверяем только значимые окна
                    foreach (var hwnd in relatedWindows)
                    {
                        if (IsWindowOverlappingPanel(hwnd))
                        {
                            //System.Diagnostics.Debug.WriteLine($"✅ [AUTO-HIDE] Related window {hwnd} overlaps - returning TRUE");
                            return true;
                        }
                    }
                }

                // 3. ДОПОЛНИТЕЛЬНО: Проверяем owner окно (для popup меню)
                IntPtr owner = Win32Helper.GetWindow(foregroundWindow, Win32Helper.GW_OWNER);
                System.Diagnostics.Debug.WriteLine($"🔍 [AUTO-HIDE] General owner check: {owner}");

                if (owner != IntPtr.Zero && IsWindowOverlappingPanel(owner))
                {
                    //System.Diagnostics.Debug.WriteLine($"✅ [AUTO-HIDE] Owner window overlaps - returning TRUE");
                    return true;
                }

                //System.Diagnostics.Debug.WriteLine($"❌ [AUTO-HIDE] No overlapping windows found - returning FALSE");
                return false;
            }
            catch (Exception ex)
            {
                //System.Diagnostics.Debug.WriteLine($"❗ [AUTO-HIDE] ERROR in IsAnyRelatedWindowOverlappingPanel: {ex.Message}");
                return false;
            }
        }

        // ✅ ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        private bool IsUwpApplication(string processPath)
        {
            return processPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                   processPath.Contains("Calculator", StringComparison.OrdinalIgnoreCase) ||
                   processPath.Contains("SystemSettings", StringComparison.OrdinalIgnoreCase) ||
                   processPath.Contains("ClockAndCalendar", StringComparison.OrdinalIgnoreCase) ||
                   processPath.Contains("Microsoft.", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSystemOrServiceWindow(string className)
        {
            return className == "ApplicationManager_DesktopShellWindow" ||
                   className == "WorkerW" ||
                   className == "Progman" ||
                   className == "DV2ControlHost" ||
                   className == "ForegroundStaging" ||
                   className == "ApplicationManager_ImmersiveShellWindow" ||
                   className.StartsWith("Windows.UI.Core") ||
                   className.Contains("InputMethod") ||
                   className.Contains("IME");
        }

        private bool IsMainWindow(IntPtr hWnd)
        {
            // Простая эвристика для определения главного окна
            Win32Helper.GetWindowRect(hWnd, out var rect);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            // Главные окна обычно больше чем popup/service окна
            return width > 100 && height > 100;
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

        // ✅ БЫСТРЫЙ МЕТОД: Простое получение пути процесса для auto-hide (без UWP обработки)
        private string GetProcessPathForAutoHide(IntPtr hWnd)
        {
            try
            {
                if (hWnd == IntPtr.Zero)
                    return string.Empty;

                Win32Helper.GetWindowThreadProcessId(hWnd, out uint processId);
                if (processId == 0)
                    return string.Empty;

                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}