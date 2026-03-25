using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PlankWin
{
    internal static class Win32Helper
    {
        // ==========================================
        // БАЗОВЫЕ ФУНКЦИИ УПРАВЛЕНИЯ ОКНАМИ
        // ==========================================

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static string GetWindowClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            int len = GetClassName(hWnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Win32Point pt);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

        // ==========================================
        // ПЕРЕБОР ОКОН (ENUMERATION)
        // ==========================================
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        // ==========================================
        // ПРОЦЕССЫ И ПАМЯТЬ
        // ==========================================
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint appUserModelIdLength, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder sbAppUserModelId);

        // ==========================================
        // ХУКИ И ВВОД (HOOKS & INPUT)
        // ==========================================
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterShellHookWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DeregisterShellHookWindow(IntPtr hWnd);

        // ==========================================
        // DWM (ОТРИСОВКА WINDOWS)
        // ==========================================
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        // ==========================================
        // GDI & ИКОНКИ
        // ==========================================
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
        public static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetClassLong")]
        public static extern IntPtr GetClassLong32(IntPtr hWnd, int nIndex);

        public static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8) return GetClassLongPtr64(hWnd, nIndex);
            return GetClassLong32(hWnd, nIndex);
        }

        // ==========================================
        // SHELL (ПАНЕЛЬ ЗАДАЧ, ПРОВОДНИК, ЯРЛЫКИ)
        // ==========================================
        [DllImport("shell32.dll")]
        public static extern int SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHCreateItemFromParsingName([In, MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(IntPtr pIDL, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore propertyStore);

        [DllImport("ole32.dll")]
        public static extern void PropVariantClear(ref PROPVARIANT pvar);

        // ==========================================
        // СТРУКТУРЫ И КОНСТАНТЫ
        // ==========================================
        public const int SW_RESTORE = 9;
        public const int SW_MINIMIZE = 6;
        public const uint WM_CLOSE = 0x0010;
        public const int GWL_EXSTYLE = -20;
        public const int GWL_STYLE = -16;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const uint WS_EX_APPWINDOW = 0x00040000;
        public const uint GW_OWNER = 4;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int DWMWA_CLOAKED = 14;
        public const int ABM_NEW = 0x0000;
        public const int ABM_REMOVE = 0x0001;
        public const int ABM_QUERYPOS = 0x0002;
        public const int ABM_SETPOS = 0x0003;
        public const int ABE_LEFT = 0;
        public const int ABE_TOP = 1;
        public const int ABE_RIGHT = 2;
        public const int ABE_BOTTOM = 3;
        public const uint WM_GETICON = 0x007F;
        public const int ICON_BIG = 1;
        public const int ICON_SMALL2 = 2;
        public const int ICON_SMALL = 0;
        public const int GCLP_HICON = -14;
        public const int GCLP_HICONSM = -34;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public const uint SHGFI_ICON = 0x100;
        public const uint SHGFI_LARGEICON = 0x0;
        public const uint SHGFI_PIDL = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        public struct Win32Point { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO { public Win32Point ptReserved; public Win32Point ptMaxSize; public Win32Point ptMaxPosition; public Win32Point ptMinTrackSize; public Win32Point ptMaxTrackSize; }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT { public int length; public int flags; public int showCmd; public Win32Point ptMinPosition; public Win32Point ptMaxPosition; public RECT rcNormalPosition; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int uCallbackMessage; public int uEdge; public RECT rc; public IntPtr lParam; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; } }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit)]
        public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr pwszVal; }

        [Flags]
        public enum SIIGBF { SIIGBF_RESIZETOFIT = 0x00, SIIGBF_BIGGERSIZEOK = 0x01, SIIGBF_MEMORYONLY = 0x02, SIIGBF_ICONONLY = 0x04, SIIGBF_THUMBNAILONLY = 0x08, SIIGBF_INCACHEONLY = 0x10 }

        // ==========================================
        // COM ИНТЕРФЕЙСЫ
        // ==========================================
        [ComImport, Guid("bcc18b79-ba16-442f-8a36-ce52a96bffa6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItemImageFactory { [PreserveSig] int GetImage([In] SIZE size, [In] SIIGBF flags, out IntPtr phbm); }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore { [PreserveSig] int GetCount(out uint cProps); [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey); [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv); [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar); [PreserveSig] int Commit(); }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        public class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        public interface IShellLink { void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags); void GetIDList(out IntPtr ppidl); void SetIDList(IntPtr pidl); void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName); void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir); void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs); void GetHotkey(out short pwHotkey); void SetHotkey(short wHotkey); void GetShowCmd(out int piShowCmd); void SetShowCmd(int iShowCmd); void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon); void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon); void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved); void Resolve(IntPtr hwnd, int fFlags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile); }

        public static string ResolveShortcut(string shortcutPath, out string arguments)
        {
            arguments = "";
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                arguments = shortcut.Arguments;
                return shortcut.TargetPath;
            }
            catch { return shortcutPath; }
        }

        // ==========================================
        // RAW INPUT (СЫРОЙ ВВОД МЫШИ ДЛЯ "НАДАВЛИВАНИЯ")
        // ==========================================
        public const int WM_INPUT = 0x00FF;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RID_INPUT = 0x10000003;
        public const int RIM_TYPEMOUSE = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct RAWMOUSE
        {
            [FieldOffset(0)] public ushort usFlags;
            [FieldOffset(4)] public uint ulButtons;
            [FieldOffset(4)] public ushort usButtonFlags;
            [FieldOffset(6)] public ushort usButtonData;
            [FieldOffset(8)] public uint ulRawButtons;
            [FieldOffset(12)] public int lLastX; // Сдвиг по X напрямую с сенсора!
            [FieldOffset(16)] public int lLastY; // Сдвиг по Y напрямую с сенсора!
            [FieldOffset(20)] public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct RAWINPUT
        {
            [FieldOffset(0)] public RAWINPUTHEADER header;
            [FieldOffset(24)] public RAWMOUSE mouse; // Смещение 24 строго для 64-битных систем
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, out RAWINPUT pData, ref uint pcbSize, uint cbSizeHeader);

        // ==========================================
        // UWP APPLICATION USER MODEL ID SUPPORT  
        // ==========================================

        //[DllImport("user32.dll", CharSet = CharSet.Unicode)]
        //public static extern int GetWindowApplicationUserModelId(
        //    IntPtr hwnd,
        //    ref uint applicationUserModelIdLength,
        //    IntPtr applicationUserModelId);

        //[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        //public static extern int GetProcessApplicationUserModelId(
        //    IntPtr hProcess,
        //    ref uint applicationUserModelIdLength,
        //    IntPtr applicationUserModelId);
    }
}