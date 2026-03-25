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

        private static bool IsAppActuallyClosing(DockAppConfig app)
        {
            try
            {
                // ГЛАВНОЕ ИЗМЕНЕНИЕ: IsDeleting - это реальный признак закрытия
                if (app.IsDeleting)
                {
                    System.Diagnostics.Debug.WriteLine($"App {app.Name} is marked as deleting");
                    return true;
                }

                // НОВАЯ ЛОГИКА: Если приложение не запущено, но НЕ помечено как удаляющееся,
                // то это просто закрепленное неактивное приложение - НЕ закрывающееся!
                if (!app.IsRunning && !app.IsDeleting)
                {
                    System.Diagnostics.Debug.WriteLine($"App {app.Name} is pinned but not running - NOT closing");
                    return false; // ЭТО КЛЮЧЕВОЕ ИЗМЕНЕНИЕ!
                }

                // Если приложение запущено, проверяем состояние окон
                if (app.IsRunning)
                {
                    // Проверяем основное окно
                    if (app.WindowHandle != IntPtr.Zero && !Win32Helper.IsWindow(app.WindowHandle))
                    {
                        System.Diagnostics.Debug.WriteLine($"App {app.Name} main window is dead");
                        return true;
                    }

                    // Для многооконных приложений проверяем все окна
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

                        System.Diagnostics.Debug.WriteLine($"App {app.Name} has {aliveWindows} alive windows");
                    }
                }

                return false; // По умолчанию считаем что приложение НЕ закрывается
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IsAppActuallyClosing error for {app.Name}: {ex.Message}");
                return false; // В случае ошибки считаем что приложение работает
            }
        }

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is DockAppConfig app)
            {
                string cacheKey = $"{app.Path}_{app.Name}";

                System.Diagnostics.Debug.WriteLine($"=== ICON CONVERT REQUEST ===");
                System.Diagnostics.Debug.WriteLine($"App: {app.Name}");
                System.Diagnostics.Debug.WriteLine($"Path: {app.Path}");
                System.Diagnostics.Debug.WriteLine($"IsRunning: {app.IsRunning}");
                System.Diagnostics.Debug.WriteLine($"IsDeleting: {app.IsDeleting}");
                System.Diagnostics.Debug.WriteLine($"WindowHandle: {app.WindowHandle}");
                System.Diagnostics.Debug.WriteLine($"WindowHandles Count: {app.WindowHandles?.Count ?? 0}");
                System.Diagnostics.Debug.WriteLine($"Cache Key: {cacheKey}");
                System.Diagnostics.Debug.WriteLine($"Has Cached Icon: {_lastValidIconCache.ContainsKey(cacheKey)}");

                // НОВОЕ: Если иконка еще не загружена и приложение только запустилось, пробуем агрессивно
                if (!_lastValidIconCache.ContainsKey(cacheKey) && app.IsRunning)
                {
                    System.Diagnostics.Debug.WriteLine($"No cached icon for {app.Name}, attempting aggressive load");

                    if (!string.IsNullOrEmpty(app.Path))
                    {
                        try
                        {
                            System.Diagnostics.Debug.WriteLine($"Trying ExtractIconFromPath: {app.Path}");
                            var quickIcon = ExtractIconFromPath(app.Path);
                            if (quickIcon != null)
                            {
                                _lastValidIconCache[cacheKey] = quickIcon;
                                System.Diagnostics.Debug.WriteLine($"✅ Quick loaded icon for {app.Name}");
                                System.Diagnostics.Debug.WriteLine($"=============================");
                                return quickIcon;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"❌ ExtractIconFromPath returned null for {app.Path}");
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"❌ ExtractIconFromPath exception: {ex.Message}");
                        }
                    }
                }

                try
                {
                    bool isAppClosing = IsAppActuallyClosing(app);
                    System.Diagnostics.Debug.WriteLine($"Is App Closing: {isAppClosing}");

                    if (isAppClosing)
                    {
                        if (_lastValidIconCache.TryGetValue(cacheKey, out var cachedIcon))
                        {
                            System.Diagnostics.Debug.WriteLine($"✅ Returning cached icon for closing app: {app.Name}");
                            System.Diagnostics.Debug.WriteLine($"=============================");
                            return cachedIcon;
                        }
                        System.Diagnostics.Debug.WriteLine($"❌ No cached icon for closing app: {app.Name}");
                        System.Diagnostics.Debug.WriteLine($"=============================");
                        return Binding.DoNothing;
                    }

                    // Проверяем активное окно
                    IntPtr activeHwnd = app.WindowHandle;
                    System.Diagnostics.Debug.WriteLine($"Active HWND: {activeHwnd}");

                    if (app.IsRunning && activeHwnd != IntPtr.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine($"Trying to get icon from active window...");
                        IntPtr hIcon = IntPtr.Zero;
                        IntPtr result;

                        if (Win32Helper.SendMessageTimeout(activeHwnd, Win32Helper.WM_GETICON, (IntPtr)Win32Helper.ICON_BIG, IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out result) != IntPtr.Zero) hIcon = result;
                        if (hIcon == IntPtr.Zero && Win32Helper.SendMessageTimeout(activeHwnd, Win32Helper.WM_GETICON, (IntPtr)Win32Helper.ICON_SMALL2, IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out result) != IntPtr.Zero) hIcon = result;
                        if (hIcon == IntPtr.Zero && Win32Helper.SendMessageTimeout(activeHwnd, Win32Helper.WM_GETICON, (IntPtr)Win32Helper.ICON_SMALL, IntPtr.Zero, Win32Helper.SMTO_ABORTIFHUNG, 50, out result) != IntPtr.Zero) hIcon = result;
                        if (hIcon == IntPtr.Zero) hIcon = Win32Helper.GetClassLongPtr(activeHwnd, Win32Helper.GCLP_HICON);
                        if (hIcon == IntPtr.Zero) hIcon = Win32Helper.GetClassLongPtr(activeHwnd, Win32Helper.GCLP_HICONSM);

                        System.Diagnostics.Debug.WriteLine($"Got hIcon from window: {hIcon}");

                        if (hIcon != IntPtr.Zero)
                        {
                            var img = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            img.Freeze();
                            _lastValidIconCache[cacheKey] = img;
                            System.Diagnostics.Debug.WriteLine($"✅ Got icon from window for: {app.Name}");
                            System.Diagnostics.Debug.WriteLine($"=============================");
                            return img;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"❌ No icon from window for: {app.Name}");
                        }
                    }

                    string targetPath = app.Path;
                    System.Diagnostics.Debug.WriteLine($"Target Path: {targetPath}");

                    // UWP и остальная логика...
                    if (app.IsRunning && app.WindowHandles != null && app.WindowHandles.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"Processing {app.WindowHandles.Count} window handles...");

                        foreach (var hwnd in app.WindowHandles)
                        {
                            if (hwnd == IntPtr.Zero || !Win32Helper.IsWindow(hwnd)) continue;

                            bool isUwpWrapper = IsUwpWrapper(hwnd);
                            IntPtr realHwnd = GetRealUwpWindow(hwnd);
                            System.Diagnostics.Debug.WriteLine($"  HWND {hwnd}: UWP={isUwpWrapper}, RealHWND={realHwnd}");

                            if (isUwpWrapper && realHwnd != hwnd)
                            {
                                System.Threading.Thread.Sleep(50);
                                realHwnd = GetRealUwpWindow(hwnd);

                                string aumid = GetAumidRobust(hwnd, realHwnd);
                                System.Diagnostics.Debug.WriteLine($"  Got AUMID: {aumid}");

                                if (!string.IsNullOrEmpty(aumid))
                                {
                                    var img = ExtractIconFromPath(@"shell:AppsFolder\" + aumid);
                                    if (img != null)
                                    {
                                        _lastValidIconCache[cacheKey] = img;
                                        System.Diagnostics.Debug.WriteLine($"✅ Got UWP icon for: {app.Name} (AUMID: {aumid})");
                                        System.Diagnostics.Debug.WriteLine($"=============================");
                                        return img;
                                    }
                                }
                            }

                            if (!IsUwpWrapper(realHwnd))
                            {
                                IntPtr hLiveIcon = GetLiveIcon(realHwnd);
                                System.Diagnostics.Debug.WriteLine($"  Live icon from {realHwnd}: {hLiveIcon}");

                                if (hLiveIcon != IntPtr.Zero)
                                {
                                    var liveImg = Imaging.CreateBitmapSourceFromHIcon(hLiveIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                    liveImg.Freeze();
                                    _lastValidIconCache[cacheKey] = liveImg;
                                    System.Diagnostics.Debug.WriteLine($"✅ Got live icon for: {app.Name}");
                                    System.Diagnostics.Debug.WriteLine($"=============================");
                                    return liveImg;
                                }
                            }

                            string realExePath = GetProcessExecutablePath(realHwnd != IntPtr.Zero ? realHwnd : hwnd);
                            if (!string.IsNullOrEmpty(realExePath)) targetPath = realExePath;
                            System.Diagnostics.Debug.WriteLine($"  Real exe path: {realExePath}");
                        }
                    }
                    else if (!string.IsNullOrEmpty(targetPath) && !targetPath.Contains("\\") && targetPath.Contains("!"))
                    {
                        System.Diagnostics.Debug.WriteLine($"Trying shell:AppsFolder path: {targetPath}");
                        var img = ExtractIconFromPath(@"shell:AppsFolder\" + targetPath);
                        if (img != null)
                        {
                            _lastValidIconCache[cacheKey] = img;
                            System.Diagnostics.Debug.WriteLine($"✅ Got shell icon for: {app.Name}");
                            System.Diagnostics.Debug.WriteLine($"=============================");
                            return img;
                        }
                    }

                    if (string.IsNullOrEmpty(targetPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"❌ Empty target path for: {app.Name}");
                        if (_lastValidIconCache.TryGetValue(cacheKey, out var fallback))
                        {
                            System.Diagnostics.Debug.WriteLine($"✅ Returning cached fallback for: {app.Name}");
                            System.Diagnostics.Debug.WriteLine($"=============================");
                            return fallback;
                        }
                        System.Diagnostics.Debug.WriteLine($"❌ No fallback for: {app.Name}");
                        System.Diagnostics.Debug.WriteLine($"=============================");
                        return null!;
                    }

                    System.Diagnostics.Debug.WriteLine($"Trying final ExtractIconFromPath: {targetPath}");
                    var finalImg = ExtractIconFromPath(targetPath);
                    if (finalImg != null)
                    {
                        _lastValidIconCache[cacheKey] = finalImg;
                        System.Diagnostics.Debug.WriteLine($"✅ Got final icon for: {app.Name}");
                        System.Diagnostics.Debug.WriteLine($"=============================");
                        return finalImg;
                    }

                    System.Diagnostics.Debug.WriteLine($"Trying System.Drawing fallback for: {targetPath}");
                    var fallbackIcon = System.Drawing.Icon.ExtractAssociatedIcon(targetPath);
                    if (fallbackIcon != null)
                    {
                        var fallbackImg = Imaging.CreateBitmapSourceFromHIcon(fallbackIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        fallbackImg.Freeze();
                        _lastValidIconCache[cacheKey] = fallbackImg;
                        System.Diagnostics.Debug.WriteLine($"✅ Got System.Drawing icon for: {app.Name}");
                        System.Diagnostics.Debug.WriteLine($"=============================");
                        return fallbackImg;
                    }

                    System.Diagnostics.Debug.WriteLine($"❌ ALL METHODS FAILED for: {app.Name}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ EXCEPTION in Convert for {app.Name}: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");

                    if (_lastValidIconCache.TryGetValue(cacheKey, out var errorFallback))
                    {
                        System.Diagnostics.Debug.WriteLine($"✅ Returning cached icon after error for: {app.Name}");
                        System.Diagnostics.Debug.WriteLine($"=============================");
                        return errorFallback;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"❌ FINAL FAILURE for: {app.Name}");
                System.Diagnostics.Debug.WriteLine($"=============================");
            }
            return null!;
        }

        public object ConvertBack(object value, Type targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }
}