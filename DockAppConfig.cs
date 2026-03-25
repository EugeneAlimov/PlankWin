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
        public bool IsRunning
        {
            get => _isRunning;
            set { if (_isRunning == value) return; _isRunning = value; OnPropertyChanged(); }
        }

        private int _runningInstancesCount;
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
            System.Diagnostics.Debug.WriteLine($"🔄 FORCE ICON UPDATE called for: {Name}");
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

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}