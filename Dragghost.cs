using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PlankWin
{
    // Общий "призрак" перетаскивания: borderless topmost окно с иконкой под курсором.
    // Специфику (какая иконка, размер, dpi) передаёт вызывающий — расступание остаётся снаружи.
    public class DragGhost
    {
        private Window? _window;

        public bool IsVisible => _window != null;

        public void Show(ImageSource? icon, double size, bool shadow = true)
        {
            Close();
            var img = new Image { Source = icon, Stretch = Stretch.Uniform, Opacity = 0.9 };
            if (shadow)
                img.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Direction = 270, Opacity = 0.5, Color = Colors.Black };

            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                IsHitTestVisible = false,
                Width = size,
                Height = size,
                Content = img
            };
            _window.Show();

            // Сделать окно прозрачным для мыши (события проходят сквозь).
            try
            {
                var helper = new WindowInteropHelper(_window);
                helper.EnsureHandle();
                int ex = Win32Helper.GetWindowLong(helper.Handle, Win32Helper.GWL_EXSTYLE);
                Win32Helper.SetWindowLong(helper.Handle, Win32Helper.GWL_EXSTYLE, ex | Win32Helper.WS_EX_TRANSPARENT);
            }
            catch { }
        }

        // Центрирование под курсором (offset=null) или смещение от курсора (external drag).
        public void Update(double dpi, double? offsetX = null, double? offsetY = null)
        {
            if (_window == null) return;
            Win32Helper.GetCursorPos(out var pt);
            if (dpi <= 0) dpi = 1.0;
            if (offsetX.HasValue && offsetY.HasValue)
            {
                _window.Left = pt.X / dpi + offsetX.Value;
                _window.Top = pt.Y / dpi + offsetY.Value;
            }
            else
            {
                _window.Left = pt.X / dpi - _window.Width / 2;
                _window.Top = pt.Y / dpi - _window.Height / 2;
            }
        }

        public void Close()
        {
            if (_window != null) { try { _window.Close(); } catch { } _window = null; }
        }
    }
}