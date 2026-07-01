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
        private const int MinVisibleMs = 550;
        private IntPtr _lastRealForeground = IntPtr.Zero;

        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private bool _isForceHidden = false;

        private Point _dragStartPoint;
        private bool _isDragging = false;
        private Window? _dragGhost;
        private DateTime _lastSwapTime = DateTime.MinValue;
        private DockAppConfig? _placeholderApp;

        public static DockAppConfig? DraggedAppConfig = null;

        private double _shownLeft;
        private double _shownTop;
        private double _hiddenLeft;
        private double _hiddenTop;

        private bool _isFirstStartup = true;

        public const double ZoomOverflow = 40.0;
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

                // РЕГИСТРИРУЕМ СЫРОЙ ВВОД МЫШИ (ДЛЯ ФИЗИЧЕСКОГО НАДАВЛИВАНИЯ)
                var rid = new PlankWin.Win32Helper.RAWINPUTDEVICE[1];
                rid[0].usUsagePage = 0x01; // Константа HID (General)
                rid[0].usUsage = 0x02;     // Константа HID (Мышь / Тачпад)
                rid[0].dwFlags = PlankWin.Win32Helper.RIDEV_INPUTSINK;
                rid[0].hwndTarget = source.Handle;

                PlankWin.Win32Helper.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(rid[0]));
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;

            UpdateAppBar(forceRemove: true);

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

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
                    ProcessRawMouseInput(raw.mouse.lLastX, raw.mouse.lLastY);
                }
            }

            return IntPtr.Zero;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Settings = PlankConfig.Load(AppContext.BaseDirectory, _profileName);

            _visualPosition = Settings.Position;
            _visualAlignment = Settings.Alignment;

            Settings.UIPosition = Settings.Position;
            DataContext = this;
            Settings.PropertyChanged += Settings_PropertyChanged;

            Apps.CollectionChanged += Apps_CollectionChanged;

            ApplySystemTheme();
            SystemEvents.UserPreferenceChanged += (s, ev) => Dispatcher.InvokeAsync(ApplySystemTheme);

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
            Application.Current.Resources["MenuBorderBrush"] = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Color.FromRgb(70, 70, 70) : System.Windows.Media.Color.FromRgb(220, 220, 220));
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() => PositionDock(skipAnimation: false));
        }

        private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DockSettings.UIPosition)) return;

            PlankConfig.Save(AppContext.BaseDirectory, _profileName, Settings);

            if (e.PropertyName == nameof(DockSettings.RunAtStartup))
            {
                ApplyAutostart(Settings.RunAtStartup);
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

        private void SetupTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon();
            _trayIcon.Text = "PlankWin";

            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exePath)) _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch { }

            var menu = new System.Windows.Forms.ContextMenuStrip();

            var titleItem = new System.Windows.Forms.ToolStripMenuItem("⚙ PlankWin (Настройки: Ctrl+Shift+ПКМ)");
            titleItem.Enabled = false;
            menu.Items.Add(titleItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            // --- Подменю "Режим панели" (три взаимоисключающих режима) ---
            var modeMenu = new System.Windows.Forms.ToolStripMenuItem("Режим панели");

            var modeAlways = new System.Windows.Forms.ToolStripMenuItem("Всегда видима");
            var modeAuto = new System.Windows.Forms.ToolStripMenuItem("Автоскрытие");
            var modeSmart = new System.Windows.Forms.ToolStripMenuItem("Умное скрытие");

            modeAlways.Click += (s, args) => { Settings.VisibilityMode = DockVisibilityMode.AlwaysVisible; };
            modeAuto.Click += (s, args) => { Settings.VisibilityMode = DockVisibilityMode.AutoHide; };
            modeSmart.Click += (s, args) => { Settings.VisibilityMode = DockVisibilityMode.SmartHide; };

            modeMenu.DropDownItems.Add(modeAlways);
            modeMenu.DropDownItems.Add(modeAuto);
            modeMenu.DropDownItems.Add(modeSmart);
            menu.Items.Add(modeMenu);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var forceHideItem = new System.Windows.Forms.ToolStripMenuItem("Принудительно скрыть панель");
            forceHideItem.CheckOnClick = true;
            forceHideItem.CheckedChanged += (s, args) =>
            {
                _isForceHidden = forceHideItem.Checked;
                if (_isForceHidden) { _isHidden = true; PositionDock(); }
                else { if (!Settings.AutoHide) { _isHidden = false; } PositionDock(); }
            };
            menu.Items.Add(forceHideItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Выход");
            exitItem.Click += (s, args) => { _allowClose = true; Application.Current.Shutdown(); };
            menu.Items.Add(exitItem);

            // При каждом открытии меню — отмечаем текущий режим точкой (актуально, даже
            // если режим сменили через окно настроек) и синхронизируем галочку force-hide.
            menu.Opening += (s, args) =>
            {
                modeAlways.Checked = Settings.VisibilityMode == DockVisibilityMode.AlwaysVisible;
                modeAuto.Checked = Settings.VisibilityMode == DockVisibilityMode.AutoHide;
                modeSmart.Checked = Settings.VisibilityMode == DockVisibilityMode.SmartHide;
                forceHideItem.Checked = _isForceHidden;
            };

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.Visible = true;
        }

        private void Apps_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isLoadingConfig) return;

            SaveAppsConfig();
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                if (e.NewItems?[0] is DockAppConfig app)
                {
                    this.Width = this.ActualWidth;
                    this.Height = this.ActualHeight;
                    this.SizeToContent = SizeToContent.Manual;

                    AnimatedStackPanel.IsAnimationSuspended = true;

                    var generator = AppsItemsControl.ItemContainerGenerator;

                    void PrepareAndAnimate()
                    {
                        var container = generator.ContainerFromItem(app) as FrameworkElement;
                        if (container != null)
                        {
                            bool isVertical = _visualPosition == DockPosition.Left || _visualPosition == DockPosition.Right;

                            if (!app.IsPlaceholder) container.Opacity = 0;

                            double targetSize = Settings.IconSize + Settings.IconMargin;

                            Dispatcher.InvokeAsync(() =>
                            {
                                if (app.IsPlaceholder) AnimatePlaceholderAddedAsync(container, targetSize);
                                else AnimateItemAddedAsync(container, targetSize);
                            }, DispatcherPriority.Loaded);
                        }
                        else
                        {
                            AnimatedStackPanel.IsAnimationSuspended = false;
                        }
                    }

                    if (generator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    {
                        PrepareAndAnimate();
                    }
                    else
                    {
                        EventHandler? statusChanged = null;
                        statusChanged = (s, ev) =>
                        {
                            if (generator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                            {
                                generator.StatusChanged -= statusChanged;
                                PrepareAndAnimate();
                            }
                        };
                        generator.StatusChanged += statusChanged;
                    }

                    PositionDock(skipAnimation: false);
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

            var duration = new Duration(TimeSpan.FromMilliseconds(400));
            var sizeEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
            var opEase = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };

            var sizeAnim = new System.Windows.Media.Animation.DoubleAnimation(0, targetSize, duration) { EasingFunction = sizeEase };
            var opAnim = new System.Windows.Media.Animation.DoubleAnimation(0, 1.0, duration) { EasingFunction = opEase };

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            sizeAnim.Completed += (s, e) => tcs.TrySetResult(true);

            container.BeginAnimation(UIElement.OpacityProperty, opAnim);
            container.BeginAnimation(lengthProp, sizeAnim);

            await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(450));

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
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
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

                        if (!string.IsNullOrEmpty(app.Path) && System.IO.File.Exists(app.Path))
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

                        try
                        {
                            DragDrop.DoDragDrop(btn, dragData, DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
                        }
                        catch { }
                        finally
                        {
                            btn.Opacity = 1.0;
                            _isDragging = false;
                            _draggedApp = null;
                            CleanupExternalDrag();
                        }
                    }
                }
            }
        }

        private void OnAppButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DockAppConfig app) return;
            if (app.IsPlaceholder) return;

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
                        IntPtr targetHwnd = app.WindowHandles[0];
                        if (PlankWin.Win32Helper.IsIconic(targetHwnd))
                        {
                            PlankWin.Win32Helper.ShowWindow(targetHwnd, 9);
                        }
                        PlankWin.Win32Helper.SetForegroundWindow(targetHwnd);
                    }
                }
                else
                {
                    var psi = new ProcessStartInfo { FileName = app.Path, Arguments = app.Arguments ?? string.Empty, UseShellExecute = true };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка запуска:\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseAppClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app && app.IsRunning && app.WindowHandle != IntPtr.Zero)
            {
                PlankWin.Win32Helper.SendMessage(app.WindowHandle, PlankWin.Win32Helper.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
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
            if (sender is MenuItem menuItem && menuItem.DataContext is DockAppConfig app)
            {
                try
                {
                    var psi = new ProcessStartInfo { FileName = app.Path, Arguments = app.Arguments ?? string.Empty, UseShellExecute = true };
                    Process.Start(psi);
                    if (Settings.AutoHide) HideDock();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка запуска:\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
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
                        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        private void OnIconMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            _isDragging = false;
        }

        private void CreateDragGhost(DockAppConfig app)
        {
            if (_dragGhost != null) { _dragGhost.Close(); _dragGhost = null; }

            var converter = new AppIconConverter();
            var imageSource = converter.Convert(app, typeof(System.Windows.Media.ImageSource), null!, System.Globalization.CultureInfo.CurrentCulture) as System.Windows.Media.ImageSource;

            _dragGhost = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                IsHitTestVisible = false,
                ShowActivated = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = new Image { Source = imageSource, Width = 44, Height = 44, Opacity = 0.85 }
            };

            PlankWin.Win32Helper.GetCursorPos(out var pt);
            _dragGhost.Left = pt.X - 22;
            _dragGhost.Top = pt.Y - 22;

            var helper = new WindowInteropHelper(_dragGhost);
            helper.EnsureHandle();

            int extendedStyle = PlankWin.Win32Helper.GetWindowLong(helper.Handle, PlankWin.Win32Helper.GWL_EXSTYLE);
            PlankWin.Win32Helper.SetWindowLong(helper.Handle, PlankWin.Win32Helper.GWL_EXSTYLE, extendedStyle | PlankWin.Win32Helper.WS_EX_TRANSPARENT);

            _dragGhost.Show();
        }

        private void OnIconGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_dragGhost != null)
            {
                PlankWin.Win32Helper.GetCursorPos(out var pt);

                double dpiX = 1.0, dpiY = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }

                _dragGhost.Left = (pt.X / dpiX) - (_dragGhost.ActualWidth / 2);
                _dragGhost.Top = (pt.Y / dpiY) - (_dragGhost.ActualHeight / 2);

                Mouse.SetCursor(Cursors.Arrow);
                e.UseDefaultCursors = false;
                e.Handled = true;
            }
        }

        private int CalculateTargetDropIndex(bool isExistingApp = false)
        {
            PlankWin.Win32Helper.GetCursorPos(out var pt);
            double dpiX = 1.0, dpiY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null) { dpiX = source.CompositionTarget.TransformToDevice.M11; dpiY = source.CompositionTarget.TransformToDevice.M22; }
            double screenX = pt.X / dpiX; double screenY = pt.Y / dpiY;

            int activeCount = Apps.Count(a => !a.IsDeleting);
            int simulatedCount = (_placeholderApp == null && !isExistingApp) ? activeCount + 1 : activeCount;

            Rect futureGlass = SimulateGlassRect(simulatedCount);

            bool isVertical = Settings.Position == DockPosition.Left || Settings.Position == DockPosition.Right;

            double k = Settings.IconSize / 44.0;
            double morphCellLength = Settings.IconSize + Math.Max(10.0, 10.0 * k);
            double buttonStep = morphCellLength + Settings.IconMargin;

            if (buttonStep <= 0) return 0;

            double stableLocalPos = isVertical ? (screenY - futureGlass.Y) : (screenX - futureGlass.X);
            stableLocalPos -= Settings.PanelPadding;

            int targetIndex = (int)(stableLocalPos / buttonStep);

            int maxIndex = Math.Max(0, simulatedCount - 1);
            int finalIndex = Math.Clamp(targetIndex, 0, maxIndex);

            return finalIndex;
        }

        private void UpdateDragGhost(string firstFile)
        {
            if (_dragGhost == null && !_isDragging)
            {
                string resolvedPath = firstFile;
                if (firstFile.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedPath = PlankWin.Win32Helper.ResolveShortcut(firstFile, out _);
                }

                CreateDragGhost(new DockAppConfig
                {
                    Path = resolvedPath,
                    Name = System.IO.Path.GetFileNameWithoutExtension(firstFile),
                    IsPlaceholder = false
                });

                if (_dragGhost != null)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
                }
            }

            if (_dragGhost != null)
            {
                PlankWin.Win32Helper.GetCursorPos(out var ptGhost);
                double dpiX = 1.0, dpiY = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null) { dpiX = source.CompositionTarget.TransformToDevice.M11; dpiY = source.CompositionTarget.TransformToDevice.M22; }

                _dragGhost.Left = (ptGhost.X / dpiX) + 5;
                _dragGhost.Top = (ptGhost.Y / dpiY) + 5;
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
                checkPath = PlankWin.Win32Helper.ResolveShortcut(firstFile, out _);

                // UWP-ярлык: ResolveShortcut пуст -> берём AUMID, чтобы checkPath совпадал
                // с тем, что сохранит HandleExternalDrop (shell:AppsFolder\AUMID),
                // иначе повторное перетаскивание того же UWP не распознаётся как дубликат.
                if (string.IsNullOrEmpty(checkPath))
                {
                    string aumid = PlankWin.Win32Helper.GetAumidFromShortcut(firstFile);
                    if (!string.IsNullOrEmpty(aumid))
                        checkPath = "shell:AppsFolder\\" + aumid;
                }
            }
            var existingApp = System.Linq.Enumerable.FirstOrDefault(Apps, a => a != null && !a.IsPlaceholder && string.Equals(a.Path, checkPath, StringComparison.OrdinalIgnoreCase));

            int targetIndex = CalculateTargetDropIndex();

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

                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string resolvedPath = file;
                    string resolvedArgs = string.Empty;
                    string appName = System.IO.Path.GetFileNameWithoutExtension(file);

                    if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedPath = PlankWin.Win32Helper.ResolveShortcut(file, out resolvedArgs);

                        // UWP-ярлык (Калькулятор, Settings и т.п.) не имеет обычного пути к exe —
                        // ResolveShortcut возвращает пусто. В этом случае извлекаем AUMID из ярлыка
                        // и формируем путь shell:AppsFolder\AUMID, который наш код запуска
                        // (Process.Start с UseShellExecute) и конвертер иконок уже понимают.
                        if (string.IsNullOrEmpty(resolvedPath))
                        {
                            string aumid = PlankWin.Win32Helper.GetAumidFromShortcut(file);
                            if (!string.IsNullOrEmpty(aumid))
                                resolvedPath = "shell:AppsFolder\\" + aumid;
                        }
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
                            var newApp = new DockAppConfig { Name = appName, Path = resolvedPath, Arguments = resolvedArgs, IsDynamic = false, IsRunning = false };
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

            if (_dragGhost != null) { _dragGhost.Close(); _dragGhost = null; }

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
                    }
                }
            }
            else if (HasValidExecutable(e.Data))
            {
                ProcessExternalDrag(e);
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

        private void OnIconDrop(object sender, DragEventArgs e)
        {
            if (_isDragging && _draggedApp != null)
            {
                SaveAppsConfig();
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                HandleExternalDrop(sender, e);
            }
        }

        private void OnIconMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                if (sender is Button btn && btn.DataContext is DockAppConfig app)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = app.Path,
                            Arguments = app.Arguments ?? string.Empty,
                            UseShellExecute = true
                        };
                        Process.Start(psi);

                        if (Settings.AutoHide)
                        {
                            _isHidden = true;
                            PositionDock();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка запуска:\n{ex.Message}", "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    e.Handled = true;
                }
            }
        }

        private void Window_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
            {
                var settingsWin = new SettingsWindow(Settings, _profileName);
                settingsWin.Owner = this;
                settingsWin.ShowDialog();
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

                        if (pt.X < rect.Left - 5 || pt.X > rect.Right + 5 || pt.Y < rect.Top - 5 || pt.Y > rect.Bottom + 5)
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

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            _openContextMenusCount++;

            if (sender is ContextMenu menu)
            {
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