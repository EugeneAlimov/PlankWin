using System.Windows;
using System.Windows.Controls;

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
            ChkCheckUpdates.IsChecked = GlobalConfig.CheckUpdates;
            ChkBadges.IsChecked = GlobalConfig.ShowBadges;
            FillHotkeysCombo();
            TxtVersion.Text = string.Format(Loc.T("upd_version"), Updater.CurrentText) + (Updater.IsDevBuild ? " · " + Loc.T("upd_dev") : "");

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
            ParseHexToTintSliders(currentSettings.PeekTint);
            FillLanguageCombo();
            // Цвет может смениться извне (авто-цвет Windows) — держим слайдеры в синхроне.
            currentSettings.PropertyChanged += Settings_ColorChanged;
            Closed += (s, e) => currentSettings.PropertyChanged -= Settings_ColorChanged;
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

        private bool _fillingHotkeys;
        private void FillHotkeysCombo()
        {
            _fillingHotkeys = true;
            CmbHotkeys.Items.Clear();
            foreach (var m in MainWindow.HotkeyModes)
                CmbHotkeys.Items.Add(new ComboBoxItem { Content = m.key == "off" ? Loc.T("hk_off") : m.label + " + 1…9 / 0", Tag = m.key });
            CmbHotkeys.SelectedItem = CmbHotkeys.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == GlobalConfig.Hotkeys)
                                      ?? CmbHotkeys.Items[0];
            _fillingHotkeys = false;
            UpdateHotkeysStatus();
        }

        private void CmbHotkeys_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingHotkeys || CmbHotkeys.SelectedItem is not ComboBoxItem it) return;
            GlobalConfig.Hotkeys = (string)it.Tag;
            MainWindow.ApplyHotkeys();
            UpdateHotkeysStatus();
        }

        private void UpdateHotkeysStatus()
        {
            if (GlobalConfig.Hotkeys == "off") { TxtHotkeys.Text = ""; return; }
            TxtHotkeys.Text = MainWindow.HotkeyFailures.Count == 0
                ? Loc.T("hk_hint")
                : string.Format(Loc.T("hk_busy"), string.Join(", ", MainWindow.HotkeyFailures));
            // Ctrl+Alt = AltGr: на раскладках с AltGr (de, hu, pl…) перестанут печататься символы AltGr+цифра.
            if (GlobalConfig.Hotkeys == "ctrlalt") TxtHotkeys.Text += "\n" + Loc.T("hk_altgr");
        }

        private void ChkBadges_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            GlobalConfig.ShowBadges = ChkBadges.IsChecked == true; // таймер бейджей подхватит на следующем тике
        }

        private void ChkCheckUpdates_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            GlobalConfig.CheckUpdates = ChkCheckUpdates.IsChecked == true;
        }

        private void BtnExportConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"PlankWin-config-{System.DateTime.Now:yyyy-MM-dd}.zip",
                Filter = "PlankWin config (*.zip)|*.zip",
                DefaultExt = ".zip"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                ConfigTransfer.Export(dlg.FileName);
                MessageBox.Show(this, Loc.T("cfg_export_done"), "PlankWin", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, ex.Message, "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnImportConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PlankWin config (*.zip)|*.zip" };
            if (dlg.ShowDialog(this) != true) return;
            if (!ConfigTransfer.IsValid(dlg.FileName))
            {
                MessageBox.Show(this, Loc.T("cfg_bad_file"), "PlankWin", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show(this, Loc.T("cfg_import_confirm"), "PlankWin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            try
            {
                ConfigTransfer.Import(dlg.FileName);
                MainWindow.RestartApp();
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, ex.Message, "PlankWin", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                        // Применить сразу: убрать/вернуть кнопку программы на панели.
                        (Owner as MainWindow)?.ApplyExclusions();
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

        private bool _isUpdatingTint = false;
        private void ParseHexToTintSliders(string hex)
        {
            try
            {
                _isUpdatingTint = true;
                if (string.IsNullOrEmpty(hex)) hex = "#000000";
                if (!hex.StartsWith("#")) hex = "#" + hex;
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                TintSliderR.Value = c.R;
                TintSliderG.Value = c.G;
                TintSliderB.Value = c.B;
                TintPreview.Background = new System.Windows.Media.SolidColorBrush(c);
            }
            catch { }
            finally { _isUpdatingTint = false; }
        }

        private void TintRGB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingTint || this.DataContext is not DockSettings settings) return;
            byte r = (byte)TintSliderR.Value;
            byte g = (byte)TintSliderG.Value;
            byte b = (byte)TintSliderB.Value;
            settings.PeekTint = $"#{r:X2}{g:X2}{b:X2}";
            TintPreview.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        }

                private void SliderRGB_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingColor || this.DataContext is not DockSettings settings) return;

            byte r = (byte)SliderR.Value;
            byte g = (byte)SliderG.Value;
            byte b = (byte)SliderB.Value;

            settings.FollowWindowsColor = false; // ручной цвет отключает авто
            settings.BackgroundColor = $"#{r:X2}{g:X2}{b:X2}";
        }

        // ===== Язык =====
        private bool _fillingLang;
        private void FillLanguageCombo()
        {
            _fillingLang = true;
            CmbLanguage.Items.Clear();
            CmbLanguage.Items.Add(new ComboBoxItem { Content = Loc.T("lang_auto"), Tag = "auto" });
            for (int i = 0; i < Loc.Codes.Length; i++)
                CmbLanguage.Items.Add(new ComboBoxItem { Content = Loc.NativeNames[i], Tag = Loc.Codes[i] });
            string cur = GlobalConfig.Language;
            foreach (ComboBoxItem it in CmbLanguage.Items)
                if ((string)it.Tag == cur) { CmbLanguage.SelectedItem = it; break; }
            if (CmbLanguage.SelectedItem == null) CmbLanguage.SelectedIndex = 0;
            _fillingLang = false;
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fillingLang || CmbLanguage.SelectedItem is not ComboBoxItem it) return;
            GlobalConfig.Language = (string)it.Tag;
            Loc.Apply(GlobalConfig.Language);
            // Пункт «Авто» перевести на новый язык (его текст задан кодом, не ресурсом).
            if (CmbLanguage.Items[0] is ComboBoxItem auto) auto.Content = Loc.T("lang_auto");
        }

        private void Settings_ColorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DockSettings.BackgroundColor) || sender is not DockSettings s) return;
            Dispatcher.InvokeAsync(() => ParseHexToSliders(s.BackgroundColor));
        }
    }
}