using System;

namespace PlankWin
{
    public partial class MainWindow
    {
        private static bool IsWindowCloaked(IntPtr hWnd)
        {
            try
            {
                int cloaked;
                int hr = PlankWin.Win32Helper.DwmGetWindowAttribute(hWnd, PlankWin.Win32Helper.DWMWA_CLOAKED, out cloaked, sizeof(int));
                return hr == 0 && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}