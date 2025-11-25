using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PlankWin
{
    public partial class MainWindow
    {
        private void SetupAutoHide()
        {
            if (!_settings.AutoHide)
                return;

            _autoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AutoTimerIntervalMs)
            };
            _autoHideTimer.Tick += AutoHideTimer_Tick;
            _autoHideTimer.Start();
        }

        private IntPtr GetEffectiveForegroundWindow()
        {
            IntPtr raw = GetForegroundWindow();
            var thisHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            if (raw != IntPtr.Zero && raw != thisHwnd && !IsShellLikeWindow(raw))
            {
                _lastRealForeground = raw;
            }

            if (_lastRealForeground != IntPtr.Zero)
                return _lastRealForeground;

            return raw;
        }

        private bool IsShellLikeWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
                return true;

            string className = GetWindowClassName(hWnd);
            return className == "Progman"
                   || className == "WorkerW"
                   || className == "Shell_TrayWnd"
                   || className == "Shell_SecondaryTrayWnd";
        }

        private void AutoHideTimer_Tick(object? sender, EventArgs e)
        {
            if (!GetCursorPos(out POINT pt))
                return;

            var cursorX = pt.X;
            var cursorY = pt.Y;
            var workArea = SystemParameters.WorkArea;

            if (_isHidden)
                _visibleTimeMs = 0;
            else
                _visibleTimeMs += AutoTimerIntervalMs;

            int waLeft = (int)Math.Round(workArea.Left);
            int waTop = (int)Math.Round(workArea.Top);
            int waRight = (int)Math.Round(workArea.Right);
            int waBottom = (int)Math.Round(workArea.Bottom);
            int pressMargin = 1;

            IntPtr fg = GetEffectiveForegroundWindow();
            bool windowBlocksDock = WindowBlocksDock(fg);

            bool cursorOverDock = false;
            if (!_isHidden)
            {
                double winLeft = Left;
                double winTop = Top;
                double winRight = winLeft + Width;
                double winBottom = winTop + Height;

                cursorOverDock =
                    cursorX >= winLeft &&
                    cursorX <= winRight &&
                    cursorY >= winTop &&
                    cursorY <= winBottom;
            }

            if (windowBlocksDock)
            {
                if (_isHidden)
                {
                    bool atEdgeForPress = _settings.Position switch
                    {
                        DockPosition.Bottom => Math.Abs(cursorY - waBottom) <= pressMargin,
                        DockPosition.Top => Math.Abs(cursorY - waTop) <= pressMargin,
                        DockPosition.Left => Math.Abs(cursorX - waLeft) <= pressMargin,
                        DockPosition.Right => Math.Abs(cursorX - waRight) <= pressMargin,
                        _ => false
                    };

                    bool pressingNow = false;
                    if (atEdgeForPress && _hasLastCursor)
                    {
                        switch (_settings.Position)
                        {
                            case DockPosition.Bottom:
                                pressingNow = cursorY >= _lastCursorY;
                                break;
                            case DockPosition.Top:
                                pressingNow = cursorY <= _lastCursorY;
                                break;
                            case DockPosition.Left:
                                pressingNow = cursorX <= _lastCursorX;
                                break;
                            case DockPosition.Right:
                                pressingNow = cursorX >= _lastCursorX;
                                break;
                        }
                    }

                    if (pressingNow || (atEdgeForPress && _edgeHoverMs > 0))
                    {
                        _edgeHoverMs += AutoTimerIntervalMs;

                        if (_edgeHoverMs >= EdgePressMs)
                        {
                            _edgeHoverMs = 0;
                            ShowDock();
                        }
                    }
                    else
                    {
                        _edgeHoverMs = 0;
                    }
                }
                else
                {
                    if (!cursorOverDock && _visibleTimeMs >= MinVisibleMs)
                    {
                        HideDock();
                    }

                    _edgeHoverMs = 0;
                }
            }
            else
            {
                if (_isHidden)
                {
                    ShowDock();
                }

                _edgeHoverMs = 0;
            }

            _lastCursorX = cursorX;
            _lastCursorY = cursorY;
            _hasLastCursor = true;
        }

        private bool WindowBlocksDock(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
                return false;

            var thisHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hWnd == thisHwnd)
                return false;

            if (IsShellLikeWindow(hWnd))
                return false;

            var placement = new WINDOWPLACEMENT();
            placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));

            if (!GetWindowPlacement(hWnd, ref placement))
                return false;

            const int SW_SHOWMINIMIZED = 2;
            const int SW_SHOWMAXIMIZED = 3;

            if (placement.showCmd == SW_SHOWMINIMIZED)
                return false;

            if (placement.showCmd == SW_SHOWMAXIMIZED)
                return true;

            if (!GetWindowRect(hWnd, out RECT rect))
                return false;

            double dockLeft = _shownLeft;
            double dockTop = _shownTop;
            double dockRight = dockLeft + Width;
            double dockBottom = dockTop + Height;

            bool intersects =
                rect.Right > dockLeft &&
                rect.Left < dockRight &&
                rect.Bottom > dockTop &&
                rect.Top < dockBottom;

            return intersects;
        }

        private void HideDock()
        {
            if (_isHidden)
                return;

            _isHidden = true;

            var animLeft = new DoubleAnimation
            {
                To = _hiddenLeft,
                Duration = TimeSpan.FromMilliseconds(AnimationDurationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            var animTop = new DoubleAnimation
            {
                To = _hiddenTop,
                Duration = TimeSpan.FromMilliseconds(AnimationDurationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            BeginAnimation(Window.LeftProperty, animLeft);
            BeginAnimation(Window.TopProperty, animTop);
        }

        private void ShowDock()
        {
            if (!_isHidden)
                return;

            _isHidden = false;
            _visibleTimeMs = 0;

            PositionDock();

            Left = _hiddenLeft;
            Top = _hiddenTop;

            var animLeft = new DoubleAnimation
            {
                To = _shownLeft,
                Duration = TimeSpan.FromMilliseconds(AnimationDurationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            var animTop = new DoubleAnimation
            {
                To = _shownTop,
                Duration = TimeSpan.FromMilliseconds(AnimationDurationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            BeginAnimation(Window.LeftProperty, animLeft);
            BeginAnimation(Window.TopProperty, animTop);

            Topmost = true;
        }
    }
}
