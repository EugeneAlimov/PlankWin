using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlankWin
{
    public enum DockPosition { Bottom, Top, Left, Right }
    public enum DockAlignment { Center, Start, End }

    public sealed class DockSettings : INotifyPropertyChanged
    {
        // Список процессов, которые не должны появляться в доке
        public System.Collections.ObjectModel.ObservableCollection<string> IgnoreProcessNames { get; set; } = new();

        public bool RunAtStartup { get; set; } = false;

        // --- БАЗОВОЕ ПОВЕДЕНИЕ ---
        private DockPosition _position = DockPosition.Bottom;
        private DockAlignment _alignment = DockAlignment.Center;
        private bool _isAdaptive = true;
        private bool _reserveScreenSpace = true;
        private bool _autoHide = false;
        //private string[] _ignoreProcessNames = Array.Empty<string>();
        private string _targetMonitor = ""; // Пустая строка = Основной монитор

        // --- РАЗМЕРЫ ---
        private double _thickness = 64;
        private double _iconSize = 44;
        private double _iconMargin = 6;
        private double _panelPadding = 10;
        private double _edgeMargin = 10;
        private double _panelCornerRadius = 16;
        private double _iconCornerRadius = 12;

        // --- ЦВЕТА (HEX) ---
        private string _backgroundColor = "#151515";
        private double _backgroundOpacity = 0.8;
        private string _borderColor = "#33FFFFFF";
        private string _indicatorColor = "#FFFFFFFF";
        private string _hoverHighlightColor = "#15FFFFFF";

        // -------------------------------------------------------------------
        // СВОЙСТВА (PROPERTIES) ДЛЯ ПРИВЯЗКИ
        // -------------------------------------------------------------------

        public DockPosition Position
        {
            get => _position;
            set { if (_position != value) { _position = value; OnPropertyChanged(); } }
        }

        public string TargetMonitor
        {
            get => _targetMonitor;
            set { if (_targetMonitor != value) { _targetMonitor = value; OnPropertyChanged(); } }
        }

        private DockPosition _uiPosition = DockPosition.Bottom;
        [JsonIgnore] // Не сохраняем это в файл настроек, это только для визуала
        public DockPosition UIPosition
        {
            get => _uiPosition;
            set { if (_uiPosition != value) { _uiPosition = value; OnPropertyChanged(); } }
        }

        public DockAlignment Alignment
        {
            get => _alignment;
            set { if (_alignment != value) { _alignment = value; OnPropertyChanged(); } }
        }

        private bool _isFloating = true;

        public bool IsFloating
        {
            get => _isFloating;
            set { if (_isFloating != value) { _isFloating = value; OnPropertyChanged(); } }
        }

        public bool IsAdaptive
        {
            get => _isAdaptive;
            set { if (_isAdaptive != value) { _isAdaptive = value; OnPropertyChanged(); } }
        }

        public double Thickness
        {
            get => _thickness;
            set
            {
                double clamped = Math.Clamp(value, 4, 128);
                if (_thickness != clamped)
                {
                    _thickness = clamped;
                    OnPropertyChanged();
                    UpdateShelfModeHysteresis();
                }
            }
        }

        public double IconSize
        {
            get => _iconSize;
            set
            {
                double clamped = Math.Clamp(value, 16, 128);
                if (_iconSize != clamped)
                {
                    _iconSize = clamped;
                    OnPropertyChanged();
                    UpdateShelfModeHysteresis();
                }
            }
        }

        public double IconMargin
        {
            get => _iconMargin;
            set { if (_iconMargin != value) { _iconMargin = value; OnPropertyChanged(); } }
        }

        public double PanelPadding
        {
            get => _panelPadding;
            set { if (_panelPadding != value) { _panelPadding = value; OnPropertyChanged(); } }
        }

        public double EdgeMargin
        {
            get => _edgeMargin;
            set { if (_edgeMargin != value) { _edgeMargin = value; OnPropertyChanged(); } }
        }

        public double PanelCornerRadius
        {
            get => _panelCornerRadius;
            set { if (_panelCornerRadius != value) { _panelCornerRadius = value; OnPropertyChanged(); } }
        }

        public double IconCornerRadius
        {
            get => _iconCornerRadius;
            set { if (_iconCornerRadius != value) { _iconCornerRadius = value; OnPropertyChanged(); } }
        }

        public string BackgroundColor
        {
            get => _backgroundColor;
            set { if (_backgroundColor != value) { _backgroundColor = value; OnPropertyChanged(); } }
        }

        public double BackgroundOpacity
        {
            get => _backgroundOpacity;
            set { double clamped = Math.Clamp(value, 0.0, 1.0); if (_backgroundOpacity != clamped) { _backgroundOpacity = clamped; OnPropertyChanged(); } }
        }

        public string BorderColor
        {
            get => _borderColor;
            set { if (_borderColor != value) { _borderColor = value; OnPropertyChanged(); } }
        }

        public string IndicatorColor
        {
            get => _indicatorColor;
            set { if (_indicatorColor != value) { _indicatorColor = value; OnPropertyChanged(); } }
        }

        public string HoverHighlightColor
        {
            get => _hoverHighlightColor;
            set { if (_hoverHighlightColor != value) { _hoverHighlightColor = value; OnPropertyChanged(); } }
        }

        public bool ReserveScreenSpace
        {
            get => _reserveScreenSpace;
            set { if (_reserveScreenSpace != value) { _reserveScreenSpace = value; OnPropertyChanged(); } }
        }

        public bool AutoHide
        {
            get => _autoHide;
            set { if (_autoHide != value) { _autoHide = value; OnPropertyChanged(); } }
        }


        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isShelfMode;
        [JsonIgnore]
        public bool IsShelfMode
        {
            get => _isShelfMode;
            set { if (_isShelfMode != value) { _isShelfMode = value; OnPropertyChanged(); } }
        }

        private void UpdateShelfModeHysteresis()
        {
            // Тот же самый коэффициент
            double k = IconSize / 44.0;

            // НОВЫЙ ПОРОГ: масштабируется вместе с размером капсулы выделения!
            double threshold = IconSize + Math.Max(20.0, 36.0 * k);

            if (!IsShelfMode && Thickness <= threshold - 2)
                IsShelfMode = true;
            else if (IsShelfMode && Thickness >= threshold + 2)
                IsShelfMode = false;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public static class PlankConfig
    {
        // Убрали жесткую константу ConfigFileName

        private static JsonSerializerOptions GetJsonOptions() => new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true
        };

        // Теперь мы передаем имя профиля (например, "Primary" или "DISPLAY2")
        public static DockSettings Load(string baseDir, string profileName = "Primary")
        {
            // Очищаем имя профиля от запрещенных символов (например, слэшей из \\.\DISPLAY2)
            string safeProfileName = string.Join("_", profileName.Split(Path.GetInvalidFileNameChars()));
            string fileName = $"settings_{safeProfileName}.json";
            var path = Path.Combine(baseDir, fileName);

            if (!File.Exists(path))
            {
                var settings = new DockSettings();
                TrySave(path, settings);
                return settings;
            }
            try
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<DockSettings>(json, GetJsonOptions()) ?? new DockSettings();

                // === ИСПРАВЛЕННАЯ СТРОКА ===
                if (settings.IgnoreProcessNames == null)
                    settings.IgnoreProcessNames = new System.Collections.ObjectModel.ObservableCollection<string>();

                settings.Thickness = settings.Thickness;

                return settings;
            }
            catch { return new DockSettings(); }
        }

        public static void Save(string baseDir, string profileName, DockSettings settings)
        {
            string safeProfileName = string.Join("_", profileName.Split(Path.GetInvalidFileNameChars()));
            string fileName = $"settings_{safeProfileName}.json";
            TrySave(Path.Combine(baseDir, fileName), settings);
        }

        private static void TrySave(string path, DockSettings settings)
        {
            try { File.WriteAllText(path, JsonSerializer.Serialize(settings, GetJsonOptions())); } catch { }
        }
    }
}