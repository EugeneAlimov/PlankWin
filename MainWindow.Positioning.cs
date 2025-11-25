using System;
using System.Windows;

namespace PlankWin
{
    public partial class MainWindow
    {
        private void PositionDock()
        {
            var workArea = SystemParameters.WorkArea;

            switch (_settings.Position)
            {
                case DockPosition.Top:
                    Width = workArea.Width;
                    Height = _settings.Height;
                    _shownLeft = workArea.Left;
                    _shownTop = workArea.Top;
                    break;

                case DockPosition.Left:
                    Width = _settings.Width;
                    Height = workArea.Height;
                    _shownLeft = workArea.Left;
                    _shownTop = workArea.Top;
                    break;

                case DockPosition.Right:
                    Width = _settings.Width;
                    Height = workArea.Height;
                    _shownLeft = workArea.Right - Width;
                    _shownTop = workArea.Top;
                    break;

                case DockPosition.Bottom:
                default:
                    Width = workArea.Width;
                    Height = _settings.Height;
                    _shownLeft = workArea.Left;
                    _shownTop = workArea.Bottom - Height;
                    break;
            }

            Left = _shownLeft;
            Top = _shownTop;

            ComputeHiddenPosition();
        }

        private void ComputeHiddenPosition()
        {
            var workArea = SystemParameters.WorkArea;

            switch (_settings.Position)
            {
                case DockPosition.Bottom:
                    _hiddenLeft = _shownLeft;
                    _hiddenTop = workArea.Bottom;          // ниже экрана
                    break;

                case DockPosition.Top:
                    _hiddenLeft = _shownLeft;
                    _hiddenTop = workArea.Top - Height;    // выше экрана
                    break;

                case DockPosition.Left:
                    _hiddenLeft = workArea.Left - Width;   // левее экрана
                    _hiddenTop = _shownTop;
                    break;

                case DockPosition.Right:
                    _hiddenLeft = workArea.Right;          // правее экрана
                    _hiddenTop = _shownTop;
                    break;

                default:
                    _hiddenLeft = _shownLeft;
                    _hiddenTop = _shownTop;
                    break;
            }
        }

        private void ApplyItemsOrientation()
        {
            if (_settings.Position == DockPosition.Left ||
                _settings.Position == DockPosition.Right)
            {
                var verticalTemplate = TryFindResource("VerticalPanel") as System.Windows.Controls.ItemsPanelTemplate;
                if (verticalTemplate != null)
                {
                    AppsItemsControl.ItemsPanel = verticalTemplate;
                }
            }
        }
    }
}
