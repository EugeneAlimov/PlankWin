using System.Windows;

namespace PlankWin
{
    public partial class SettingsWindow : Window
    {
        private bool _isUpdatingColor = false;
        private string _profileName = "Primary";

        public System.Collections.ObjectModel.ObservableCollection<AppFilterItem> FilterItems { get; } = new();

        public class AppFilterItem : System.ComponentModel.INotifyPropertyChanged
        {
            private bool _isIgnored;
            public string ProcessName { get; set; } = "";
            public bool IsRunning { get; set; }

            public bool IsIgnored
            {
                get => _isIgnored;
                set { if (_isIgnored != value) { _isIgnored = value; OnPropertyChanged(); } }
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? prop = null)
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));
            }
        }

        public SettingsWindow(DockSettings currentSettings, string profileName)
        {
            InitializeComponent();
            _profileName = profileName;

            var allPositions = (DockPosition[])Enum.GetValues(typeof(DockPosition));
            var allowedPositions = new List<DockPosition>();

            // === ТОТ САМЫЙ БЕЗОПАСНЫЙ ПОИСК ЭКРАНА (Внутри конструктора!) ===
            var screen = profileName == "Primary"
                ? System.Windows.Forms.Screen.PrimaryScreen
                : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == profileName)
                  ?? System.Windows.Forms.Screen.PrimaryScreen;

            if (screen != null)
            {
                foreach (var pos in allPositions)
                {
                    if (IsPositionSafe(screen, pos)) allowedPositions.Add(pos);
                }
            }

            CmbPosition.ItemsSource = allowedPositions.Count > 0 ? allowedPositions : allPositions;
            CmbAlignment.ItemsSource = Enum.GetValues(typeof(DockAlignment));
            DataContext = currentSettings;

            ParseHexToSliders(currentSettings.BackgroundColor);
            RefreshFilterList();
        }

        // =========================================================
        // ИГРОВОЙ ЦИКЛ ПЕРЕТАСКИВАНИЯ (Синхронизация с монитором)
        // =========================================================

        private bool _isDragging = false;
        private Win32Helper.Win32Point _startMousePos;
        private Point _startWindowPos;

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                _isDragging = true;
                ((UIElement)sender).CaptureMouse();

                // Запоминаем стартовые точки
                Win32Helper.GetCursorPos(out _startMousePos);
                _startWindowPos = new Point(this.Left, this.Top);

                // МАГИЯ: Подписываемся на аппаратное обновление кадров монитора (60/144 Гц)
                System.Windows.Media.CompositionTarget.Rendering += DragWindow_Rendering;
            }
        }

        private void TitleBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                ((UIElement)sender).ReleaseMouseCapture();

                // Обязательно отписываемся, чтобы не грузить систему
                System.Windows.Media.CompositionTarget.Rendering -= DragWindow_Rendering;
            }
        }

        private void DragWindow_Rendering(object? sender, EventArgs e)
        {
            if (!_isDragging) return;

            // Берем координаты ровно 1 раз за кадр, игнорируя 1000Гц спам от MX Master
            Win32Helper.GetCursorPos(out Win32Helper.Win32Point currentMousePos);

            double dx = currentMousePos.X - _startMousePos.X;
            double dy = currentMousePos.Y - _startMousePos.Y;

            // Учитываем масштаб Windows
            double dpiX = 1.0, dpiY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }

            // Идеально плавное смещение без обратной связи
            this.Left = _startWindowPos.X + (dx / dpiX);
            this.Top = _startWindowPos.Y + (dy / dpiY);
        }

        // === МЕТОД ПРОВЕРКИ ПОЗИЦИИ (Возвращаем переменные screens и threshold) ===
        private bool IsPositionSafe(System.Windows.Forms.Screen currentScreen, DockPosition pos)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int threshold = 10;

            foreach (var other in screens)
            {
                if (other.DeviceName == currentScreen.DeviceName) continue;
                if (pos == DockPosition.Right && Math.Abs(currentScreen.Bounds.Right - other.Bounds.Left) < threshold) return false;
                if (pos == DockPosition.Left && Math.Abs(currentScreen.Bounds.Left - other.Bounds.Right) < threshold) return false;
                if (pos == DockPosition.Top && Math.Abs(currentScreen.Bounds.Top - other.Bounds.Bottom) < threshold) return false;
                if (pos == DockPosition.Bottom && Math.Abs(currentScreen.Bounds.Bottom - other.Bounds.Top) < threshold) return false;
            }
            return true;
        }

        // === ПЕРЕТАСКИВАНИЕ ===

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BtnRefreshFilter_Click(object sender, RoutedEventArgs e)
        {
            RefreshFilterList();
        }

        private void RefreshFilterList()
        {
            FilterItems.Clear();
            if (this.DataContext is not DockSettings settings) return;

            var ignored = new HashSet<string>(settings.IgnoreProcessNames ?? new System.Collections.ObjectModel.ObservableCollection<string>(), StringComparer.OrdinalIgnoreCase);

            var running = System.Diagnostics.Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                .Select(p => p.ProcessName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var combined = ignored.Union(running).Distinct(StringComparer.OrdinalIgnoreCase);

            var tempList = new List<AppFilterItem>();
            foreach (var p in combined)
            {
                var item = new AppFilterItem
                {
                    ProcessName = p.ToLower(),
                    IsIgnored = ignored.Contains(p),
                    IsRunning = running.Contains(p)
                };

                item.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(AppFilterItem.IsIgnored))
                    {
                        settings.IgnoreProcessNames ??= new System.Collections.ObjectModel.ObservableCollection<string>();

                        if (item.IsIgnored && !settings.IgnoreProcessNames.Any(x => x.Equals(item.ProcessName, StringComparison.OrdinalIgnoreCase)))
                        {
                            settings.IgnoreProcessNames.Add(item.ProcessName);
                        }
                        else if (!item.IsIgnored)
                        {
                            var toRemove = settings.IgnoreProcessNames.FirstOrDefault(x => x.Equals(item.ProcessName, StringComparison.OrdinalIgnoreCase));
                            if (toRemove != null) settings.IgnoreProcessNames.Remove(toRemove);
                        }

                        PlankConfig.Save(AppContext.BaseDirectory, _profileName, settings);
                    }
                };
                tempList.Add(item);
            }

            var sorted = tempList.OrderByDescending(x => x.IsRunning).ThenBy(x => x.ProcessName);
            foreach (var item in sorted) FilterItems.Add(item);
        }

        private void ParseHexToSliders(string hex)
        {
            try
            {
                _isUpdatingColor = true;
                if (!hex.StartsWith("#")) hex = "#" + hex;
                var wpfColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

                SliderR.Value = wpfColor.R;
                SliderG.Value = wpfColor.G;
                SliderB.Value = wpfColor.B;
            }
            catch { }
            finally { _isUpdatingColor = false; }
        }

        private void SliderRGB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingColor || this.DataContext is not DockSettings settings) return;

            byte r = (byte)SliderR.Value;
            byte g = (byte)SliderG.Value;
            byte b = (byte)SliderB.Value;

            settings.BackgroundColor = $"#{r:X2}{g:X2}{b:X2}";
        }
    }
}