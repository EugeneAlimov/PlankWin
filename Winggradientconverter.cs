using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PlankWin
{
    // Строит "объёмный" градиент панели (профиль крыла) от базового цвета фона.
    // Гребень-блик у внешнего края (под иконками), склон-тень уходит внутрь панели.
    // Цвета — производные от BackgroundColor, поэтому объём сохраняется при смене цвета.
    public class WingGradientConverter : IMultiValueConverter
    {
        // Насколько осветлять гребень и затемнять склон (доли 0..1).
        private const double SlopeDarken = 0.1;  // затемнение к внутреннему краю (светлая тема)
        private const double SlopeLighten = 0.2; // осветление к внутреннему краю (тёмная тема)
        private const double AccentInVolume = 0.15; // доля акцента в склоне (0..1)

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            Color baseC = Colors.Gray;
            if (values.Length > 0 && values[0] is string hex && !string.IsNullOrEmpty(hex))
            {
                try { baseC = (Color)ColorConverter.ConvertFromString(hex); }
                catch { }
            }

            double op = 1.0;
            if (values.Length > 1 && values[1] is double d) op = Math.Clamp(d, 0.0, 1.0);
            else if (values.Length > 1 && values[1] is int di) op = Math.Clamp(di / 100.0, 0.0, 1.0);

            string pos = values.Length > 2 && values[2] != null ? values[2].ToString() ?? "Bottom" : "Bottom";

            byte a = (byte)(op * 255);
            Color mid = WithAlpha(baseC, a);
            // Тёмная тема — осветляем к внутреннему краю (свечение), светлая — затемняем (тень).
            Color slope = WithAlpha(IsWindowsDarkTheme() ? Lighten(baseC, SlopeLighten) : Darken(baseC, SlopeDarken), a);
            // Акцент Windows в объёме: подмешиваем приглушённый акцент к склону (внутреннему краю).
            bool useAccent = values.Length > 3 && values[3] is bool ua && ua;
            if (useAccent)
            {
                try
                {
                    var acc = (Color)ColorConverter.ConvertFromString(Win32Helper.GetAccentColorRaw());
                    double f = values.Length > 4 && values[4] is int ai ? ai / 50.0 : 1.0; // 50% = базовый вид
                    slope = WithAlpha(Blend(slope, acc, Math.Clamp(AccentInVolume * f, 0.0, 1.0)), a);
                }
                catch { }
            }

            var g = new LinearGradientBrush();
            g.GradientStops.Add(new GradientStop(mid, 0.0));
            g.GradientStops.Add(new GradientStop(mid, 0.55));
            g.GradientStops.Add(new GradientStop(slope, 1.0));

            // Направление поперёк панели: внешний край -> внутренний.
            switch (pos)
            {
                case "Left": g.StartPoint = new Point(0, 0); g.EndPoint = new Point(1, 0); break;
                case "Right": g.StartPoint = new Point(1, 0); g.EndPoint = new Point(0, 0); break;
                case "Top": g.StartPoint = new Point(0, 0); g.EndPoint = new Point(0, 1); break;
                default: g.StartPoint = new Point(0, 1); g.EndPoint = new Point(0, 0); break; // Bottom
            }
            return g;
        }

        private static Color Blend(Color a, Color b, double k)
        {
            byte M(byte x, byte y) => (byte)(x * (1.0 - k) + y * k);
            return Color.FromArgb(a.A, M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
        }

        private static bool IsWindowsDarkTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
            }
            catch { }
            return true;
        }

        private static Color Lighten(Color c, double f)
        {
            return Color.FromRgb(
                (byte)(c.R + (255 - c.R) * f),
                (byte)(c.G + (255 - c.G) * f),
                (byte)(c.B + (255 - c.B) * f));
        }

        private static Color Darken(Color c, double f)
        {
            return Color.FromRgb(
                (byte)(c.R * (1 - f)),
                (byte)(c.G * (1 - f)),
                (byte)(c.B * (1 - f)));
        }

        private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}