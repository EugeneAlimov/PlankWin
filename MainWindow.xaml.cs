using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System;
using System.Linq;

namespace PlankWin
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DockAppConfig> Apps { get; } = new();

        private DockAppConfig? MainAppViewModel => Apps.FirstOrDefault(a => !a.IsDeleting);

        public DockSettings Settings { get; private set; } = new();

        // Свойство для плавного свечения края
        public static readonly DependencyProperty EdgePushProgressProperty =
            DependencyProperty.Register("EdgePushProgress", typeof(double), typeof(MainWindow), new PropertyMetadata(0.0));

        public double EdgePushProgress
        {
            get { return (double)GetValue(EdgePushProgressProperty); }
            set { SetValue(EdgePushProgressProperty, value); }
        }

        private string _profileName = "Primary";

        private bool _isLoadingConfig = false;

        public DockPosition _visualPosition = DockPosition.Bottom;
        public DockAlignment _visualAlignment = DockAlignment.Center;

        private DispatcherTimer? _autoHideTimer;
        private bool _isHidden;

        private bool _isTransitioning = false;

        private const int AutoTimerIntervalMs = 100;
        private const int EdgePressMs = 250;
        private int _openContextMenusCount = 0;

        private DockAppConfig? _draggedApp = null;
        private ContextMenu? _activeIconMenu;

        private bool _allowClose = false;
        private double _visibleTimeMs = 0;
        private IntPtr _lastRealForeground = IntPtr.Zero;

        private bool _isForceHidden = false;

        private Point _dragStartPoint;
        private bool _isDragging = false;
        // Кнопка, на которой реально было нажатие ЛКМ. Клик, закрывающий контекстное меню, съедает popup,
        // и PreviewMouseLeftButtonDown до кнопки не доходит — без этой проверки MouseMove сравнивал бы
        // курсор со старой точкой от предыдущего клика и сразу начинал перетаскивание (кнопка «дёргалась»).
        private object? _dragSource;
        private readonly DragGhost _dragGhost = new DragGhost();
        private DateTime _lastSwapTime = DateTime.MinValue;
        private DockAppConfig? _placeholderApp;

        public static DockAppConfig? DraggedAppConfig = null;

        private double _shownLeft;
        private double _shownTop;
        private double _hiddenLeft;
        private double _hiddenTop;

        private bool _isFirstStartup = true;

        public const double ZoomOverflow = 40.0; // запас по толщине: иконки на полке (shelf) выступают + тень
        private bool _isExternalDragActive = false;
        private DispatcherTimer? _menuWatcherTimer;

        // СТОРОЖЕВОЙ ТАЙМЕР ОТМЕНЫ ВНЕШНЕГО ПЕРЕТАСКИВАНИЯ.
        // Когда ярлык тянут с рабочего стола на док, а потом ОТПУСКАЮТ вне дока
        // (передумали, вернули обратно), WPF не присылает доку ни Drop, ни надёжный
        // DragLeave — drop ушёл в другое приложение. В результате _placeholderApp и
        // _isExternalDragActive "зависают", и панель замирает. Этот таймер страхует:
        // если внешний drag активен, но левая кнопка мыши уже отпущена — значит
        // перетаскивание завершилось где-то снаружи, и мы принудительно сбрасываем состояние.
        private DispatcherTimer? _dragWatchdogTimer;

        public MainWindow(string profileName)
        {
            InitializeComponent();

            _profileName = profileName;

            this.Opacity = 0;
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Left = -10000;
            this.Top = -10000;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                int exStyle = PlankWin.Win32Helper.GetWindowLong(source.Handle, PlankWin.Win32Helper.GWL_EXSTYLE);
                PlankWin.Win32Helper.SetWindowLong(source.Handle, PlankWin.Win32Helper.GWL_EXSTYLE, exStyle | PlankWin.Win32Helper.WS_EX_TOOLWINDOW | 0x08000000);

                source.AddHook(WndProc);
                _mainHwnd = source.Handle;
                InstallScrollHook();

                // Сырой ввод мыши (упор в край). Регистрация действует на ПРОЦЕСС и только для одного
                // окна (последнего зарегистрировавшего), поэтому регистрирует одна панель и раздаёт всем.
                if (_rawInputOwner == null) RegisterRawInput();
                // Горячие клавиши — тоже одна регистрация на процесс.
                if (_hotkeyOwner == null) { _hotkeyOwner = this; Dispatcher.BeginInvoke(new Action(ApplyHotkeys)); }
                StartBadgeTimer();
            }
        }

        private static MainWindow? _rawInputOwner;

        private void RegisterRawInput()
        {
            if (_mainHwnd == IntPtr.Zero) return;
            var rid = new PlankWin.Win32Helper.RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 0x01; // HID Generic Desktop
            rid[0].usUsage = 0x02;     // Мышь / тачпад
            rid[0].dwFlags = PlankWin.Win32Helper.RIDEV_INPUTSINK;
            rid[0].hwndTarget = _mainHwnd;
            if (PlankWin.Win32Helper.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(rid[0])))
                _rawInputOwner = this;
        }

        // Перезапуск приложения (после импорта настроек): новый процесс ждёт завершения этого.
        public static void RestartApp()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    Process.Start(new ProcessStartInfo(exe, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false });
            }
            catch { }
            ShutdownForRestart();
        }

        // Закрыть все панели и завершить процесс (перезапуск, установка, удаление).
        public static void ShutdownForRestart()
        {
            foreach (var d in _trayDocks.ToList()) d._allowClose = true;
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;

            UpdateAppBar(forceRemove: true);

            // Общий трей: убираем себя; последний закрывшийся убирает значок.
            _trayDocks.Remove(this);
            if (_trayDocks.Count == 0 && _sharedTray != null)
            {
                _sharedTray.Visible = false;
                _sharedTray.Dispose();
                _sharedTray = null;
            }

            // Сырой ввод регистрируется на процесс ОДНИМ окном — передаём другому, если это были мы.
            if (_rawInputOwner == this)
            {
                _rawInputOwner = null;
                var next = _trayDocks.FirstOrDefault();
                next?.RegisterRawInput();
            }

            ReleaseHotkeysOnClose();
            StopBadgeTimer();
            StopProcessMonitor();
            base.OnClosed(e);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_CLOSE = 0xF060;
            const int WM_GETMINMAXINFO = 0x0024;
            const int WM_MOUSEHWHEEL = 0x020E;
            const int WM_MOUSEWHEEL = 0x020A;

            if (HandleHotkeyMessage(msg, wParam)) { handled = true; return IntPtr.Zero; }

            if (msg == WM_MOUSEHWHEEL)
            {
                int hdelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (_appsOverflow && !_appsVertical) { ApplyAppsScroll(-hdelta * 0.5); handled = true; return IntPtr.Zero; }
            }

            // Вертикальное колесо/жест ловим по всему окну (не только над иконкой) — иначе в полочке
            // скролл пропадает, когда под курсором выступающая часть окна без иконки.
            if (msg == WM_MOUSEWHEEL && _appsOverflow)
            {
                int vdelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                if (Math.Abs(vdelta) >= 120) ApplyAppsScroll(vdelta > 0 ? 40 : -40); // мышь
                else if (_appsVertical) ApplyAppsScroll(vdelta * 0.5); // вертикальный тачпад — только вертикальная панель
                handled = true; return IntPtr.Zero;
            }

            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = (PlankWin.Win32Helper.MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(PlankWin.Win32Helper.MINMAXINFO))!;
                mmi.ptMinTrackSize.X = 1;
                mmi.ptMinTrackSize.Y = 1;
                Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            if (msg == WM_SYSCOMMAND)
            {
                int cmd = wParam.ToInt32() & 0xFFF0;
                if (cmd == SC_CLOSE && !_allowClose)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            if (msg == PlankWin.Win32Helper.WM_INPUT)
            {
                uint dwSize = (uint)Marshal.SizeOf(typeof(PlankWin.Win32Helper.RAWINPUT));
                PlankWin.Win32Helper.GetRawInputData(lParam, PlankWin.Win32Helper.RID_INPUT, out var raw, ref dwSize, (uint)Marshal.SizeOf(typeof(PlankWin.Win32Helper.RAWINPUTHEADER)));

                if (raw.header.dwType == PlankWin.Win32Helper.RIM_TYPEMOUSE)
                {
                    // Раздаём всем панелям: каждая сама проверяет край СВОЕГО монитора.
                    foreach (var dock in _trayDocks.ToList())
                        dock.ProcessRawMouseInput(raw.mouse.lLastX, raw.mouse.lLastY, raw.mouse.usFlags);
                }
            }

            return IntPtr.Zero;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Settings = PlankConfig.Load(AppContext.BaseDirectory, _profileName);
            // Автозапуск — общий для приложения: источник правды — реестр, а не профиль монитора.
            Settings.RunAtStartup = IsAutostartEnabled();

            _visualPosition = Settings.Position;
            _visualAlignment = Settings.Alignment;

            Settings.UIPosition = Settings.Position;
            DataContext = this;
            Settings.PropertyChanged += Settings_PropertyChanged;

            Apps.CollectionChanged += Apps_CollectionChanged;

            ApplySystemTheme();
            SystemEvents.UserPreferenceChanged += (s, ev) => Dispatcher.InvokeAsync(OnWindowsColorsChanged);

            LoadAppsConfig();
            SetupAutoHide();
            SetupTrayIcon();

            this.UpdateLayout();
            await Task.Delay(200);

            bool realHiddenState = _isHidden;
            _isHidden = true;
            PositionDock(skipAnimation: true);

            await Task.Yield();
            await Task.Delay(50);

            this.Opacity = 1;
            _isHidden = realHiddenState;
            PositionDock(skipAnimation: false);

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            InitializeProcessMonitor();
            _ = Dispatcher.BeginInvoke(new Action(() => WarmupPreview()), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Смена темы/акцента Windows: подхватываем без похода в настройки.
        private async void OnWindowsColorsChanged()
        {
            await Task.Delay(300); // реестр/DWM обновляются чуть позже события
            if (Settings.FollowWindowsColor)
                Settings.BackgroundColor = PlankWin.Win32Helper.GetWindowsAccentColorHex();
            ApplySystemTheme();
            // Конвертер объёма читает тему/акцент сам — заставляем пересчитать.
            System.Windows.Data.BindingOperations.GetMultiBindingExpression(MainBorder, System.Windows.Controls.Border.BackgroundProperty)?.UpdateTarget();
        }

        // Приглушить цвет: подмешать нейтральный серый (k=доля серого 0..1).
        private static System.Windows.Media.Color MuteColor(System.Windows.Media.Color c, double k)
        {
            const double gray = 128.0;
            byte Mix(byte v) => (byte)(v * (1.0 - k) + gray * k);
            return System.Windows.Media.Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
        }

        private void ApplySystemTheme()
        {
            bool isDark = false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null && key.GetValue("AppsUseLightTheme") is int useLight)
                {
                    isDark = useLight == 0;
                }
            }
            catch { }

            Application.Current.Resources["MenuBgBrush"] = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Color.FromRgb(43, 43, 43) : System.Windows.Media.Color.FromRgb(249, 249, 249));
            Application.Current.Resources["MenuTextBrush"] = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black);
            Application.Current.Resources["MenuHoverBrush"] = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Color.FromRgb(65, 65, 65) : System.Windows.Media.Color.FromRgb(234, 234, 234));
            // Акцент Windows для элементов интерфейса (переключатели, вкладки, списки).
            try { Application.Current.Resources["AccentBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(PlankWin.Win32Helper.GetAccentColorRaw())); } catch { }
            Application.Current.Resources["MenuBorderBrush"] = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Color.FromRgb(70, 70, 70) : System.Windows.Media.Color.FromRgb(220, 220, 220));

            // Эффект парения: тёмная тема — светлое свечение вокруг панели, светлая — обычная тёмная тень.
            if (PanelShadow != null)
            {
                if (Settings.UseAccentShadow)
                {
                    try
                    {
                        var accRaw = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(PlankWin.Win32Helper.GetAccentColorRaw());
                        double f = Settings.AccentIntensity / 50.0; // 0..2, 1 = базовый вид
                        var acc = MuteColor(accRaw, Math.Clamp(1.0 - 0.6 * f, 0.0, 1.0)); // при f=1 серого 0.4
                        PanelShadow.Color = acc;
                        PanelShadow.Opacity = Math.Min(1.0, (isDark ? 0.5 : 0.45) * f); // акцент заметнее нейтральной тени
                        if (MainBorder != null) { MainBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(acc); MainBorder.BorderThickness = new Thickness(f > 0 ? 0.75 : 0); }
                    }
                    catch { if (isDark) { PanelShadow.Color = System.Windows.Media.Colors.White; PanelShadow.Opacity = 0.07; } else { PanelShadow.Color = System.Windows.Media.Colors.Black; PanelShadow.Opacity = 0.2; } }
                }
                else if (isDark) { PanelShadow.Color = System.Windows.Media.Colors.White; PanelShadow.Opacity = 0.07; if (MainBorder != null) MainBorder.BorderThickness = new Thickness(0); }
                else { PanelShadow.Color = System.Windows.Media.Colors.Black; PanelShadow.Opacity = 0.2; if (MainBorder != null) MainBorder.BorderThickness = new Thickness(0); }
            }
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() => PositionDock(skipAnimation: false));
        }

        private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DockSettings.UIPosition)) return;

            PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);

            if (e.PropertyName == nameof(DockSettings.EnableFavorites) || e.PropertyName == nameof(DockSettings.EnableFavoriteFiles))
                ApplyFavoritesButton();

            if (e.PropertyName == nameof(DockSettings.UseAccentShadow) || e.PropertyName == nameof(DockSettings.AccentIntensity))
                ApplySystemTheme();

            if (e.PropertyName == nameof(DockSettings.FollowWindowsColor) && Settings.FollowWindowsColor)
                Settings.BackgroundColor = PlankWin.Win32Helper.GetWindowsAccentColorHex();

            if (e.PropertyName == nameof(DockSettings.RunAtStartup))
            {
                ApplyAutostart(Settings.RunAtStartup);
                // Синхронизируем галочку во всех панелях (равенство в сеттере гасит цикл).
                foreach (var d in _trayDocks.ToList())
                    if (d != this) d.Settings.RunAtStartup = Settings.RunAtStartup;
                return;
            }

            if (e.PropertyName == nameof(DockSettings.AutoHide) || e.PropertyName == nameof(DockSettings.VisibilityMode))
            {
                // Резервирование места имеет смысл ТОЛЬКО в режиме "Всегда видима".
                // При уходе из него — принудительно сбрасываем флаг (галочка в UI станет
                // неактивной через конвертер, а значение физически выключается здесь).
                if (Settings.VisibilityMode != DockVisibilityMode.AlwaysVisible && Settings.ReserveScreenSpace)
                {
                    Settings.ReserveScreenSpace = false; // вызовет повторный заход сюда уже с ReserveScreenSpace — обработается ниже
                }

                SetupAutoHide();
            }
            else if (e.PropertyName == nameof(DockSettings.Position) || e.PropertyName == nameof(DockSettings.Alignment))
            {
                if (_isTransitioning) return;

                Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        _isTransitioning = true;
                        _isHidden = true;
                        PositionDock(skipAnimation: false);
                        await System.Threading.Tasks.Task.Delay(450);

                        this.Opacity = 0;
                        await System.Threading.Tasks.Task.Delay(50);

                        AnimatedStackPanel.IsAnimationSuspended = true;

                        // Сброс скролла при смене ориентации — иначе лента остаётся сдвинутой за экран.
                        _appsScrollOffset = 0;
                        AppsScrollTransform.X = 0;
                        AppsScrollTransform.Y = 0;
                        AppsScrollHost.Clip = null;
                        AppsScrollHost.OpacityMask = null;
                        AppsScrollHost.ClearValue(FrameworkElement.WidthProperty);
                        AppsScrollHost.ClearValue(FrameworkElement.HeightProperty);
                        System.Windows.Controls.Canvas.SetTop(AppsItemsControl, 0);
                        System.Windows.Controls.Canvas.SetLeft(AppsItemsControl, 0);

                        _visualPosition = Settings.Position;
                        _visualAlignment = Settings.Alignment;
                        Settings.UIPosition = Settings.Position;

                        PositionDock(skipAnimation: true);
                        this.UpdateLayout();
                        await System.Threading.Tasks.Task.Delay(100);

                        this.Opacity = 1;
                        await System.Threading.Tasks.Task.Delay(50);

                        AnimatedStackPanel.IsAnimationSuspended = false;

                        _isHidden = false;
                        PositionDock(skipAnimation: false);

                        await System.Threading.Tasks.Task.Delay(400);
                    }
                    finally
                    {
                        _isTransitioning = false;
                        if (_visualPosition != Settings.Position || _visualAlignment != Settings.Alignment)
                        {
                            Settings_PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DockSettings.Position)));
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Normal);
            }
            else
            {
                if (!_isTransitioning)
                {
                    Dispatcher.InvokeAsync(() => PositionDock(false));
                }
            }
        }

        // ===== ОДИН ЗНАЧОК В ТРЕЕ НА ВСЕ ПАНЕЛИ =====
        // Каждая панель (монитор) регистрируется в общем списке. Меню собирается при открытии:
        // одна панель — пункты плоско, несколько — подменю на каждый монитор (слева направо).
        private static System.Windows.Forms.NotifyIcon? _sharedTray;
        private static readonly List<MainWindow> _trayDocks = new();
        private static Action? _balloonClick;

        // Уведомление из трея (на Win10/11 — системный тост). onClick — действие по клику на уведомление.
        public static void ShowTrayBalloon(string title, string text, Action? onClick)
        {
            if (_sharedTray == null) return;
            _balloonClick = onClick;
            try { _sharedTray.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.Info); } catch { }
        }

        private void SetupTrayIcon()
        {
            _trayDocks.Add(this);
            if (_sharedTray != null) return;

            _sharedTray = new System.Windows.Forms.NotifyIcon { Text = "PlankWin" };
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exePath)) _sharedTray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch { }

            _sharedTray.BalloonTipClicked += (s, a) => { var act = _balloonClick; _balloonClick = null; act?.Invoke(); };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("PlankWin"); // заглушка: пустое меню WinForms не открывает
            menu.Opening += (s, args) =>
            {
                menu.Items.Clear();
                var titleItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray_title")) { Enabled = false };
                menu.Items.Add(titleItem);
                // Найдено обновление — первым пунктом.
                if (Updater.Available is Updater.ReleaseInfo upd)
                {
                    var updItem = new System.Windows.Forms.ToolStripMenuItem(string.Format(Loc.T("upd_menu"), upd.Version.ToString(3)))
                    { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold), Enabled = !Updater.Busy };
                    updItem.Click += (s2, a2) => _ = Updater.PromptAndUpdate(upd);
                    menu.Items.Add(updItem);
                }
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

                var app = Application.Current as App;
                var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(x => x.Bounds.Left).ThenBy(x => x.Bounds.Top).ToList();
                if (screens.Count == 1)
                {
                    var only = app?.GetDock(screens[0].DeviceName) ?? _trayDocks.FirstOrDefault();
                    if (only != null) foreach (var it in only.BuildTrayItems()) menu.Items.Add(it);
                }
                else
                {
                    int enabledCount = screens.Count(x => app?.GetDock(x.DeviceName) != null);
                    for (int i = 0; i < screens.Count; i++)
                    {
                        var scr = screens[i];
                        var d = app?.GetDock(scr.DeviceName);
                        string label = $"{Loc.T("tray_monitor")} {i + 1} ({scr.Bounds.Width}×{scr.Bounds.Height}{(scr.Primary ? ", " + Loc.T("tray_primary") : "")})" + (d == null ? " — " + Loc.T("tray_off") : "");
                        var sub = new System.Windows.Forms.ToolStripMenuItem(label);

                        // Включение/выключение панели на этом мониторе (последнюю выключить нельзя).
                        string devName = scr.DeviceName;
                        var toggle = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray_panelHere"))
                        {
                            Checked = d != null,
                            Enabled = d == null || enabledCount > 1
                        };
                        toggle.Click += (s2, a2) => app?.SetMonitorEnabled(devName, d == null);
                        sub.DropDownItems.Add(toggle);

                        if (d != null)
                        {
                            sub.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
                            foreach (var it in d.BuildTrayItems()) sub.DropDownItems.Add(it);
                        }
                        menu.Items.Add(sub);
                    }
                }

                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                var checkItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("upd_check")) { Enabled = !Updater.Busy };
                checkItem.Click += (s2, a2) => _ = Updater.ManualCheck();
                menu.Items.Add(checkItem);
                // Установка (переносной exe) / удаление (установленный).
                if (SelfInstaller.IsInstalled)
                {
                    var un = new System.Windows.Forms.ToolStripMenuItem(Loc.T("uninst_menu"));
                    un.Click += (s2, a2) => SelfInstaller.Uninstall();
                    menu.Items.Add(un);
                }
                else if (!Updater.IsDevBuild)
                {
                    var inst = new System.Windows.Forms.ToolStripMenuItem(Loc.T("inst_menu"));
                    inst.Click += (s2, a2) => SelfInstaller.Install();
                    menu.Items.Add(inst);
                }
                var exitItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray_exit"));
                exitItem.Click += (s2, a2) =>
                {
                    foreach (var d in _trayDocks.ToList()) d._allowClose = true;
                    Application.Current.Shutdown();
                };
                menu.Items.Add(exitItem);
                foreach (System.Windows.Forms.ToolStripItem it in menu.Items) FitSubmenuToScreen(it);
                // Тема меню трея — по текущей теме Windows и акценту.
                bool trayDark = true;
                try
                {
                    using var pk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    if (pk?.GetValue("AppsUseLightTheme") is int lt) trayDark = lt == 0;
                }
                catch { }
                var trayAccent = System.Drawing.ColorTranslator.FromHtml(PlankWin.Win32Helper.GetAccentColorRaw());
                ThemedTrayRenderer.Apply(menu, trayDark, trayAccent);
                args.Cancel = false;
            };

            _sharedTray.ContextMenuStrip = menu;
            _sharedTray.Visible = true;
        }

        // Подменю трея открываются вправо; если меню у правого края своего монитора (трей на стыке),
        // направляем их влево, чтобы не уезжали на соседний экран.
        private static void FitSubmenuToScreen(System.Windows.Forms.ToolStripItem item)
        {
            if (item is not System.Windows.Forms.ToolStripMenuItem mi || !mi.HasDropDownItems) return;
            mi.DropDownOpening += (s, e) =>
            {
                var owner = mi.Owner;
                if (owner == null) return;
                var wa = System.Windows.Forms.Screen.FromRectangle(owner.Bounds).WorkingArea;
                int need = Math.Max(mi.DropDown.PreferredSize.Width, 150);
                mi.DropDownDirection = owner.Bounds.Right + need > wa.Right
                    ? System.Windows.Forms.ToolStripDropDownDirection.Left
                    : System.Windows.Forms.ToolStripDropDownDirection.Right;
            };
            foreach (System.Windows.Forms.ToolStripItem child in mi.DropDownItems) FitSubmenuToScreen(child);
        }

        private System.Drawing.Rectangle GetScreenBounds()
        {
            var scr = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == _profileName);
            return scr?.Bounds ?? System.Drawing.Rectangle.Empty;
        }

        // Пункты трея для ЭТОЙ панели (создаются заново при каждом открытии меню — галочки актуальны).
        private List<System.Windows.Forms.ToolStripItem> BuildTrayItems()
        {
            var items = new List<System.Windows.Forms.ToolStripItem>();

            var modeMenu = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray_mode"));
            var modeAlways = new System.Windows.Forms.ToolStripMenuItem(Loc.T("mode_always")) { Checked = Settings.VisibilityMode == DockVisibilityMode.AlwaysVisible };
            var modeAuto = new System.Windows.Forms.ToolStripMenuItem(Loc.T("mode_auto")) { Checked = Settings.VisibilityMode == DockVisibilityMode.AutoHide };
            var modeSmart = new System.Windows.Forms.ToolStripMenuItem(Loc.T("mode_smart")) { Checked = Settings.VisibilityMode == DockVisibilityMode.SmartHide };
            modeAlways.Click += (s, args) => { Settings.VisibilityMode = DockVisibilityMode.AlwaysVisible; };
            modeAuto.Click += (s, args) => { Settings.VisibilityMode = DockVisibilityMode.AutoHide; };
            modeSmart.Click += (s, args) => { Settings.VisibilityMode = DockVisibilityMode.SmartHide; };
            modeMenu.DropDownItems.Add(modeAlways);
            modeMenu.DropDownItems.Add(modeAuto);
            modeMenu.DropDownItems.Add(modeSmart);
            items.Add(modeMenu);

            var forceHideItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray_forceHide")) { Checked = _isForceHidden };
            forceHideItem.Click += (s, args) =>
            {
                _isForceHidden = !_isForceHidden;
                if (_isForceHidden) { _isHidden = true; PositionDock(); }
                else { if (!Settings.AutoHide) { _isHidden = false; } PositionDock(); }
            };
            items.Add(forceHideItem);

            var settingsItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray_settings"));
            settingsItem.Click += (s, args) => { Dispatcher.Invoke(OpenSettings); };
            items.Add(settingsItem);

            return items;
        }

        private void Apps_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isLoadingConfig) return;

            SaveAppsConfig();
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                if (e.NewItems?[0] is DockAppConfig app)
                {
                    if (app.IsPlaceholder)
                    {
                        // Placeholder (drag-drop) — прежняя логика.
                        AnimatedStackPanel.IsAnimationSuspended = true;
                        var generator = AppsItemsControl.ItemContainerGenerator;
                        void PrepPlaceholder(int attempt)
                        {
                            var container = generator.ContainerFromItem(app) as FrameworkElement;
                            if (container != null)
                            {
                                double targetSize = Settings.IconSize + Settings.IconMargin;
                                Dispatcher.InvokeAsync(() => AnimatePlaceholderAddedAsync(container, targetSize), DispatcherPriority.Loaded);
                            }
                            else if (attempt < 10)
                                Dispatcher.BeginInvoke(new Action(() => PrepPlaceholder(attempt + 1)), DispatcherPriority.Loaded);
                            else
                                AnimatedStackPanel.IsAnimationSuspended = false;
                        }
                        PrepPlaceholder(0);
                        PositionDock(skipAnimation: false);
                    }
                    else
                    {
                        // Обычная иконка — свой покадровый аниматор (через модель, без ожидания контейнера).
                        _dockAnimator.AnimateAppear(app, () => PositionDock(skipAnimation: true));
                    }
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove)
            {
                PositionDock(skipAnimation: false);
            }
        }

        private async void AnimateItemAddedAsync(FrameworkElement container, double targetSize)
        {
            bool isVertical = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;
            DependencyProperty lengthProp = isVertical ? FrameworkElement.HeightProperty : FrameworkElement.WidthProperty;

            // Фаза 1: место раздвигается синхронно с панелью (350ms, QuarticEase EaseOut). Иконка невидима.
            var sizeDur = new Duration(TimeSpan.FromMilliseconds(350));
            var sizeEase = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var sizeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, targetSize, sizeDur) { EasingFunction = sizeEase };

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            sizeAnim.Completed += (s, e) => tcs.TrySetResult(true);
            container.BeginAnimation(lengthProp, sizeAnim);
            await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(400));

            // Фаза 2: иконка проявляется в готовом месте.
            var opAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1.0, new Duration(TimeSpan.FromMilliseconds(200)))
            { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };
            var tcs2 = new System.Threading.Tasks.TaskCompletionSource<bool>();
            opAnim.Completed += (s, e) => tcs2.TrySetResult(true);
            container.BeginAnimation(UIElement.OpacityProperty, opAnim);
            await System.Threading.Tasks.Task.WhenAny(tcs2.Task, System.Threading.Tasks.Task.Delay(250));

            AnimatedStackPanel.IsAnimationSuspended = false;
            container.BeginAnimation(lengthProp, null);
            if (isVertical) container.ClearValue(FrameworkElement.HeightProperty);
            else container.ClearValue(FrameworkElement.WidthProperty);
        }

        private async void AnimatePlaceholderAddedAsync(FrameworkElement container, double targetSize)
        {
            bool isVertical = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;
            DependencyProperty lengthProp = isVertical ? FrameworkElement.HeightProperty : FrameworkElement.WidthProperty;

            var duration = new Duration(TimeSpan.FromMilliseconds(400));
            var sizeEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
            var sizeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, targetSize, duration) { EasingFunction = sizeEase };

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            sizeAnim.Completed += (s, e) => tcs.TrySetResult(true);

            container.BeginAnimation(lengthProp, sizeAnim);

            await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(450));
            AnimatedStackPanel.IsAnimationSuspended = false;

            container.BeginAnimation(lengthProp, null);

            if (isVertical) container.Height = targetSize;
            else container.Width = targetSize;
        }

        private void OnIconMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { _dragSource = null; return; }
            if (!_isDragging && ReferenceEquals(_dragSource, sender))
            {
                Point currentPos = e.GetPosition(this);
                Vector diff = _dragStartPoint - currentPos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is Button btn && btn.DataContext is DockAppConfig app)
                    {
                        _isDragging = true;
                        btn.Opacity = 0.3;

                        _draggedApp = app;

                        CreateDragGhost(app);

                        var dragData = new DataObject();

                        // UWP (shell:AppsFolder\AUMID): отдаём нативный shell-объект — проводник создаст ярлык сам.
                        var shellObj = !string.IsNullOrEmpty(app.Path) && app.Path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase)
                            ? PlankWin.Win32Helper.GetShellDataObject(app.Path) : null;

                        if (shellObj != null)
                        {
                            dragData = new DataObject(shellObj);
                        }
                        else if (!string.IsNullOrEmpty(app.Path) && System.IO.File.Exists(app.Path))
                        {
                            var fileList = new System.Collections.Specialized.StringCollection();
                            string safeDropPath = CreateTempShortcut(app.Path, app.Name);
                            fileList.Add(safeDropPath);
                            dragData.SetFileDropList(fileList);
                        }
                        else
                        {
                            dragData.SetData(DataFormats.StringFormat, app.Name);
                        }

                        DragDropEffects dndResult = DragDropEffects.None;
                        try
                        {
                            dndResult = DragDrop.DoDragDrop(btn, dragData, DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
                        }
                        catch { }
                        finally
                        {
                            btn.Opacity = 1.0;
                            _isDragging = false;
                            _dragSource = null;
                            _draggedApp = null;
                            CleanupExternalDrag();
                        }

                        // Вынесли за док и проводник принял (создал ярлык) -> убираем кнопку с панели.
                        // Отмена (Esc) или бросок туда, где не принимают, даёт None — кнопка остаётся.
                        bool outside = IsCursorOutsideDock();
                        if (outside && dndResult != DragDropEffects.None && !app.IsFavorites) _ = RemoveDraggedOutAsync(app);
                    }
                }
            }
        }

        // Родитель-fallback: если процесс с таким exe запущен, берём его родительский процесс,
        // и если тот лежит в той же папке-дереве приложения — возвращаем путь родителя.
        private static string ResolveByParent(string path)
        {
            try
            {
                string exeName = System.IO.Path.GetFileName(path);
                IntPtr snap = Win32Helper.CreateToolhelp32Snapshot(Win32Helper.TH32CS_SNAPPROCESS, 0);
                if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return path;
                try
                {
                    var pe = new Win32Helper.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<Win32Helper.PROCESSENTRY32>() };
                    uint parentPid = 0;
                    if (Win32Helper.Process32First(snap, ref pe))
                    {
                        do
                        {
                            if (string.Equals(pe.szExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                            {
                                parentPid = pe.th32ParentProcessID;
                                break;
                            }
                        } while (Win32Helper.Process32Next(snap, ref pe));
                    }
                    if (parentPid != 0)
                    {
                        try
                        {
                            var parent = Process.GetProcessById((int)parentPid);
                            string parentPath = parent.MainModule?.FileName ?? string.Empty;
                            if (!string.IsNullOrEmpty(parentPath))
                            {
                                string appDir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
                                string parentDir = System.IO.Path.GetDirectoryName(parentPath) ?? string.Empty;
                                // родитель выше или на одном уровне в дереве приложения
                                if (!string.IsNullOrEmpty(parentDir) &&
                                    appDir.StartsWith(parentDir, StringComparison.OrdinalIgnoreCase) &&
                                    System.IO.File.Exists(parentPath))
                                    return parentPath;
                            }
                        }
                        catch { }
                    }
                }
                finally { Win32Helper.CloseHandle(snap); }
            }
            catch { }
            return path;
        }

        // Некоторые приложения имеют окно во вспомогательном процессе, а запускать нужно главный
        // exe. Пример: Steam — окно принадлежит steamwebhelper.exe, а стартовать надо steam.exe.
        private static string ResolveLaunchPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                // Явный кейс Steam: окно в steamwebhelper.exe, запускать steam.exe из корня.
                if (path.EndsWith("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = path.IndexOf(@"\Steam\", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        string steamRoot = path.Substring(0, idx + 7);
                        string steamExe = System.IO.Path.Combine(steamRoot, "steam.exe");
                        if (System.IO.File.Exists(steamExe)) return steamExe;
                    }
                }

                // Общая эвристика: exe лежит в служебной подпапке (bin/cef/helper/...) —
                // поднимаемся к корню приложения и берём exe с именем корневой папки.
                var parts = path.Split(System.IO.Path.DirectorySeparatorChar);
                string[] helperSeg = { "bin", "cef", "helper", "helpers", "crashpadhandler", "resources", "app" };
                for (int i = parts.Length - 2; i >= 1; i--)
                {
                    if (Array.IndexOf(helperSeg, parts[i].ToLowerInvariant()) >= 0)
                    {
                        string root = string.Join(System.IO.Path.DirectorySeparatorChar.ToString(), parts, 0, i);
                        string appName = parts[i - 1];
                        string candidate = System.IO.Path.Combine(root, appName + ".exe");
                        if (!candidate.Equals(path, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(candidate))
                            return candidate;
                    }
                }

                // Fallback: если процесс-хелпер запущен — поднимаемся к родителю.
                string byParent = ResolveByParent(path);
                if (!byParent.Equals(path, StringComparison.OrdinalIgnoreCase)) return byParent;
            }
            catch { }
            return path;
        }

        // Пружинистый прыжок иконки при запуске приложения (взлёт + падение с отскоками).
        private void AnimateLaunchBounce(System.Windows.Controls.Button btn)
        {
            if (btn == null) return;
            if (btn.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                btn.RenderTransform = tt;
            }
            var kf = new DoubleAnimationUsingKeyFrames();
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(-16,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            kf.KeyFrames.Add(new EasingDoubleKeyFrame(0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)),
                new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 2, Bounciness = 2 }));
            tt.BeginAnimation(TranslateTransform.YProperty, kf);
        }

        private double _appsScrollOffset = 0;
        private double _crossPad = 40.0;
        private double _appsMaxScroll = 0;
        private bool _appsOverflow = false;
        private bool _appsVertical = true;
        private readonly DockAnimator _dockAnimator = new DockAnimator();
        private double _appsVisibleLen = 0;
        private double _appsPad = 5;

        private void OnAppsWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_appsOverflow) return;
            // Колесо мыши: |delta| кратно 120 (WHEEL_DELTA). Тачпад шлёт мелкий поток (<120).
            if (Math.Abs(e.Delta) >= 120)
            {
                ApplyAppsScroll(e.Delta > 0 ? 40 : -40); // мышь — полный шаг, в любом положении панели
            }
            else if (_appsVertical)
            {
                ApplyAppsScroll(e.Delta * 0.5); // вертикальный тачпад — только вертикальная панель, мягко
            }
            // горизонтальная панель + вертикальный жест тачпада — игнор (горизонталь идёт через WM_MOUSEHWHEEL)
            e.Handled = true;
        }

        // Общий сдвиг ленты приложений (колесо/тач). delta>0 — к началу.
        private void ApplyAppsScroll(double delta)
        {
            if (!_appsOverflow) return;
            _appsScrollOffset = Math.Clamp(_appsScrollOffset + delta, 0, _appsMaxScroll);
            double shift = _appsScrollOffset - _appsMaxScroll; // offset=0 -> показан конец
            if (_appsVertical) AppsScrollTransform.Y = shift;
            else AppsScrollTransform.X = shift;
            UpdateChevrons();
        }

        // Тач/трекпад-свайп по ленте (2 пальца) при переполнении.
        private void OnAppsManipulation(object sender, System.Windows.Input.ManipulationDeltaEventArgs e)
        {
            if (!_appsOverflow) return;
            // Скролл по оси панели: горизонтальную — горизонтальным свайпом, вертикальную — вертикальным.
            double dx = e.DeltaManipulation.Translation.X;
            double dy = e.DeltaManipulation.Translation.Y;
            double primary = _appsVertical ? dy : dx;
            double secondary = _appsVertical ? dx : dy;
            // Если по оси панели почти нет движения, используем другую ось (трекпад шлёт одну ось сильнее).
            double d = Math.Abs(primary) >= 0.5 ? primary : secondary;
            ApplyAppsScroll(d);
            e.Handled = true;
        }

        private void UpdateAppsClip()
        {
            if (!_appsOverflow) { AppsScrollHost.Clip = null; return; }
            double pad = Settings.EffectiveIconSize;
            double pp = _appsPad;
            AppsScrollHost.Clip = _appsVertical
                ? new System.Windows.Media.RectangleGeometry(new Rect(-pad, pp, Settings.Thickness + 2 * pad, _appsVisibleLen))
                : new System.Windows.Media.RectangleGeometry(new Rect(pp, -pad, _appsVisibleLen, Settings.Thickness + 2 * pad));
        }

        // Сохранить позицию избранного, чтобы она не терялась при перезапуске.
        private void UpdateFavoritesIndex()
        {
            bool changed = false;
            var fav = Apps.FirstOrDefault(a => a.IsFavorites && !a.IsFavFiles);
            if (fav != null && Apps.IndexOf(fav) is int fi && fi >= 0 && Settings.FavoritesIndex != fi) { Settings.FavoritesIndex = fi; changed = true; }
            var ff = Apps.FirstOrDefault(a => a.IsFavFiles);
            if (ff != null && Apps.IndexOf(ff) is int fj && fj >= 0 && Settings.FavFilesIndex != fj) { Settings.FavFilesIndex = fj; changed = true; }
            if (changed) PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
        }

        // Запуск закреплённого PWA: chrome.exe --app-id=<id> --profile-directory=<profile>.
        private bool TryGetPwaLaunch(string path, out string fileName, out string arguments)
        {
            fileName = string.Empty; arguments = string.Empty;
            if (string.IsNullOrEmpty(path) || !path.StartsWith("pwa:", StringComparison.OrdinalIgnoreCase)) return false;
            string body = path.Substring(4);
            int at = body.LastIndexOf('@');
            string profile = at >= 0 ? body.Substring(at + 1) : "Default";
            string rest = at >= 0 ? body.Substring(0, at) : body;
            int colon = rest.IndexOf(':');
            string browser = colon >= 0 ? rest.Substring(0, colon) : "chrome";
            string appId = colon >= 0 ? rest.Substring(colon + 1) : rest;
            fileName = FindBrowserExe(browser);
            if (string.IsNullOrEmpty(fileName)) return false;
            arguments = $"--profile-directory={profile} --app-id={appId}";
            return true;
        }

        private static string FindBrowserExe(string browser)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = browser.Equals("edge", StringComparison.OrdinalIgnoreCase)
                ? new[] {
                    System.IO.Path.Combine(pfx, "Microsoft", "Edge", "Application", "msedge.exe"),
                    System.IO.Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe") }
                : new[] {
                    System.IO.Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                    System.IO.Path.Combine(pfx, "Google", "Chrome", "Application", "chrome.exe"),
                    System.IO.Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe") };
            foreach (var c in candidates) if (System.IO.File.Exists(c)) return c;

            // Fallback: путь из реестра App Paths (нестандартная установка).
            string exe = browser.Equals("edge", StringComparison.OrdinalIgnoreCase) ? "msedge.exe" : "chrome.exe";
            string regPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe;
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var k = root.OpenSubKey(regPath);
                    if (k?.GetValue(null) is string p && System.IO.File.Exists(p)) return p;
                }
                catch { }
            }
            return string.Empty;
        }

        private const double ChevronInset = 7; // отступ шеврона от края панели

        private void UpdateChevrons()
        {
            if (!_appsOverflow)
            {
                ChevronStart.Visibility = Visibility.Collapsed;
                ChevronEnd.Visibility = Visibility.Collapsed;
                AppsScrollHost.OpacityMask = null;
                return;
            }
            var brush = new SmartContrastConverter().Convert(Settings.BackgroundColor, typeof(Brush), "Dot", System.Globalization.CultureInfo.InvariantCulture) as Brush;
            if (brush == null) brush = Brushes.Gray;

            ChevronStart.Stroke = brush;
            ChevronEnd.Stroke = brush;
            if (_appsVertical)
            {
                ChevronStart.Data = System.Windows.Media.Geometry.Parse("M 0,7 L 8,0 L 16,7");
                ChevronStart.HorizontalAlignment = HorizontalAlignment.Center;
                ChevronStart.VerticalAlignment = VerticalAlignment.Top;
                ChevronStart.Margin = new Thickness(0, ChevronInset, 0, 0);
                ChevronEnd.Data = System.Windows.Media.Geometry.Parse("M 0,0 L 8,7 L 16,0");
                ChevronEnd.HorizontalAlignment = HorizontalAlignment.Center;
                ChevronEnd.VerticalAlignment = VerticalAlignment.Bottom;
                ChevronEnd.Margin = new Thickness(0, 0, 0, ChevronInset);
            }
            else
            {
                ChevronStart.Data = System.Windows.Media.Geometry.Parse("M 7,0 L 0,8 L 7,16");
                ChevronStart.HorizontalAlignment = HorizontalAlignment.Left;
                ChevronStart.VerticalAlignment = VerticalAlignment.Center;
                ChevronStart.Margin = new Thickness(ChevronInset, 0, 0, 0);
                ChevronEnd.Data = System.Windows.Media.Geometry.Parse("M 0,0 L 8,8 L 0,16");
                ChevronEnd.HorizontalAlignment = HorizontalAlignment.Right;
                ChevronEnd.VerticalAlignment = VerticalAlignment.Center;
                ChevronEnd.Margin = new Thickness(0, 0, ChevronInset, 0);
            }
            bool fadeStart = _appsScrollOffset < _appsMaxScroll - 1;
            bool fadeEnd = _appsScrollOffset > 1;
            ChevronStart.Visibility = fadeStart ? Visibility.Visible : Visibility.Collapsed;
            ChevronEnd.Visibility = fadeEnd ? Visibility.Visible : Visibility.Collapsed;
            UpdateAppsFade(fadeStart, fadeEnd);
        }

        // Плавное затухание иконок к краю видимой области — только с тех сторон, где есть что прокручивать
        // (там же, где шеврон). Маска в абсолютных координатах полосы, совпадает с её клипом.
        private void UpdateAppsFade(bool fadeStart, bool fadeEnd)
        {
            double len = _appsVisibleLen;
            if (len <= 0) { AppsScrollHost.OpacityMask = null; return; }
            double fade = Math.Min(Settings.EffectiveIconSize * 0.75, len / 4); // длина зоны затухания
            double f = fade / len;
            double a0 = _appsPad, a1 = _appsPad + len;

            var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
            if (_appsVertical) { g.StartPoint = new Point(0, a0); g.EndPoint = new Point(0, a1); }
            else { g.StartPoint = new Point(a0, 0); g.EndPoint = new Point(a1, 0); }

            g.GradientStops.Add(new GradientStop(fadeStart ? Colors.Transparent : Colors.Black, 0));
            g.GradientStops.Add(new GradientStop(Colors.Black, fadeStart ? f : 0));
            g.GradientStops.Add(new GradientStop(Colors.Black, fadeEnd ? 1 - f : 1));
            g.GradientStops.Add(new GradientStop(fadeEnd ? Colors.Transparent : Colors.Black, 1));
            g.Freeze();
            AppsScrollHost.OpacityMask = g;

            // Клип: со стороны, где прокручивать некуда, отпускаем до края панели — иначе подсветка/фон
            // крайней иконки срезается по границе зоны шеврона.
            double s0 = fadeStart ? a0 : 0;
            double s1 = fadeEnd ? a1 : a1 + _appsPad;
            double cross = Settings.EffectiveIconSize;
            AppsScrollHost.Clip = _appsVertical
                ? new System.Windows.Media.RectangleGeometry(new Rect(-cross, s0, Settings.Thickness + 2 * cross, s1 - s0))
                : new System.Windows.Media.RectangleGeometry(new Rect(s0, -cross, s1 - s0, Settings.Thickness + 2 * cross));
        }

        private void OnAppButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DockAppConfig app) return;
            if (app.IsPlaceholder) return;

            // Кнопка "Избранное" — панель откроется на следующем шаге (пока заглушка).
            if (app.IsFavorites) { if (Settings.FavoritesOpenMode == FavoritesOpenMode.Click) ToggleFavoritesPanel(btn); return; }

            // Клики с модификаторами — как на таскбаре Windows: Shift — новое окно, Ctrl+Shift — от администратора;
            // своё: Alt — папка программы.
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.Shift) { HidePreview(); LaunchNewInstance(app, admin: false, bounce: btn); return; }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift)) { HidePreview(); LaunchNewInstance(app, admin: true, bounce: btn); return; }
            if (mods == ModifierKeys.Alt) { HidePreview(); OpenAppLocation(app); return; }

            // Клик по кнопке = намеренное переключение окна: закрываем превью и
            // отменяем возврат прежнего окна (иначе Aero Peek перекроет активированное).
            _peekPrevForeground = IntPtr.Zero;
            _peekRestoredIconic = IntPtr.Zero;
            HidePreview();

            try
            {
                if (app.IsRunning && app.WindowHandles.Count > 0)
                {
                    IntPtr fgHwnd = PlankWin.Win32Helper.GetForegroundWindow();

                    if (app.WindowHandles.Contains(fgHwnd))
                    {
                        if (app.WindowHandles.Count == 1)
                        {
                            PlankWin.Win32Helper.ShowWindow(app.WindowHandles[0], 6); // SW_MINIMIZE
                        }
                        else
                        {
                            int currentIndex = app.WindowHandles.IndexOf(fgHwnd);
                            int nextIndex = (currentIndex + 1) % app.WindowHandles.Count;
                            IntPtr nextHwnd = app.WindowHandles[nextIndex];

                            if (PlankWin.Win32Helper.IsIconic(nextHwnd))
                                PlankWin.Win32Helper.ShowWindow(nextHwnd, 9); // SW_RESTORE

                            PlankWin.Win32Helper.SetForegroundWindow(nextHwnd);
                        }
                    }
                    else
                    {
                        // Приложение неактивно -> активируем ПОСЛЕДНЕЕ активное окно группы
                        // (app.WindowHandle обновляется в OnShellWindowActivated при любой
                        // активации окна), а не первое назначенное. Fallback на [0], если
                        // хэндл почему-то невалиден.
                        IntPtr targetHwnd = (app.WindowHandle != IntPtr.Zero && app.WindowHandles.Contains(app.WindowHandle))
                                            ? app.WindowHandle
                                            : app.WindowHandles[0];
                        if (PlankWin.Win32Helper.IsIconic(targetHwnd))
                        {
                            PlankWin.Win32Helper.ShowWindow(targetHwnd, 9);
                        }
                        PlankWin.Win32Helper.SetForegroundWindow(targetHwnd);
                    }
                }
                else
                {
                    string launchPath; string launchArgs;
                    if (TryGetPwaLaunch(app.Path, out string _pwaFile, out string _pwaArgs)) { launchPath = _pwaFile; launchArgs = _pwaArgs; }
                    else if (!string.IsNullOrEmpty(app.LaunchUri)) { launchPath = app.LaunchUri; launchArgs = string.Empty; }
                    else { launchPath = ResolveLaunchPath(app.Path); launchArgs = app.Arguments ?? string.Empty; }
                    var psi = new ProcessStartInfo { FileName = launchPath, Arguments = launchArgs, UseShellExecute = true };
                    // Многие приложения (Steam, игры, портативные) ищут свои файлы относительно
                    // рабочего каталога — задаём его равным папке exe, иначе не запустятся.
                    if (!app.IsPackaged && System.IO.File.Exists(launchPath))
                        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(launchPath) ?? string.Empty;
                    Process.Start(psi);
                    AnimateLaunchBounce(btn); // пружинистый прыжок иконки при запуске
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Loc.T("err_launch")}\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Карусель окон колесом мыши над кнопкой (только для 2+ окон): листаем окна группы,
        // показывая следующее и СВОРАЧИВАЯ предыдущее (одно окно группы на экране за раз).
        // Обычное листание вперёд без сворачивания доступно кликом по кнопке.
        private void OnAppButtonWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DockAppConfig app) return;
            if (app.IsPlaceholder || !app.IsRunning) return;
            if (app.WindowHandles.Count < 2) return;

            e.Handled = true;

            // Текущее окно группы: активное foreground (если из группы), иначе последнее активное.
            IntPtr fg = PlankWin.Win32Helper.GetForegroundWindow();
            int currentIndex = app.WindowHandles.Contains(fg)
                               ? app.WindowHandles.IndexOf(fg)
                               : app.WindowHandles.IndexOf(app.WindowHandle);
            if (currentIndex < 0) currentIndex = 0;

            int count = app.WindowHandles.Count;
            int step = e.Delta > 0 ? -1 : 1; // вверх -> предыдущее, вниз -> следующее
            int nextIndex = ((currentIndex + step) % count + count) % count;

            IntPtr currentHwnd = app.WindowHandles[currentIndex];
            IntPtr nextHwnd = app.WindowHandles[nextIndex];
            if (nextHwnd == currentHwnd) return;

            try
            {
                if (PlankWin.Win32Helper.IsIconic(nextHwnd))
                    PlankWin.Win32Helper.ShowWindow(nextHwnd, 9); // SW_RESTORE
                PlankWin.Win32Helper.SetForegroundWindow(nextHwnd);

                if (currentHwnd != IntPtr.Zero && !PlankWin.Win32Helper.IsIconic(currentHwnd))
                    PlankWin.Win32Helper.ShowWindow(currentHwnd, 6); // SW_MINIMIZE
            }
            catch { }
        }

        private void OnCloseAppClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app && app.IsRunning && app.WindowHandle != IntPtr.Zero)
            {
                // PostMessage, а не SendMessage: SendMessage ЖДЁТ обработки сообщения целевым
                // окном. Если приложение приостановлено (свёрнутый UWP) или показывает диалог
                // «сохранить изменения?», ответа нет — и UI-поток дока зависает намертво.
                // PostMessage кладёт сообщение в очередь окна и сразу возвращается.
                PlankWin.Win32Helper.PostMessage(app.WindowHandle, PlankWin.Win32Helper.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        private void OnCloseAllWindowsClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app && app.IsRunning)
            {
                foreach (var h in app.WindowHandles.ToList())
                {
                    try { PlankWin.Win32Helper.PostMessage(h, PlankWin.Win32Helper.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                }
            }
            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        private void OnPinAppClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app)
            {
                app.IsDynamic = false;
                SaveAppsConfig();
            }

            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        private async void OnUnpinAppClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app)
            {
                app.IsDynamic = true;
                SaveAppsConfig();

                if (!app.IsRunning)
                {
                    app.IsDeleting = true;
                    PositionDock(skipAnimation: false);

                    await AnimateItemContainerAsync(app, targetSize: 0, animateOpacity: true, durationMs: 350);
                    Apps.Remove(app);
                }
            }
            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        private void OnLaunchNewInstanceClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app) LaunchNewInstance(app, admin: false);
            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        // Новый экземпляр приложения (меню, средний клик, Shift+клик) или от администратора (Ctrl+Shift+клик).
        // Учитывает PWA, ярлыки лаунчеров (.url), рабочую папку exe. Для UWP/URI повышение прав неприменимо.
        private void LaunchNewInstance(DockAppConfig app, bool admin, Button? bounce = null)
        {
            try
            {
                string launchPath; string launchArgs;
                if (TryGetPwaLaunch(app.Path, out string _pwaFile, out string _pwaArgs)) { launchPath = _pwaFile; launchArgs = _pwaArgs; }
                else if (!string.IsNullOrEmpty(app.LaunchUri)) { launchPath = app.LaunchUri; launchArgs = string.Empty; }
                else { launchPath = ResolveLaunchPath(app.Path); launchArgs = app.Arguments ?? string.Empty; }
                var psi = new ProcessStartInfo { FileName = launchPath, Arguments = launchArgs, UseShellExecute = true };
                if (!app.IsPackaged && System.IO.File.Exists(launchPath))
                {
                    psi.WorkingDirectory = System.IO.Path.GetDirectoryName(launchPath) ?? string.Empty;
                    if (admin) psi.Verb = "runas";
                }
                Process.Start(psi);
                if (bounce != null) AnimateLaunchBounce(bounce);
                if (Settings.AutoHide) HideDock();
            }
            catch (System.ComponentModel.Win32Exception w32) when (w32.NativeErrorCode == 1223)
            {
                // Пользователь отменил запрос UAC — тихо игнорируем.
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Loc.T("err_launch")}\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Alt+клик: папка программы в Проводнике с выделенным exe (у игр из лаунчеров — папка установки).
        private void OpenAppLocation(DockAppConfig app)
        {
            try
            {
                if (!string.IsNullOrEmpty(app.LaunchUri))
                {
                    if (!string.IsNullOrEmpty(app.InstallDir) && System.IO.Directory.Exists(app.InstallDir))
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + app.InstallDir + "\"") { UseShellExecute = true });
                    return;
                }
                if (app.IsPackaged || TryGetPwaLaunch(app.Path, out _, out _)) return;
                string exe = ResolveLaunchPath(app.Path);
                if (System.IO.File.Exists(exe))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + exe + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        // Запуск НОВОГО экземпляра приложения с повышенными правами (UAC). Работает для
        // обычных exe; для UWP пункт скрыт (RunAsAdminVisibility). Отмену UAC глушим.
        private void OnRunAsAdminClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app) LaunchNewInstance(app, admin: true);
            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        // Свернуть все окна группы (или единственное окно приложения).
        private void OnMinimizeWindowsClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app)
            {
                foreach (var hwnd in app.WindowHandles)
                {
                    if (hwnd != IntPtr.Zero && PlankWin.Win32Helper.IsWindow(hwnd)
                        && !PlankWin.Win32Helper.IsIconic(hwnd))
                    {
                        PlankWin.Win32Helper.ShowWindow(hwnd, 6); // SW_MINIMIZE
                    }
                }
            }

            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        private bool HasValidExecutable(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] files)
                {
                    foreach (string file in files)
                    {
                        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                            || file.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        // Курсор вне прямоугольника окна дока (физические пиксели).
        private bool IsCursorOutsideDock()
        {
            if (_mainHwnd == IntPtr.Zero) return false;
            PlankWin.Win32Helper.GetCursorPos(out var pt);
            if (!PlankWin.Win32Helper.GetWindowRect(_mainHwnd, out var wr)) return false;
            return pt.X < wr.Left || pt.X > wr.Right || pt.Y < wr.Top || pt.Y > wr.Bottom;
        }

        // Кнопка, вынесенная за док: запущенное приложение открепляется (остаётся, пока живо),
        // незапущенное уезжает с анимацией.
        private async System.Threading.Tasks.Task RemoveDraggedOutAsync(DockAppConfig app)
        {
            app.IsDynamic = true;
            SaveAppsConfig();
            if (app.IsRunning) return;

            app.IsDeleting = true;
            PositionDock(skipAnimation: false);
            await AnimateItemContainerAsync(app, targetSize: 0, animateOpacity: true, durationMs: 350);
            Apps.Remove(app);
            SaveAppsConfig();
        }

        private void OnIconMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            _isDragging = false;
            _dragSource = _openContextMenusCount > 0 ? null : sender;
        }

        private void CreateDragGhost(DockAppConfig app)
        {
            var converter = new AppIconConverter();
            var imageSource = converter.Convert(app, typeof(System.Windows.Media.ImageSource), null!, System.Globalization.CultureInfo.CurrentCulture) as System.Windows.Media.ImageSource;
            _dragGhost.Show(imageSource, 44);
            double dpi = 1.0;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null) dpi = src.CompositionTarget.TransformToDevice.M11;
            _dragGhost.Update(dpi);
        }

        private void OnIconGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_dragGhost.IsVisible)
            {
                double dpi = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null) dpi = source.CompositionTarget.TransformToDevice.M11;
                _dragGhost.Update(dpi);

                Mouse.SetCursor(Cursors.Arrow);
                e.UseDefaultCursors = false;
                e.Handled = true;
            }
        }

        // Индекс вставки по РЕАЛЬНЫМ позициям кнопок на экране: учитывает прокрутку,
        // масштаб Viewbox при переполнении, полочку и DPI — без геометрической симуляции.
        // exclude — элемент, который двигаем (заглушка или уже закреплённое приложение):
        // результат — индекс в списке БЕЗ него, т.е. годится и для Insert, и для Move.
        private int CalculateTargetDropIndex(DockAppConfig? exclude = null)
        {
            PlankWin.Win32Helper.GetCursorPos(out var pt);
            bool isVertical = Settings.Position == DockPosition.Left || Settings.Position == DockPosition.Right;
            double cursor = isVertical ? pt.Y : pt.X;

            int index = 0;
            foreach (var item in Apps)
            {
                if (item == exclude || item.IsDeleting) continue;
                if (AppsItemsControl.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement fe
                    || !fe.IsVisible || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                    continue;
                Point c = fe.PointToScreen(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2)); // физ. пиксели, как GetCursorPos
                if ((isVertical ? c.Y : c.X) < cursor) index++;
            }
            return index;
        }

        private void UpdateDragGhost(string firstFile)
        {
            if (!_dragGhost.IsVisible && !_isDragging)
            {
                string resolvedPath = firstFile;
                if (firstFile.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string ghostAumid = PlankWin.Win32Helper.GetAumidFromShortcut(firstFile);
                    resolvedPath = !string.IsNullOrEmpty(ghostAumid)
                        ? "shell:AppsFolder\\" + ghostAumid
                        : PlankWin.Win32Helper.ResolveShortcut(firstFile, out _);
                }

                CreateDragGhost(new DockAppConfig
                {
                    Path = resolvedPath,
                    Name = System.IO.Path.GetFileNameWithoutExtension(firstFile),
                    IsPlaceholder = false
                });

                if (_dragGhost.IsVisible)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
                }
            }

            if (_dragGhost.IsVisible)
            {
                double dpi = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null) dpi = source.CompositionTarget.TransformToDevice.M11;
                _dragGhost.Update(dpi, 5, 5);
            }
        }

        private void ProcessExternalDrag(DragEventArgs e)
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;

            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            string firstFile = files[0];

            UpdateDragGhost(firstFile);

            string checkPath = firstFile;
            if (firstFile.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // UWP-ярлык: сначала AUMID (у ярлыков, созданных Проводником из «Приложений»,
                // ResolveShortcut может вернуть мусорный путь), затем обычный путь.
                string aumid = PlankWin.Win32Helper.GetAumidFromShortcut(firstFile);
                checkPath = !string.IsNullOrEmpty(aumid)
                    ? "shell:AppsFolder\\" + aumid
                    : PlankWin.Win32Helper.ResolveShortcut(firstFile, out _);
            }
            else if (firstFile.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                var ur = UrlShortcutResolver.Resolve(firstFile);
                if (ur != null && !string.IsNullOrEmpty(ur.ExePath)) checkPath = ur.ExePath;
            }
            var existingApp = System.Linq.Enumerable.FirstOrDefault(Apps, a => a != null && !a.IsPlaceholder && string.Equals(a.Path, checkPath, StringComparison.OrdinalIgnoreCase));

            int targetIndex = CalculateTargetDropIndex(existingApp ?? _placeholderApp);

            if (existingApp != null)
            {
                int sourceIndex = Apps.IndexOf(existingApp);
                int safeTarget = Math.Clamp(targetIndex, 0, Math.Max(0, Apps.Count - 1));

                if (sourceIndex >= 0 && sourceIndex != safeTarget && (DateTime.Now - _lastSwapTime).TotalMilliseconds > 200)
                {
                    Apps.Move(sourceIndex, safeTarget);
                    _lastSwapTime = DateTime.Now;
                }
                return;
            }

            if (!_isExternalDragActive)
            {
                _isExternalDragActive = true;
                StartDragWatchdog();
                _placeholderApp = new DockAppConfig { Name = string.Empty, Path = checkPath, IsDynamic = false, IsRunning = false, IsPlaceholder = true };

                int safeInsert = Math.Clamp(targetIndex, 0, Apps.Count);
                Apps.Insert(safeInsert, _placeholderApp);

                PositionDock(skipAnimation: false);
                _lastSwapTime = DateTime.Now;
            }
            else if (_placeholderApp != null)
            {
                int pIndex = Apps.IndexOf(_placeholderApp);
                int safeTarget = Math.Clamp(targetIndex, 0, Math.Max(0, Apps.Count - 1));
                if (pIndex >= 0 && pIndex != safeTarget && (DateTime.Now - _lastSwapTime).TotalMilliseconds > 200)
                {
                    Apps.Move(pIndex, safeTarget);
                    _lastSwapTime = DateTime.Now;
                }
            }
        }

        private void OnPanelDrop(object sender, DragEventArgs e)
        {
            if (_isDragging)
            {
                SaveAppsConfig();
                e.Handled = true;
                return;
            }

            HandleExternalDrop(sender, e);
        }

        private void HandleExternalDrop(object sender, DragEventArgs e)
        {

            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                bool needsSave = false;
                string file = files[0];

                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                {
                    string resolvedPath = file;
                    string resolvedArgs = string.Empty;
                    string appName = System.IO.Path.GetFileNameWithoutExtension(file);
                    string? urlLaunch = null, urlDir = null;

                    // .url лаунчера (Steam/Epic): путь = exe игры (склейка с процессом), запуск = URI.
                    if (file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        var ur = UrlShortcutResolver.Resolve(file);
                        if (ur == null) { CleanupExternalDrag(); e.Handled = true; return; }
                        urlLaunch = ur.Uri;
                        urlDir = string.IsNullOrEmpty(ur.InstallDir) ? null : ur.InstallDir;
                        if (!string.IsNullOrEmpty(ur.ExePath)) resolvedPath = ur.ExePath;
                    }

                    if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        // UWP-ярлык (Калькулятор, Media Player и т.п.): сначала AUMID — путь shell:AppsFolder\AUMID
                        // понимают и запуск, и конвертер иконок. Ярлык, созданный Проводником из «Приложений»
                        // (наш drag-out), может отдавать через ResolveShortcut посторонний путь, поэтому AUMID — первым.
                        string aumid = PlankWin.Win32Helper.GetAumidFromShortcut(file);
                        if (!string.IsNullOrEmpty(aumid))
                        {
                            resolvedPath = "shell:AppsFolder\\" + aumid;
                            resolvedArgs = string.Empty;
                        }
                        else
                        {
                            resolvedPath = PlankWin.Win32Helper.ResolveShortcut(file, out resolvedArgs);
                        }
                        Debug.WriteLine($"[LNK] {file} -> aumid='{aumid}' path='{resolvedPath}'");

                        // Имя без системного хвоста « - Shortcut» / « - Ярлык».
                        appName = System.Text.RegularExpressions.Regex.Replace(appName, @"\s*[-–—]\s*(Shortcut|Ярлык)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    }

                    // Отклоняем то, что не удалось разрешить ни в путь к exe, ни в AUMID
                    // (не создаём пустую иконку, которая потом падает с "file name has not been provided").
                    if (string.IsNullOrEmpty(resolvedPath))
                    {
                        CleanupExternalDrag();
                        e.Handled = true;
                        return;
                    }

                    bool alreadyExists = Apps.Any(a => !a.IsPlaceholder && string.Equals(a.Path, resolvedPath, StringComparison.OrdinalIgnoreCase));

                    if (!alreadyExists)
                    {
                        if (_placeholderApp != null)
                        {
                            var targetApp = _placeholderApp;
                            _placeholderApp = null;
                            _isExternalDragActive = false;

                            int pIndex = Apps.IndexOf(targetApp);
                            if (pIndex >= 0)
                            {
                                var newApp = new DockAppConfig
                                {
                                    Name = appName,
                                    Path = resolvedPath,
                                    Arguments = resolvedArgs,
                                    LaunchUri = urlLaunch,
                                    InstallDir = urlDir,
                                    IsDynamic = false,
                                    IsRunning = false,
                                    IsPlaceholder = false
                                };

                                Apps[pIndex] = newApp;

                                AppsItemsControl.UpdateLayout();
                                var container = AppsItemsControl.ItemContainerGenerator.ContainerFromItem(newApp) as FrameworkElement;
                                if (container != null)
                                {
                                    container.Opacity = 0;
                                    var opEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                                    var opAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(400))) { EasingFunction = opEase };
                                    container.BeginAnimation(UIElement.OpacityProperty, opAnim);
                                }
                            }
                            needsSave = true;
                        }
                        else
                        {
                            var newApp = new DockAppConfig { Name = appName, Path = resolvedPath, Arguments = resolvedArgs, LaunchUri = urlLaunch, InstallDir = urlDir, IsDynamic = false, IsRunning = false };
                            Apps.Add(newApp);
                            needsSave = true;
                        }
                    }
                }

                if (needsSave) SaveAppsConfig();
                PositionDock(skipAnimation: false);
            }

            CleanupExternalDrag();
            e.Handled = true;
        }

        private void CleanupExternalDrag()
        {
            StopDragWatchdog();

            _dragGhost.Close();

            if (_placeholderApp != null)
            {
                var appToRemove = _placeholderApp;
                _placeholderApp = null;
                _isExternalDragActive = false;

                appToRemove.IsDeleting = true;
                PositionDock(skipAnimation: false);

                Dispatcher.InvokeAsync(async () =>
                {
                    await AnimateItemContainerAsync(appToRemove, targetSize: 0, animateOpacity: false, durationMs: 400);
                    Apps.Remove(appToRemove);
                });
            }
            else if (_isExternalDragActive)
            {
                _isExternalDragActive = false;
                PositionDock(skipAnimation: false);
            }
        }

        // Запускает сторожевой таймер на время внешнего перетаскивания.
        private void StartDragWatchdog()
        {
            if (_dragWatchdogTimer == null)
            {
                _dragWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _dragWatchdogTimer.Tick += DragWatchdog_Tick;
            }
            _dragWatchdogTimer.Start();
        }

        private void StopDragWatchdog()
        {
            _dragWatchdogTimer?.Stop();
        }

        private void DragWatchdog_Tick(object? sender, EventArgs e)
        {
            // Если внешний drag уже не активен — таймеру делать нечего.
            if (!_isExternalDragActive && _placeholderApp == null)
            {
                StopDragWatchdog();
                return;
            }

            // Левая кнопка мыши отпущена (старший бит == 0)?
            // Значит перетаскивание завершилось — возможно, дропом ВНЕ дока,
            // и наш обработчик Drop не сработал. Подстраховываемся и чистим состояние.
            bool leftButtonDown = (PlankWin.Win32Helper.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (!leftButtonDown)
            {
                CleanupExternalDrag();
                return;
            }

            // Курсор вышел за пределы ОКНА дока, но кнопка ещё нажата?
            // Значит пользователь "передумал" и тащит ярлык обратно на рабочий стол.
            // Границей служит прямоугольник всего окна (а не видимого стекла) — пока курсор
            // над окном, система не показывает запретный курсор, призрак живёт; как только
            // курсор покинул окно целиком — убираем НАШИ артефакты (призрак, placeholder),
            // оставляя только системное перетаскивание Windows (нативное таскание по столу).
            PlankWin.Win32Helper.GetCursorPos(out var pt);
            double dpiX = 1.0, dpiY = 1.0;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null) { dpiX = src.CompositionTarget.TransformToDevice.M11; dpiY = src.CompositionTarget.TransformToDevice.M22; }
            double sx = pt.X / dpiX, sy = pt.Y / dpiY;

            // Прямоугольник окна дока в WPF-координатах.
            double winLeft = this.Left;
            double winTop = this.Top;
            double winRight = this.Left + this.ActualWidth;
            double winBottom = this.Top + this.ActualHeight;

            if (sx < winLeft || sx > winRight || sy < winTop || sy > winBottom)
            {
                CleanupExternalDrag();
            }
        }

        private void OnPanelDragLeave(object sender, DragEventArgs e)
        {
            if (_isDragging) return;

            PlankWin.Win32Helper.GetCursorPos(out var pt);
            double dpiX = 1.0, dpiY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null) { dpiX = source.CompositionTarget.TransformToDevice.M11; dpiY = source.CompositionTarget.TransformToDevice.M22; }
            double screenX = pt.X / dpiX; double screenY = pt.Y / dpiY;

            int activeCount = Apps.Count(a => !a.IsDeleting);
            int simulatedCount = _placeholderApp == null ? activeCount + 1 : activeCount;

            Rect futureGlass = SimulateGlassRect(simulatedCount);

            if (screenX < futureGlass.Left - 40 || screenX > futureGlass.Right + 40 ||
                screenY < futureGlass.Top - 40 || screenY > futureGlass.Bottom + 40)
            {
                CleanupExternalDrag();
            }
        }

        private void OnIconDragOver(object sender, DragEventArgs e)
        {
            if (_isDragging && _draggedApp != null)
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;

                var sourceApp = _draggedApp;

                if (sender is Button btn && btn.DataContext is DockAppConfig targetApp && sourceApp != targetApp)
                {
                    int sourceIndex = Apps.IndexOf(sourceApp);
                    int targetIndex = Apps.IndexOf(targetApp);
                    if (sourceIndex >= 0 && targetIndex >= 0)
                    {
                        if ((DateTime.Now - _lastSwapTime).TotalMilliseconds < 200) return;
                        Apps.Move(sourceIndex, targetIndex);
                        _lastSwapTime = DateTime.Now;
                        UpdateFavoritesIndex();
                    }
                }
            }
            else if (HasValidExecutable(e.Data))
            {
                ProcessExternalDrag(e);
            }
        }

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            // Гасим системный 🚫 в «прозрачной» области окна за пределами видимой панели.
            // Там нет принимающего элемента, поэтому эффект остаётся None, и Windows рисует
            // «нельзя бросить» прямо в момент выноса ярлыка наружу. Событие сюда всплывает
            // только если его не обработал никто из детей, так что внутреннюю логику
            // (перестановку иконок, внешний drop) это не задевает.
            if (_isDragging)
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            // Тот же случай для перетаскивания ИЗВНЕ (ярлык из проводника): затащили на док,
            // затем, не отпуская, повели обратно. Курсор идёт по прозрачной части нашего окна,
            // где принимающего элемента нет, и Windows рисует 🚫. Показываем допустимый эффект.
            if (HasValidExecutable(e.Data))
            {
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
            }
        }

        private void OnPanelDragOver(object sender, DragEventArgs e)
        {
            if (_isDragging)
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (HasValidExecutable(e.Data))
            {
                ProcessExternalDrag(e);
            }
        }

        private void OnIconDragEnter(object sender, DragEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DockAppConfig app && app.IsFavorites
                && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Звезда и кнопка файлов принимают и папки, и файлы (раскладываем по своим спискам).
                bool any = e.Data.GetData(DataFormats.FileDrop) is string[] files
                    && files.Any(f => System.IO.Directory.Exists(f) || System.IO.File.Exists(f));
                if (any)
                    btn.Effect = new DropShadowEffect { Color = Colors.Gold, BlurRadius = 22, ShadowDepth = 0, Opacity = 1.0 };
            }
        }

        private void OnIconDragLeave(object sender, DragEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DockAppConfig app && app.IsFavorites)
                btn.Effect = null;
        }

        private void OnIconDrop(object sender, DragEventArgs e)
        {
            if (_isDragging && _draggedApp != null)
            {
                SaveAppsConfig();
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Папка, брошенная на звезду "Избранное" -> добавляем в список избранных.
                if (sender is FrameworkElement fe && fe.DataContext is DockAppConfig favApp && favApp.IsFavorites)
                {
                    if (fe is Button fb) fb.Effect = null;
                    if (e.Data.GetData(DataFormats.FileDrop) is string[] favFiles)
                        AddToFavoritesRouted(favFiles); // папки -> избранные папки, файлы -> избранные файлы
                    e.Handled = true;
                    return;
                }
                HandleExternalDrop(sender, e);
            }
        }

        private void OnIconMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                if (sender is Button btn && btn.DataContext is DockAppConfig app && !app.IsFavorites && !app.IsPlaceholder)
                {
                    LaunchNewInstance(app, admin: false, bounce: btn);
                    e.Handled = true;
                }
            }
        }

        // Открыть окно настроек (из Ctrl+Shift+ПКМ или из меню трея).
        private void OpenSettings()
        {
            if (System.Windows.Application.Current.Windows.OfType<SettingsWindow>().Any()) return;
            var settingsWin = new SettingsWindow(Settings, _profileName);
            settingsWin.Owner = this;
            settingsWin.ShowDialog();
        }

        private void Window_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
            {
                OpenSettings();
            }
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_activeIconMenu != null && _activeIconMenu.IsOpen)
            {
                _activeIconMenu.IsOpen = false;
            }
        }

        private void MainBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            HidePreview(); // ПКМ закрывает превью (приоритет меню, как в Windows)

            _contextMenuOpen = true;
            if (sender is FrameworkElement _cmEl && _cmEl.ContextMenu != null)
            {
                var _cm = _cmEl.ContextMenu;
                System.Windows.RoutedEventHandler? _h = null;
                _h = (s, ev) => { _contextMenuOpen = false; _cm.Closed -= _h; };
                _cm.Closed += _h;
            }
            else Dispatcher.BeginInvoke(new Action(() => _contextMenuOpen = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (e.OriginalSource is FrameworkElement fe && fe.DataContext is DockAppConfig) return;
                e.Handled = true;
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (MainBorder.ContextMenu != null) MainBorder.ContextMenu.IsOpen = false;
            if (_activeIconMenu != null) { _activeIconMenu.IsOpen = false; _activeIconMenu = null; }
            _openContextMenusCount = 0;
        }

        private static bool IsAutostartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("PlankWin") != null;
            }
            catch { return false; }
        }

        private void ApplyAutostart(bool enable)
        {
            try
            {
                string appName = "PlankWin";
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";

                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    if (enable) key.SetValue(appName, $"\"{exePath}\"");
                    else key.DeleteValue(appName, false);
                }
            }
            catch { }
        }

        private string CreateTempShortcut(string targetPath, string appName)
        {
            try
            {
                string tempDir = System.IO.Path.GetTempPath();
                string safeName = string.Join("_", appName.Split(System.IO.Path.GetInvalidFileNameChars()));
                string lnkPath = System.IO.Path.Combine(tempDir, safeName + ".lnk");

                PlankWin.Win32Helper.IShellLink link = (PlankWin.Win32Helper.IShellLink)new PlankWin.Win32Helper.ShellLink();
                link.SetPath(targetPath);

                string? dir = System.IO.Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    link.SetWorkingDirectory(dir);
                }

                System.Runtime.InteropServices.ComTypes.IPersistFile file = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
                file.Save(lnkPath, false);

                return lnkPath;
            }
            catch { }
            return targetPath;
        }

        private const int WH_MOUSE_LL = 14;
        private PlankWin.Win32Helper.LowLevelMouseProc? _mouseProc;
        private IntPtr _mouseHookID = IntPtr.Zero;

        // Глобальный hook колеса: скролл ленты работает даже над прозрачным выступом иконок
        // (где hit-test окна не срабатывает и WM_MOUSEWHEEL иначе уходит окну под панелью).
        private IntPtr _mainHwnd = IntPtr.Zero;
        private PlankWin.Win32Helper.LowLevelMouseProc? _scrollProc;
        private IntPtr _scrollHookID = IntPtr.Zero;

        private void InstallScrollHook()
        {
            if (_scrollHookID != IntPtr.Zero) return;
            _scrollProc = ScrollHookCallback;
            using (var proc = System.Diagnostics.Process.GetCurrentProcess())
            using (var mod = proc.MainModule!)
                _scrollHookID = PlankWin.Win32Helper.SetWindowsHookEx(WH_MOUSE_LL, _scrollProc, PlankWin.Win32Helper.GetModuleHandle(mod.ModuleName), 0);
        }

        private IntPtr ScrollHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)0x020A && _appsOverflow && _mainHwnd != IntPtr.Zero) // WM_MOUSEWHEEL
            {
                try
                {
                    var hs = System.Runtime.InteropServices.Marshal.PtrToStructure<PlankWin.Win32Helper.MSLLHOOKSTRUCT>(lParam);
                    // Над панелью избранного (она перекрывает прозрачную часть окна дока) колесо — её прокрутке.
                    if (_favHost != null && _favHost.IsVisible
                        && PlankWin.Win32Helper.GetWindowRect(new WindowInteropHelper(_favHost).Handle, out var fr)
                        && hs.pt.X >= fr.Left && hs.pt.X < fr.Right && hs.pt.Y >= fr.Top && hs.pt.Y < fr.Bottom)
                        return PlankWin.Win32Helper.CallNextHookEx(_scrollHookID, nCode, wParam, lParam);
                    if (PlankWin.Win32Helper.GetWindowRect(_mainHwnd, out var wr)
                        && hs.pt.X >= wr.Left && hs.pt.X <= wr.Right && hs.pt.Y >= wr.Top && hs.pt.Y <= wr.Bottom)
                    {
                        int delta = (short)((hs.mouseData >> 16) & 0xFFFF);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (Math.Abs(delta) >= 120) ApplyAppsScroll(delta > 0 ? 40 : -40);
                            else if (_appsVertical) ApplyAppsScroll(delta * 0.5);
                        }));
                        return (IntPtr)1; // не передаём колесо окну под панелью
                    }
                }
                catch { }
            }
            return PlankWin.Win32Helper.CallNextHookEx(_scrollHookID, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)0x0201 || wParam == (IntPtr)0x0204 || wParam == (IntPtr)0x0207))
            {
                if (_activeIconMenu != null && _activeIconMenu.IsOpen)
                {
                    PlankWin.Win32Helper.GetCursorPos(out var pt);
                    if (PresentationSource.FromVisual(_activeIconMenu) is HwndSource source)
                    {
                        PlankWin.Win32Helper.GetWindowRect(source.Handle, out var rect);

                        if ((pt.X < rect.Left - 5 || pt.X > rect.Right + 5 || pt.Y < rect.Top - 5 || pt.Y > rect.Bottom + 5)
                            && !IsPointInOpenSubmenu(_activeIconMenu, pt.X, pt.Y))
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
                            }), DispatcherPriority.Normal);
                        }
                    }
                }
            }
            return PlankWin.Win32Helper.CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
        }

        // Для кнопки "Избранное" не показываем обычное меню приложения.
        private void OnAppButtonContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
        {
            HidePreview(); // ПКМ закрывает превью

            _contextMenuOpen = true;
            if (sender is FrameworkElement _cmEl && _cmEl.ContextMenu != null)
            {
                var _cm = _cmEl.ContextMenu;
                System.Windows.RoutedEventHandler? _h = null;
                _h = (s, ev) => { _contextMenuOpen = false; _cm.Closed -= _h; };
                _cm.Closed += _h;
            }
            else Dispatcher.BeginInvoke(new Action(() => _contextMenuOpen = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (sender is FrameworkElement fe && fe.DataContext is DockAppConfig app && app.IsFavorites)
            {
                e.Handled = true; // вместо меню приложения — своё меню выбора режима
                ShowFavoritesModeMenu(fe);
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            _openContextMenusCount++;

            if (sender is ContextMenu menu)
            {
                // «Добавить папку в избранное» — только у Проводника с реальной папкой и включённым избранным.
                if (menu.DataContext is DockAppConfig cmApp)
                {
                    bool canFav = Settings.EnableFavorites && IsExplorerApp(cmApp) && cmApp.IsRunning
                                  && GetExplorerFolderPath(cmApp.WindowHandle) != null;
                    foreach (var it in menu.Items.OfType<MenuItem>())
                        if (Equals(it.Tag, "AddFolderFav")) it.Visibility = canFav ? Visibility.Visible : Visibility.Collapsed;

                    // «Добавить файл в избранное» — если файл активного окна программы нашёлся и он ещё не в избранном.
                    string? openFile = null;
                    if (Settings.EnableFavoriteFiles && cmApp.IsRunning && !cmApp.IsFavorites && !IsExplorerApp(cmApp))
                    {
                        try { openFile = FindOpenFilePath(cmApp, cmApp.WindowHandle); } catch { }
                        if (openFile != null && Settings.FavoriteFiles.Contains(openFile, StringComparer.OrdinalIgnoreCase)) openFile = null;
                    }
                    foreach (var it in menu.Items.OfType<MenuItem>())
                        if (Equals(it.Tag, "AddFileFav"))
                        {
                            it.Visibility = openFile != null ? Visibility.Visible : Visibility.Collapsed;
                            it.CommandParameter = openFile;
                        }

                    UpdateRecentMenu(menu, cmApp);
                }

                _activeIconMenu = menu;
                menu.StaysOpen = true;
                menu.Focus();

                if (_mouseHookID == IntPtr.Zero)
                {
                    _mouseProc = MouseHookCallback;
                    using (Process curProcess = Process.GetCurrentProcess())
                    using (ProcessModule curModule = curProcess.MainModule!)
                    {
                        _mouseHookID = PlankWin.Win32Helper.SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, PlankWin.Win32Helper.GetModuleHandle(curModule.ModuleName), 0);
                    }
                }

                if (_menuWatcherTimer == null)
                {
                    _menuWatcherTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                    _menuWatcherTimer.Tick += MenuWatcherTimer_Tick;
                }
                _menuWatcherTimer.Start();
            }
        }

        private void MenuWatcherTimer_Tick(object? sender, EventArgs e)
        {
            if (_activeIconMenu == null || !_activeIconMenu.IsOpen)
            {
                _menuWatcherTimer?.Stop();
                return;
            }

            if ((PlankWin.Win32Helper.GetAsyncKeyState(0x1B) & 0x8000) != 0)
            {
                _activeIconMenu.IsOpen = false;
            }
        }

        private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            _openContextMenusCount--;
            if (_openContextMenusCount < 0) _openContextMenusCount = 0;
            _dragSource = null;

            if (sender is ContextMenu menu)
            {
                menu.StaysOpen = false;

                if (_activeIconMenu == menu)
                {
                    _activeIconMenu = null;
                }

                if (_openContextMenusCount == 0 && _mouseHookID != IntPtr.Zero)
                {
                    PlankWin.Win32Helper.UnhookWindowsHookEx(_mouseHookID);
                    _mouseHookID = IntPtr.Zero;
                }
            }
        }

        private async void OnHideAppClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app)
            {
                string processName = System.IO.Path.GetFileNameWithoutExtension(app.Path);
                if (!string.IsNullOrEmpty(processName))
                {
                    if (Settings.IgnoreProcessNames == null) Settings.IgnoreProcessNames = new System.Collections.ObjectModel.ObservableCollection<string>();
                    if (!Settings.IgnoreProcessNames.Any(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase)))
                    {
                        Settings.IgnoreProcessNames.Add(processName);
                        PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);
                    }

                    app.IsDeleting = true;
                    PositionDock(skipAnimation: false);

                    await AnimateItemContainerAsync(app, targetSize: 0, animateOpacity: true, durationMs: 350);
                    Apps.Remove(app);
                }
            }
            if (_activeIconMenu != null) _activeIconMenu.IsOpen = false;
        }

        private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private async System.Threading.Tasks.Task AnimateItemContainerAsync(DockAppConfig app, double targetSize, bool animateOpacity = false, int durationMs = 350)
        {
            var container = AppsItemsControl.ItemContainerGenerator.ContainerFromItem(app) as FrameworkElement;
            if (container == null)
            {
                await System.Threading.Tasks.Task.Delay(durationMs);
                return;
            }

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));

            var sizeEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };

            if (animateOpacity)
            {
                var opEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
                var opAnim = new System.Windows.Media.Animation.DoubleAnimation(container.Opacity, 0.0, duration) { EasingFunction = opEase };
                container.BeginAnimation(UIElement.OpacityProperty, opAnim);

                Panel.SetZIndex(container, -1);
            }

            AnimatedStackPanel.IsAnimationSuspended = true;

            bool isVertical = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;
            DependencyProperty lengthProp = isVertical ? FrameworkElement.HeightProperty : FrameworkElement.WidthProperty;

            double startSize = isVertical ? (double.IsNaN(container.Height) ? container.ActualHeight : container.Height)
                                          : (double.IsNaN(container.Width) ? container.ActualWidth : container.Width);

            if (startSize <= 0)
            {
                double k = Settings.IconSize / 44.0;
                startSize = Settings.IconSize + Math.Max(10.0, 10.0 * k) + Settings.IconMargin;
            }

            var sizeAnim = new System.Windows.Media.Animation.DoubleAnimation(startSize, targetSize, duration) { EasingFunction = sizeEase };
            sizeAnim.Completed += (s, e) => tcs.TrySetResult(true);
            container.BeginAnimation(lengthProp, sizeAnim);

            await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(durationMs + 50));
            AnimatedStackPanel.IsAnimationSuspended = false;
        }
    }
}