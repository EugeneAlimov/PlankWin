using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Linq;
using System;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Media;

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

            bool isOverlapping = _lastOverlappingState;

            if (Settings.AutoHide)
            {
                IntPtr fgHwnd = Win32Helper.GetForegroundWindow();
                if (fgHwnd != IntPtr.Zero && fgHwnd != new WindowInteropHelper(this).Handle)
                {
                    string className = Win32Helper.GetWindowClassName(fgHwnd);

                    bool isIgnoredSystemWindow = className == "LivePreviewSurface" ||
                                                 className == "ThumbnailDeviceHelperWnd" ||
                                                 className == "Shell_TrayWnd" ||
                                                 className == "TrayNotifyWnd" ||
                                                 className == "NotifyIconOverflowWindow" ||
                                                 className == "TaskSwitchWnd" ||
                                                 className == "MultitaskingViewFrame";

                    if (!isIgnoredSystemWindow)
                    {
                        var sb = new System.Text.StringBuilder(256);
                        Win32Helper.GetWindowText(fgHwnd, sb, sb.Capacity);

                        if (sb.ToString() != "LivePreviewSurface")
                        {
                            if (className == "Progman" || className == "WorkerW")
                            {
                                isOverlapping = false;
                            }
                            else if (Win32Helper.IsWindowVisible(fgHwnd) && !Win32Helper.IsIconic(fgHwnd))
                            {
                                Win32Helper.GetWindowRect(fgHwnd, out var fgRect);
                                double fgLeft = fgRect.Left / dpiX; double fgTop = fgRect.Top / dpiY; double fgRight = fgRect.Right / dpiX; double fgBottom = fgRect.Bottom / dpiY;

                                double viewboxW = MainViewbox?.ActualWidth ?? 0; double viewboxH = MainViewbox?.ActualHeight ?? 0;
                                double visibleW = Math.Max(MainBorder.ActualWidth, viewboxW); double visibleH = Math.Max(MainBorder.ActualHeight, viewboxH);

                                double overlapL = (_shownLeft + ZoomOverflow) - 10; double overlapT = (_shownTop + ZoomOverflow) - 10;
                                double overlapR = overlapL + visibleW + 20; double overlapB = overlapT + visibleH + 20;

                                isOverlapping = (fgLeft < overlapR && fgRight > overlapL && fgTop < overlapB && fgBottom > overlapT);
                            }
                        }
                    }
                }
                else if (fgHwnd == new WindowInteropHelper(this).Handle)
                {
                    isOverlapping = false;
                }
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
    }
}