using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PlankWin
{
    public class SmartContrastConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string target = parameter as string ?? "Dot";

            if (value is string hexColor && !string.IsNullOrEmpty(hexColor))
            {
                try
                {
                    if (!hexColor.StartsWith("#")) hexColor = "#" + hexColor;
                    Color bg = (Color)ColorConverter.ConvertFromString(hexColor);

                    // Формула вычисления относительной яркости (Luminance)
                    double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B);

                    // Если яркость больше 140 — фон считается светлым
                    bool isLight = luminance > 140;

                    if (target == "Dot")
                    {
                        // Для точек и полосок: темно-серый на светлом, чисто белый на темном
                        return isLight ? new SolidColorBrush(Color.FromArgb(255, 60, 60, 60))
                                       : new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
                    }
                    if (target == "Highlight")
                    {
                        if (isLight)
                        {
                            // Для светлой панели (оставляем как есть, работает супер)
                            return new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
                        }
                        else
                        {
                            // ДЛЯ ТЕМНОЙ ПАНЕЛИ:
                            // Берем родной цвет панели (bg) и делаем его на 45 оттенков светлее
                            byte r = (byte)Math.Min(255, bg.R + 45);
                            byte g = (byte)Math.Min(255, bg.G + 45);
                            byte b = (byte)Math.Min(255, bg.B + 45);

                            // Задаем высокую плотность (например 130 или 150). 
                            // Это добавит "густоты" цвету и навсегда уберет эффект прозрачной дырки!
                            return new SolidColorBrush(Color.FromArgb(130, r, g, b));
                        }
                    }
                }
                catch { }
            }

            // Безопасный дефолт (для темной панели)
            return target == "Dot" ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}