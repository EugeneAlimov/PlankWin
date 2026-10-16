using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PlankWin
{
    public class AppIconConverter : IValueConverter
    {
        // ============================================================
        //  КЭШ ИКОНОК (память + диск)
        //  Цель: на слабых/виртуальных машинах, где дочернее CoreWindow
        //  прикрепляется с задержкой (гонка), не показывать заглушку.
        //  Один раз добытая иконка сохраняется как PNG в
        //  %LocalAppData%\PlankWin\IconCache\ и при последующих запросах
        //  (в т.ч. в новых сессиях) отдаётся мгновенно.
        //  Любая ошибка ввода-вывода НЕ влияет на работу: тихий фолбэк
        //  на обычное извлечение, как было раньше.
        // ============================================================

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> _memCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource>();

        private static readonly string _cacheDir = InitCacheDir();

        private static string InitCacheDir()
        {
            try
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = System.IO.Path.Combine(baseDir, "PlankWin", "IconCache");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return string.Empty; // кэш на диске недоступен — работаем без него
            }
        }

        // Имя файла = SHA256(ключ).png. Ключ: для UWP - AUMID, для Win32 - путь к exe.
        private static string CacheFilePath(string key)
        {
            if (string.IsNullOrEmpty(_cacheDir) || string.IsNullOrEmpty(key)) return string.Empty;
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return System.IO.Path.Combine(_cacheDir, sb.ToString() + ".png");
            }
            catch { return string.Empty; }
        }

        // Чтение: сначала память, затем диск (с подъёмом в память).
        private static BitmapSource? TryGetFromCache(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            if (_memCache.TryGetValue(key, out var cached)) return cached;

            string file = CacheFilePath(key);
            if (string.IsNullOrEmpty(file)) return null;

            try
            {
                if (!System.IO.File.Exists(file)) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;          // прочитать файл целиком сразу
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(file, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                _memCache[key] = bmp;
                return bmp;
            }
            catch
            {
                return null; // битый/занятый файл — игнорируем, добудем заново
            }
        }

        // Запись: в память всегда, на диск - если файла ещё нет.
        private static void SaveToCache(string key, BitmapSource img)
        {
            if (string.IsNullOrEmpty(key) || img == null) return;

            try { _memCache[key] = img; } catch { }

            string file = CacheFilePath(key);
            if (string.IsNullOrEmpty(file)) return;

            try
            {
                if (System.IO.File.Exists(file)) return; // уже сохранено - не перезаписываем

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(img));

                string tmp = file + ".tmp";
                using (var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                {
                    encoder.Save(fs);
                }
                // атомарная замена, чтобы другой инстанс не прочитал недописанный файл
                if (System.IO.File.Exists(file)) System.IO.File.Delete(tmp);
                else System.IO.File.Move(tmp, file);
            }
            catch
            {
                // нет прав / диск только для чтения / гонка - не критично, просто без диска
            }
        }

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

        internal static BitmapSource? ExtractIconFromPath(string path)
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
                // --- ФИКС ЧЕХАРДЫ ИКОНОК ---
                // Если окно уже находится в процессе закрытия (мертво), 
                // говорим WPF ничего не делать. Это заморозит старую иконку 
                // до конца анимации исчезновения панели.
                if (app.IsRunning && app.WindowHandle != IntPtr.Zero && !Win32Helper.IsWindow(app.WindowHandle))
                {
                    return Binding.DoNothing;
                }

                // Ключ кэша для Win32 (путь). Для UWP уточним ниже, когда добудем AUMID.
                string cacheKey = app.Path ?? string.Empty;

                // PWA (Chrome/Edge): иконка из Web Applications по app-id.
                if (cacheKey.StartsWith("pwa:", StringComparison.OrdinalIgnoreCase))
                {
                    var pwaCached = TryGetFromCache(cacheKey);
                    if (pwaCached != null) return pwaCached;
                    var pwaImg = LoadPwaIcon(cacheKey);
                    if (pwaImg != null) { SaveToCache(cacheKey, pwaImg); return pwaImg; }
                }

                try
                {
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
                            SaveToCache(cacheKey, img);
                            return img;
                        }
                    }

                    string targetPath = app.Path ?? string.Empty;

                    if (app.IsRunning && app.WindowHandles != null && app.WindowHandles.Count > 0)
                    {
                        IntPtr hwnd = app.WindowHandles[0];
                        bool isUwpWrapper = IsUwpWrapper(hwnd);
                        IntPtr realHwnd = GetRealUwpWindow(hwnd);

                        if (isUwpWrapper && realHwnd == hwnd)
                        {
                            System.Threading.Thread.Sleep(50);
                            realHwnd = GetRealUwpWindow(hwnd);
                        }

                        string aumid = GetAumidRobust(hwnd, realHwnd);

                        // Свёрнутый (приостановленный) UWP: дочернего CoreWindow в рамке нет,
                        // поэтому GetAumidRobust возвращает пусто, и ниже targetPath подменился бы
                        // на ApplicationFrameHost.exe — то есть на иконку-заглушку.
                        // Берём AUMID надёжным способом (по парному CoreWindow).
                        if (string.IsNullOrEmpty(aumid))
                            aumid = Win32Helper.GetAumidFromWindow(hwnd);

                        if (!string.IsNullOrEmpty(aumid))
                        {
                            // Для UWP ПРАВИЛЬНЫЙ ключ кэша - это AUMID (не путь с версией пакета).
                            cacheKey = aumid;

                            var img = ExtractIconFromPath(@"shell:AppsFolder\" + aumid);
                            if (img != null)
                            {
                                SaveToCache(cacheKey, img);
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
                                SaveToCache(cacheKey, liveImg);
                                return liveImg;
                            }
                        }

                        // СТРАХОВКА ОТ ГОНКИ: добыть иконку "вживую" не удалось
                        // (на слабой/виртуальной машине CoreWindow мог не успеть прикрепиться).
                        // Пытаемся отдать ранее сохранённую иконку из кэша вместо заглушки.
                        var fromCache = TryGetFromCache(cacheKey);
                        if (fromCache != null) return fromCache;

                        string realExePath = GetProcessExecutablePath(realHwnd != IntPtr.Zero ? realHwnd : hwnd);
                        if (!string.IsNullOrEmpty(realExePath)) targetPath = realExePath;
                    }
                    else if (!string.IsNullOrEmpty(targetPath) && !targetPath.Contains("\\") && targetPath.Contains("!"))
                    {
                        // Закреплённое (не запущенное) UWP-приложение: targetPath сам является AUMID.
                        cacheKey = targetPath;

                        var cachedPinned = TryGetFromCache(cacheKey);
                        if (cachedPinned != null) return cachedPinned;

                        var img = ExtractIconFromPath(@"shell:AppsFolder\" + targetPath);
                        if (img != null)
                        {
                            SaveToCache(cacheKey, img);
                            return img;
                        }
                    }

                    if (string.IsNullOrEmpty(targetPath)) return null!;

                    var finalImg = ExtractIconFromPath(targetPath);
                    if (finalImg != null)
                    {
                        SaveToCache(cacheKey, finalImg);
                        return finalImg;
                    }

                    var fallbackIcon = System.Drawing.Icon.ExtractAssociatedIcon(targetPath);
                    if (fallbackIcon != null)
                    {
                        var fallbackImg = Imaging.CreateBitmapSourceFromHIcon(fallbackIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        fallbackImg.Freeze();
                        SaveToCache(cacheKey, fallbackImg);
                        return fallbackImg;
                    }

                    // Последняя страховка: ничего не добыли, но в кэше что-то есть - отдать.
                    var lastResort = TryGetFromCache(cacheKey);
                    if (lastResort != null) return lastResort;
                }
                catch { }
            }
            return null!;
        }

        // Иконка PWA из папки Web Applications браузера (Chrome/Edge).
        private static BitmapSource? LoadPwaIcon(string pwaPath)
        {
            try
            {
                string body = pwaPath.Substring(4); // убрать "pwa:"
                int at = body.LastIndexOf('@');
                string profile = at >= 0 ? body.Substring(at + 1) : "Default";
                string rest = at >= 0 ? body.Substring(0, at) : body;
                int colon = rest.IndexOf(':');
                string browser = colon >= 0 ? rest.Substring(0, colon) : "chrome";
                string appId = colon >= 0 ? rest.Substring(colon + 1) : rest;

                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string baseDir = browser.Equals("edge", StringComparison.OrdinalIgnoreCase)
                    ? System.IO.Path.Combine(local, "Microsoft", "Edge", "User Data")
                    : System.IO.Path.Combine(local, "Google", "Chrome", "User Data");
                string crxDir = System.IO.Path.Combine(baseDir, profile, "Web Applications", "_crx_" + appId);
                if (!System.IO.Directory.Exists(crxDir)) return null;

                // Иконка: сначала .ico (Chrome PWA кладёт "<Имя>.ico"), затем png (макс. размер).
                string? best = null;
                var icoFiles = System.IO.Directory.GetFiles(crxDir, "*.ico", System.IO.SearchOption.AllDirectories);
                if (icoFiles.Length > 0)
                {
                    best = icoFiles[0];
                    long maxLen = -1;
                    foreach (var f in icoFiles) { long ln = new System.IO.FileInfo(f).Length; if (ln > maxLen) { maxLen = ln; best = f; } }
                }
                else
                {
                    var pngFiles = System.IO.Directory.GetFiles(crxDir, "*.png", System.IO.SearchOption.AllDirectories);
                    int bestSize = -1;
                    foreach (var f in pngFiles)
                    {
                        string nm = System.IO.Path.GetFileNameWithoutExtension(f);
                        int sz = int.TryParse(nm, out int parsed) ? parsed : 0;
                        if (sz > bestSize) { bestSize = sz; best = f; }
                    }
                }
                if (best == null) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(best, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public object ConvertBack(object value, Type targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
    }
}