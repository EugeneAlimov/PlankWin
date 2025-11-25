using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PlankWin;

public class AppIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DockAppConfig app)
            return DependencyProperty.UnsetValue;

        // -------- 1. Путь к реальному файлу (exe/dll), если он вообще есть --------
        string? resolvedFile = ResolveExistingPath(app.Path);

        // 1.1. Пробуем ExtractIcon по exe
        if (!string.IsNullOrWhiteSpace(resolvedFile))
        {
            var fromExe = TryExtractIcon(resolvedFile!);
            if (fromExe != null)
                return fromExe;
        }

        // -------- 2. Пользовательская иконка из конфигурации (icons/*.png и т.п.) --------
        if (!string.IsNullOrWhiteSpace(app.Icon))
        {
            var iconPath = app.Icon!;
            if (!Path.IsPathRooted(iconPath))
                iconPath = Path.Combine(AppContext.BaseDirectory, iconPath);

            if (File.Exists(iconPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(iconPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch
                {
                    // если тут упало — идём дальше
                }
            }
        }

        // -------- 3. Иконка самого окна (для динамических приложений, Settings и т.п.) --------
        if (app.WindowHandle != IntPtr.Zero)
        {
            var fromWindow = GetWindowIcon(app.WindowHandle);
            if (fromWindow != null)
                return fromWindow;
        }

        // -------- 4. Спец-хак для Paint (UWP-версия) --------
        if (IsPaintApp(app))
        {
            var fromUwp = TryGetUwpPaintIcon();
            if (fromUwp != null)
                return fromUwp;
        }

        // -------- 5. Спец-хак для Settings --------
        if (IsSettingsApp(app))
        {
            var fromSettings = TryGetSettingsIcon();
            if (fromSettings != null)
                return fromSettings;
        }

        // -------- 6. Системная иконка по файлу / типу --------
        if (!string.IsNullOrWhiteSpace(app.Path))
        {
            // если мы уже нашли реальный файл — используем его
            if (resolvedFile != null)
            {
                var bsReal = GetFileIcon(resolvedFile, fileExists: true);
                if (bsReal != null)
                    return bsReal;
            }

            // не нашли реальный файл — пробуем по "типу" (расширению или алиасу)
            var bsByType = GetFileIcon(app.Path, fileExists: false);
            if (bsByType != null)
                return bsByType;
        }

        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    // ---------- Вспомогательные методы ----------

    /// <summary>
    /// Пробуем разрулить путь к реальному файлу:
    /// - абсолютный путь
    /// - рядом с PlankWin.exe
    /// - в системной директории (System32)
    /// </summary>
    private static string? ResolveExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            if (Path.IsPathRooted(path))
            {
                if (File.Exists(path))
                    return path;
            }
            else
            {
                // 1) рядом с PlankWin.exe
                var candidate = Path.Combine(AppContext.BaseDirectory, path);
                if (File.Exists(candidate))
                    return candidate;

                // 2) System32
                var sysDir = Environment.SystemDirectory;
                var sysCandidate = Path.Combine(sysDir, path);
                if (File.Exists(sysCandidate))
                    return sysCandidate;
            }
        }
        catch
        {
            // игнорируем
        }

        return null;
    }

    /// <summary>
    /// Определяем, что это Paint (по имени процесса, имени exe или имени приложения).
    /// </summary>
    private static bool IsPaintApp(DockAppConfig app)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(app.ProcessName) &&
                app.ProcessName.Equals("mspaint", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(app.Path))
            {
                var fn = Path.GetFileNameWithoutExtension(app.Path);
                if (!string.IsNullOrWhiteSpace(fn) &&
                    fn.Equals("mspaint", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (!string.IsNullOrWhiteSpace(app.Name) &&
                app.Name.IndexOf("paint", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Определяем, что это Settings (по имени процесса, exe или имени приложения).
    /// </summary>
    private static bool IsSettingsApp(DockAppConfig app)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(app.ProcessName))
            {
                var lower = app.ProcessName.ToLowerInvariant();
                if (lower.Contains("systemsettings") || lower.Contains("immersivecontrolpanel"))
                    return true;
            }

            if (!string.IsNullOrWhiteSpace(app.Path))
            {
                var fn = Path.GetFileNameWithoutExtension(app.Path);
                if (!string.IsNullOrWhiteSpace(fn))
                {
                    var lowerFn = fn.ToLowerInvariant();
                    if (lowerFn.Contains("systemsettings") || lowerFn.Contains("immersivecontrolpanel"))
                        return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(app.Name) &&
                app.Name.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Пробуем вытащить иконку UWP Paint по алиасу в %LocalAppData%\Microsoft\WindowsApps.
    /// </summary>
    private static BitmapSource? TryGetUwpPaintIcon()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var aliasPath = Path.Combine(
                localAppData,
                "Microsoft",
                "WindowsApps",
                "Microsoft.Paint_8wekyb3d8bbwe");

            if (!File.Exists(aliasPath))
                return null;

            // Сначала пробуем ExtractIcon
            var fromExe = TryExtractIcon(aliasPath);
            if (fromExe != null)
                return fromExe;

            // Если не получилось — пробуем SHGetFileInfo
            var fromFile = GetFileIcon(aliasPath, fileExists: true);
            return fromFile;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Пробуем вытащить иконку Settings из SystemSettings.exe в ImmersiveControlPanel.
    /// </summary>
    private static BitmapSource? TryGetSettingsIcon()
    {
        try
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var settingsExe = Path.Combine(winDir, "ImmersiveControlPanel", "SystemSettings.exe");

            if (!File.Exists(settingsExe))
                return null;

            var fromExe = TryExtractIcon(settingsExe);
            if (fromExe != null)
                return fromExe;

            var fromFile = GetFileIcon(settingsExe, fileExists: true);
            return fromFile;
        }
        catch
        {
            return null;
        }
    }

    // --- ExtractIcon ---

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszFile, int nIconIndex);

    /// <summary>
    /// Более надёжное извлечение иконки из exe через ExtractIcon.
    /// </summary>
    private static BitmapSource? TryExtractIcon(string exePath)
    {
        try
        {
            IntPtr hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);
            // Возвращаемое значение 1 — "нет иконки"
            if (hIcon != IntPtr.Zero && hIcon != new IntPtr(1))
            {
                try
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(
                        hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    // --- Иконка окна по hWnd ---

    private const uint WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL = 0;
    private const int GCL_HICON = -14;
    private const int GCL_HICONSM = -34;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr", SetLastError = true)]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLong", SetLastError = true)]
    private static extern IntPtr GetClassLongPtr32(IntPtr hWnd, int nIndex);

    private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8)
            return GetClassLongPtr64(hWnd, nIndex);
        return GetClassLongPtr32(hWnd, nIndex);
    }

    private static BitmapSource? GetWindowIcon(IntPtr hWnd)
    {
        try
        {
            IntPtr hIcon = SendMessage(hWnd, WM_GETICON, new IntPtr(ICON_BIG), IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hWnd, WM_GETICON, new IntPtr(ICON_SMALL), IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassLongPtr(hWnd, GCL_HICON);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassLongPtr(hWnd, GCL_HICONSM);

            if (hIcon == IntPtr.Zero)
                return null;

            try
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    // --- SHGetFileInfo ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        out SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_ICONLOCATION = 0x000001000;
    private const uint SHGFI_SHELLICONSIZE = 0x000000004;

    /// <summary>
    /// Получает иконку файла:
    ///  - если fileExists = true, SHGetFileInfo грузит иконку из реального файла;
    ///  - если fileExists = false, используем SHGFI_USEFILEATTRIBUTES по расширению/типу.
    /// </summary>
    private static BitmapSource? GetFileIcon(string path, bool fileExists)
    {
        var shfi = new SHFILEINFO();

        uint flags = SHGFI_ICON | SHGFI_LARGEICON;
        uint attrs = 0;

        if (!fileExists)
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
            attrs = FILE_ATTRIBUTE_NORMAL;
        }

        var res = SHGetFileInfo(
            path,
            attrs,
            out shfi,
            (uint)Marshal.SizeOf(shfi),
            flags);

        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }
}
