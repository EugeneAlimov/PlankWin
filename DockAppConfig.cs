using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PlankWin
{
    public class DockAppConfig : INotifyPropertyChanged
    {
        private bool _isActive = false;
        [JsonIgnore]
        public bool IsActive
        {
            get => _isActive;
            set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); }
        }

        // Анимация появления/исчезновения (свой аниматор). Default 1 = полностью видимо/раскрыто.
        private double _appearScale = 1.0;
        [System.Text.Json.Serialization.JsonIgnore]
        public double AppearScale
        {
            get => _appearScale;
            set { if (_appearScale == value) return; _appearScale = value; OnPropertyChanged(); }
        }

        private double _appearOpacity = 1.0;
        [System.Text.Json.Serialization.JsonIgnore]
        public double AppearOpacity
        {
            get => _appearOpacity;
            set { if (_appearOpacity == value) return; _appearOpacity = value; OnPropertyChanged(); }
        }

        private int _activeWindowIndex = -1;
        [JsonIgnore]
        public int ActiveWindowIndex
        {
            get => _activeWindowIndex;
            set { if (_activeWindowIndex == value) return; _activeWindowIndex = value; OnPropertyChanged(); }
        }

        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string? Arguments { get; set; }

        // Ярлыки лаунчеров (.url Steam/Epic): чем запускать и где лежит игра (для склейки с процессом).
        public string? LaunchUri { get; set; }

        // Закреплённые пункты меню «Недавнее» (полные пути).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public System.Collections.Generic.List<string>? RecentPins { get; set; }
        public string? InstallDir { get; set; }

        // UWP/пакетное приложение: путь имеет вид shell:AppsFolder\<AUMID>.
        [JsonIgnore]
        public bool IsPackaged => !string.IsNullOrEmpty(Path) &&
            Path.StartsWith("shell:AppsFolder\\", System.StringComparison.OrdinalIgnoreCase);

        // Пункт "Запуск от администратора" скрыт для UWP (elevation неприменим).
        [JsonIgnore]
        public System.Windows.Visibility RunAsAdminVisibility =>
            IsPackaged ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        // Видимость пункта «Закрыть все окна» — только при нескольких окнах.
        public System.Windows.Visibility MultipleWindowsVisibility =>
            (IsRunning && WindowHandles.Count > 1) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public string? Icon { get; set; }
        public string? ProcessName { get; set; }

        private bool _isDeleting = false;
        [JsonIgnore]
        public bool IsDeleting
        {
            get => _isDeleting;
            set { if (_isDeleting == value) return; _isDeleting = value; OnPropertyChanged(); }
        }

        private bool _isDynamic = true;
        public bool IsDynamic
        {
            get => _isDynamic;
            set { if (_isDynamic == value) return; _isDynamic = value; OnPropertyChanged(); }
        }

        private bool _isRunning;
        [JsonIgnore]
        public bool IsRunning
        {
            get => _isRunning;
            set { if (_isRunning == value) return; _isRunning = value; OnPropertyChanged(); }
        }

        // Счётчик из заголовка окна (Telegram (5), (3) WhatsApp…) — бейдж в углу иконки.
        private int _badgeCount;
        [JsonIgnore]
        public int BadgeCount
        {
            get => _badgeCount;
            set
            {
                if (_badgeCount == value) return;
                _badgeCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(HasBadge));
            }
        }
        [JsonIgnore] public string BadgeText => _badgeCount > 99 ? "99+" : _badgeCount.ToString();
        [JsonIgnore] public bool HasBadge => _badgeCount > 0;

        private int _runningInstancesCount;
        [JsonIgnore]
        public int RunningInstancesCount
        {
            get => _runningInstancesCount;
            set { if (_runningInstancesCount == value) return; _runningInstancesCount = value; OnPropertyChanged(); }
        }

        private IntPtr _windowHandle = IntPtr.Zero;
        [JsonIgnore]
        public IntPtr WindowHandle
        {
            get => _windowHandle;
            set
            {
                if (_windowHandle == value) return;
                _windowHandle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveIconTrigger)); // МАГИЯ: Заставляем WPF перерисовать иконку!
            }
        }

        [JsonIgnore]
        public DockAppConfig ActiveIconTrigger => this; // Свойство-пустышка для биндинга

        public void ForceIconUpdate()
        {
            // Мы меняем триггер, на который подписана иконка в XAML
            OnPropertyChanged(nameof(WindowHandle));
            OnPropertyChanged(nameof(Path));
            // Если есть специальное свойство для обновления иконки, дергаем и его
            OnPropertyChanged(nameof(ActiveIconTrigger));
        }

        [JsonIgnore]
        public System.Collections.Generic.List<IntPtr> WindowHandles { get; } = new();

        private bool _isPlaceholder = false;
        [JsonIgnore]
        public bool IsPlaceholder
        {
            get => _isPlaceholder;
            set { if (_isPlaceholder == value) return; _isPlaceholder = value; OnPropertyChanged(); }
        }

        private double _edgePushProgress = 0;
        [JsonIgnore]
        public double EdgePushProgress
        {
            get => _edgePushProgress;
            set { if (_edgePushProgress == value) return; _edgePushProgress = value; OnPropertyChanged(); }
        }

        private bool _isFavorites = false;
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsFavorites
        {
            get => _isFavorites;
            set { if (_isFavorites != value) { _isFavorites = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyWindowsChanged() => OnPropertyChanged(nameof(MultipleWindowsVisibility));

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}