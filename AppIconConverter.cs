using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PlankWin
{
    public class AppIconConverter : IValueConverter
    {
        // ДОБАВЛЯЕМ только кэш для избежания заглушек
        private static readonly Dictionary<string, BitmapSource> _lastValidIconCache = new();

        private static bool IsUwpWrapper(IntPtr hwnd)
        {
            string className = Win32Helper.GetWindowClassName(hwnd);
            return className == "ApplicationFrameWindow" || className == "Windows.UI.Core.CoreWindow";
        }

        private static IntPtr GetRealUwpWindow(IntPtr hwnd)
        {
            string className = Win32Helper.GetWindowClassName(hwnd);
            if (className == "ApplicationFrameWindow")
            {
                Win32Helper.GetWindowThreadProcessId(hwnd, out uint parentPid);
                IntPtr realCoreWindow = IntPtr.Zero;
                Win32Helper.EnumChildWindows(hwnd, (hChild, lParam) =>
                {
                    Win32Helper.GetWindowThreadProcessId(hChild, out uint childPid);
                    if (childPid != parentPid) { realCoreWindow = hChild; return false; }
                    return true;
                }, IntPtr.Zero);
                if (realCoreWindow != IntPtr.Zero) return realCoreWindow;
            }
            return hwnd;
        }

        private static IntPtr GetLiveIcon(IntPtr hwnd)
        {
            IntPtr result;
            Win32Helper.SendMessageTimeout(hwnd, Win32Helper.WM_GETICON, new IntPtr(Win32Helper.ICON_BIG), IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out result);
            if (result != IntPtr.Zero) return result;
            Win32Helper.SendMessageTimeout(hwnd, Win32Helper.WM_GETICON, new IntPtr(Win32Helper.ICON_SMALL2), IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out result);
            if (result != IntPtr.Zero) return result;
            return Win32Helper.GetClassLongPtr(hwnd, Win32Helper.GCLP_HICON);
        }

        private static string GetProcessExecutablePath(IntPtr hwnd)
        {
            Win32Helper.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return string.Empty;
            IntPtr hProcess = Win32Helper.OpenProcess(0x1000, false, pid);
            if (hProcess == IntPtr.Zero) return string.Empty;
            try
            {
                int capacity = 1024;
                var sb = new System.Text.StringBuilder(capacity);
                if (Win32Helper.QueryFullProcessImageName(hProcess, 0, sb, ref capacity)) return sb.ToString();
            }
            finally { Win32Helper.CloseHandle(hProcess); }
            return string.Empty;
        }

        private static string GetAumidFromPropertyStore(IntPtr hwnd)
        {
            try
            {
                Guid iid = typeof(Win32Helper.IPropertyStore).GUID;
                int hr = Win32Helper.SHGetPropertyStoreForWindow(hwnd, ref iid, out Win32Helper.IPropertyStore propStore);
                if (hr == 0 && propStore != null)
                {
                    Win32Helper.PROPERTYKEY pkey = new Win32Helper.PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                    Win32Helper.PROPVARIANT pv = new Win32Helper.PROPVARIANT();
                    hr = propStore.GetValue(ref pkey, out pv);
                    if (hr == 0 && pv.vt == 31)
                    {
                        string aumid = Marshal.PtrToStringUni(pv.pwszVal) ?? string.Empty;
                        Win32Helper.PropVariantClear(ref pv);
                        return aumid;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static string GetAumidFromProcess(IntPtr hwnd)
        {
            Win32Helper.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return string.Empty;
            IntPtr hProcess = Win32Helper.OpenProcess(0x1000, false, pid);
            if (hProcess == IntPtr.Zero) return string.Empty;
            try
            {
                uint length = 0;
                Win32Helper.GetApplicationUserModelId(hProcess, ref length, null!);
                if (length > 0)
                {
                    var sb = new System.Text.StringBuilder((int)length);
                    if (Win32Helper.GetApplicationUserModelId(hProcess, ref length, sb) == 0) return sb.ToString();
                }
            }
            finally { Win32Helper.CloseHandle(hProcess); }
            return string.Empty;
        }

        private static string GetAumidRobust(IntPtr hwnd, IntPtr realHwnd)
        {
            string aumid = GetAumidFromPropertyStore(hwnd);
            if (!string.IsNullOrEmpty(aumid)) return aumid;

            if (realHwnd != hwnd && realHwnd != IntPtr.Zero)
            {
                aumid = GetAumidFromPropertyStore(realHwnd);
                if (!string.IsNullOrEmpty(aumid)) return aumid;

                aumid = GetAumidFromProcess(realHwnd);
                if (!string.IsNullOrEmpty(aumid)) return aumid;
            }
            return GetAumidFromProcess(hwnd);
        }

        private static BitmapSource ExtractIconFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            IntPtr pidl = IntPtr.Zero;
            if (Win32Helper.SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
            {
                Win32Helper.SHFILEINFO shinfo = new Win32Helper.SHFILEINFO();
                IntPtr res = Win32Helper.SHGetFileInfo(pidl, 0, out shinfo, (uint)Marshal.SizeOf(shinfo), Win32Helper.SHGFI_PIDL | Win32Helper.SHGFI_ICON | Win32Helper.SHGFI_LARGEICON);
                Win32Helper.ILFree(pidl);
                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    var img = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    Win32Helper.DestroyIcon(shinfo.hIcon);
                    return img;
                }
            }

            Guid iid = typeof(Win32Helper.IShellItemImageFactory).GUID;
            int hr = Win32Helper.SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out Win32Helper.IShellItemImageFactory factory);
            if (hr == 0 && factory != null)
            {
                int imgHr = factory.GetImage(new Win32Helper.SIZE(256, 256), Win32Helper.SIIGBF.SIIGBF_RESIZETOFIT | Win32Helper.SIIGBF.SIIGBF_ICONONLY, out IntPtr hBitmap);
                if (imgHr == 0 && hBitmap != IntPtr.Zero)
                {
                    var img = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    Win32Helper.DeleteObject(hBitmap);
                    return img;
                }
            }

            Win32Helper.SHFILEINFO fallbackInfo = new Win32Helper.SHFILEINFO();
            IntPtr fallbackRes = Win32Helper.SHGetFileInfo(path, 0, out fallbackInfo, (uint)Marshal.SizeOf(fallbackInfo), Win32Helper.SHGFI_ICON | Win32Helper.SHGFI_LARGEICON);
            if (fallbackRes != IntPtr.Zero && fallbackInfo.hIcon != IntPtr.Zero)
            {
                var img = Imaging.CreateBitmapSourceFromHIcon(fallbackInfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                Win32Helper.DestroyIcon(fallbackInfo.hIcon);
                return img;
            }

            return null;
        }

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is DockAppConfig app)
            {
                string cacheKey = $"{app.Path}_{app.Name}";

                try
                {
                    // УЛУЧШЕННАЯ проверка на закрытие UWP приложения
                    bool isAppClosing = IsAppActuallyClosing(app);

                    if (isAppClosing)
                    {
                        // Возвращаем кэшированную иконку
                        if (_lastValidIconCache.TryGetValue(cacheKey, out var cachedIcon))
                        {
                            System.Diagnostics.Debug.WriteLine($"Returning cached icon for closing app: {app.Name}");
                            return cachedIcon;
                        }
                        System.Diagnostics.Debug.WriteLine($"No cached icon for closing app: {app.Name}");
                        return Binding.DoNothing;
                    }

                    // Приложение работает нормально - получаем иконку
                    IntPtr activeHwnd = app.WindowHandle;

                    if (app.IsRunning && activeHwnd != IntPtr.Zero)
                    {
                        IntPtr hIcon = IntPtr.Zero;
                        IntPtr result;

                        if (Win32Helper.SendMessageTimeout(activeHwnd, Win32Helper.WM_GETICON, (IntPtr)Win32Helper.ICON_BIG, IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out result) != IntPtr.Zero) hIcon = result;
                        if (hIcon == IntPtr.Zero && Win32Helper.SendMessageTimeout(activeHwnd, Win32Helper.WM_GETICON, (IntPtr)Win32Helper.ICON_SMALL2, IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out result) != IntPtr.Zero) hIcon = result;
                        if (hIcon == IntPtr.Zero && Win32Helper.SendMessageTimeout(activeHwnd, Win32Helper.WM_GETICON, (IntPtr)Win32Helper.ICON_SMALL, IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out result) != IntPtr.Zero) hIcon = result;
                        if (hIcon == IntPtr.Zero) hIcon = Win32Helper.GetClassLongPtr(activeHwnd, Win32Helper.GCLP_HICON);
                        if (hIcon == IntPtr.Zero) hIcon = Win32Helper.GetClassLongPtr(activeHwnd, Win32Helper.GCLP_HICONSM);

                        if (hIcon != IntPtr.Zero)
                        {
                            var img = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            img.Freeze();
                            // ПРИНУДИТЕЛЬНОЕ кэширование для каждого успешного получения
                            _lastValidIconCache[cacheKey] = img;
                            System.Diagnostics.Debug.WriteLine($"Cached icon from window for: {app.Name}");
                            return img;
                        }
                    }

                    string targetPath = app.Path;

                    // ОРИГИНАЛЬНАЯ UWP ЛОГИКА (РАБОЧАЯ!)
                    if (app.IsRunning && app.WindowHandles != null && app.WindowHandles.Count > 0)
                    {
                        foreach (var hwnd in app.WindowHandles)
                        {
                            // Пропускаем мертвые окна
                            if (hwnd == IntPtr.Zero || !Win32Helper.IsWindow(hwnd)) continue;

                            bool isUwpWrapper = IsUwpWrapper(hwnd);
                            IntPtr realHwnd = GetRealUwpWindow(hwnd);

                            if (isUwpWrapper && realHwnd != hwnd)
                            {
                                System.Threading.Thread.Sleep(50);
                                realHwnd = GetRealUwpWindow(hwnd);
                            }

                            string aumid = GetAumidRobust(hwnd, realHwnd);
                            if (!string.IsNullOrEmpty(aumid))
                            {
                                var img = ExtractIconFromPath(@"shell:AppsFolder\" + aumid);
                                if (img != null)
                                {
                                    // ПРИНУДИТЕЛЬНОЕ кэширование UWP иконки
                                    _lastValidIconCache[cacheKey] = img;
                                    System.Diagnostics.Debug.WriteLine($"Cached UWP icon for: {app.Name} (AUMID: {aumid})");
                                    return img;
                                }
                            }

                            if (!IsUwpWrapper(realHwnd))
                            {
                                IntPtr hLiveIcon = GetLiveIcon(realHwnd);
                                if (hLiveIcon != IntPtr.Zero)
                                {
                                    var liveImg = Imaging.CreateBitmapSourceFromHIcon(hLiveIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                    liveImg.Freeze();
                                    // ПРИНУДИТЕЛЬНОЕ кэширование живой иконки
                                    _lastValidIconCache[cacheKey] = liveImg;
                                    System.Diagnostics.Debug.WriteLine($"Cached live icon for: {app.Name}");
                                    return liveImg;
                                }
                            }

                            string realExePath = GetProcessExecutablePath(realHwnd != IntPtr.Zero ? realHwnd : hwnd);
                            if (!string.IsNullOrEmpty(realExePath)) targetPath = realExePath;
                        }
                    }
                    else if (!string.IsNullOrEmpty(targetPath) && !targetPath.Contains("\\") && targetPath.Contains("!"))
                    {
                        var img = ExtractIconFromPath(@"shell:AppsFolder\" + targetPath);
                        if (img != null)
                        {
                            _lastValidIconCache[cacheKey] = img;
                            System.Diagnostics.Debug.WriteLine($"Cached shell icon for: {app.Name}");
                            return img;
                        }
                    }

                    if (string.IsNullOrEmpty(targetPath))
                    {
                        // Возвращаем кэш если нет пути
                        if (_lastValidIconCache.TryGetValue(cacheKey, out var fallback))
                        {
                            System.Diagnostics.Debug.WriteLine($"Returning cached fallback for: {app.Name}");
                            return fallback;
                        }
                        return null!;
                    }

                    var finalImg = ExtractIconFromPath(targetPath);
                    if (finalImg != null)
                    {
                        _lastValidIconCache[cacheKey] = finalImg;
                        System.Diagnostics.Debug.WriteLine($"Cached extracted icon for: {app.Name}");
                        return finalImg;
                    }

                    var fallbackIcon = System.Drawing.Icon.ExtractAssociatedIcon(targetPath);
                    if (fallbackIcon != null)
                    {
                        var fallbackImg = Imaging.CreateBitmapSourceFromHIcon(fallbackIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        fallbackImg.Freeze();
                        _lastValidIconCache[cacheKey] = fallbackImg;
                        System.Diagnostics.Debug.WriteLine($"Cached fallback system icon for: {app.Name}");
                        return fallbackImg;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AppIconConverter error for {app.Name}: {ex.Message}");

                    // При ошибке возвращаем кэш
                    if (_lastValidIconCache.TryGetValue(cacheKey, out var errorFallback))
                    {
                        System.Diagnostics.Debug.WriteLine($"Returning cached icon after error for: {app.Name}");
                        return errorFallback;
                    }
                }
            }
            return null!;
        }

        // НОВЫЙ улучшенный метод определения закрытия
        private static bool IsAppActuallyClosing(DockAppConfig app)
        {
            try
            {
                // Если приложение помечено как удаляющееся
                if (app.IsDeleting)
                {
                    System.Diagnostics.Debug.WriteLine($"App {app.Name} is marked as deleting");
                    return true;
                }

                // Если приложение не запущено
                if (!app.IsRunning)
                {
                    System.Diagnostics.Debug.WriteLine($"App {app.Name} is not running");
                    return true;
                }

                // Проверяем основное окно
                if (app.WindowHandle != IntPtr.Zero && !Win32Helper.IsWindow(app.WindowHandle))
                {
                    System.Diagnostics.Debug.WriteLine($"App {app.Name} main window is dead");
                    return true;
                }

                // Для UWP/многооконных приложений проверяем все окна
                if (app.WindowHandles != null && app.WindowHandles.Count > 0)
                {
                    int aliveWindows = 0;
                    foreach (var hwnd in app.WindowHandles)
                    {
                        if (hwnd != IntPtr.Zero && Win32Helper.IsWindow(hwnd))
                        {
                            aliveWindows++;
                        }
                    }

                    if (aliveWindows == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"App {app.Name} has no alive windows");
                        return true;
                    }

                    // Если остались живые окна, приложение еще работает
                    System.Diagnostics.Debug.WriteLine($"App {app.Name} has {aliveWindows} alive windows");
                    return false;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IsAppActuallyClosing error for {app.Name}: {ex.Message}");
                return false; // В случае ошибки считаем что приложение работает
            }

        }

        public object ConvertBack(object value, Type targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }
}