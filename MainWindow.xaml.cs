using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PlankWin
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DockAppConfig> Apps { get; } = new();

        private DockSettings _settings = new();

        // Таймер: проверка активного окна, мыши и автоскрытие
        private DispatcherTimer? _autoHideTimer;
        private bool _isHidden;

        // Таймер мониторинга процессов (подсветка и динамические приложения)
        private DispatcherTimer? _processPollTimer;

        private const int AutoTimerIntervalMs = 100;   // период опроса (мс)
        private const int AnimationDurationMs = 200;   // длительность анимации (мс)
        private const int EdgePressMs = 250;           // сколько "давить" на край, чтобы док появился

        // Позиции для "показано" и "спрятано"
        private double _shownLeft;
        private double _shownTop;
        private double _hiddenLeft;
        private double _hiddenTop;

        // Накопленное время "давления" мыши на край, когда док скрыт
        private double _edgeHoverMs;

        // Последняя позиция курсора, чтобы понимать направление движения
        private int _lastCursorX;
        private int _lastCursorY;
        private bool _hasLastCursor;

        // Флаг: разрешать ли закрытие окна (Alt+F4, крестик)
        private bool _allowClose = false;

        // Сколько времени док уже видим (мс) — чтобы не прятать мгновенно после показа
        private double _visibleTimeMs = 0;
        private const int MinVisibleMs = 150;

        // Последнее "реальное" foreground-окно (не док, не desktop/taskbar)
        private IntPtr _lastRealForeground = IntPtr.Zero;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        /// <summary>
        /// Вешаем Win32-хук:
        ///  - WM_MOUSEACTIVATE -> MA_NOACTIVATE (док не активируется при клике)
        ///  - WM_SYSCOMMAND / SC_CLOSE -> блокируем закрытие (Alt+F4, крестик)
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;

            const int WM_SYSCOMMAND = 0x0112;
            const int SC_CLOSE = 0xF060;

            if (msg == WM_MOUSEACTIVATE)
            {
                // Не активируем окно при клике мышью
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }

            if (msg == WM_SYSCOMMAND)
            {
                int cmd = wParam.ToInt32() & 0xFFF0;
                if (cmd == SC_CLOSE && !_allowClose)
                {
                    // Блокируем закрытие окна (Alt+F4, крестик, системное меню)
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            return IntPtr.Zero;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var baseDir = AppContext.BaseDirectory;

            // Настройки дока
            _settings = PlankConfig.Load(baseDir);

            // Позиция окна + координаты показанного/спрятанного состояния
            PositionDock();
            ComputeHiddenPosition();

            // Ориентация иконок (горизонт/вертикаль)
            ApplyItemsOrientation();

            // Приложения из apps.json (pinned)
            LoadAppsConfig();

            // Автоскрытие / появление
            SetupAutoHide();

            // Мониторинг запущенных приложений (и динамические иконки)
            StartProcessMonitor();
        }

        /// <summary>
        /// Запуск приложения по клику на иконку.
        /// </summary>
        private void OnAppButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn)
                return;

            if (btn.Tag is not DockAppConfig app)
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = app.Path,
                    Arguments = app.Arguments ?? string.Empty,
                    UseShellExecute = true
                };

                Process.Start(psi);

                IntPtr fg = GetEffectiveForegroundWindow();
                if (WindowBlocksDock(fg))
                {
                    HideDock();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось запустить '{app.Name}' ({app.Path}):\n{ex.Message}",
                    "Ошибка запуска",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
