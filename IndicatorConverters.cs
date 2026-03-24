using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PlankWin
{
    // Модель для одной ячейки (точки или полоски)
    public class IndicatorState
    {
        public bool IsDash { get; set; }
        public bool IsActive { get; set; }
    }

    public class AppIndicatorsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var list = new List<IndicatorState>();

            if (values == null || values.Length < 2)
                return list;

            int count = values[0] is int c ? c : 0;
            bool isActive = values[1] is bool b ? b : false;

            // Читаем индекс активного окна (третий параметр из XAML)
            int activeIndex = values.Length > 2 && values[2] is int idx ? idx : -1;

            if (count == 0) return list;

            if (count <= 3)
            {
                for (int i = 0; i < count; i++)
                {
                    // МАГИЯ: Зажигаем только ту точку, которая совпадает с индексом окна!
                    list.Add(new IndicatorState { IsDash = false, IsActive = (isActive && i == activeIndex) });
                }
            }
            else
            {
                list.Add(new IndicatorState { IsDash = true, IsActive = isActive });
            }

            return list;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class MarginConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double margin) return new Thickness(margin / 2);
            return new Thickness(0);
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public class DoubleToThicknessConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double val) return new Thickness(val);
            return new Thickness(0);
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public class SmartRadiusConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 4 || values[0] == DependencyProperty.UnsetValue || values[3] == DependencyProperty.UnsetValue)
                return new CornerRadius(0);

            double rawRadius = System.Convert.ToDouble(values[0]);
            bool isFloating = values[1] is bool b && b;
            DockPosition position = values[2] is DockPosition pos ? pos : DockPosition.Bottom;
            double thickness = System.Convert.ToDouble(values[3]);

            double safeRadius = Math.Min(rawRadius, thickness / 2.0);

            if (isFloating) return new CornerRadius(safeRadius);

            return position switch
            {
                DockPosition.Bottom => new CornerRadius(safeRadius, safeRadius, 0, 0),
                DockPosition.Top => new CornerRadius(0, 0, safeRadius, safeRadius),
                DockPosition.Left => new CornerRadius(0, safeRadius, safeRadius, 0),
                DockPosition.Right => new CornerRadius(safeRadius, 0, 0, safeRadius),
                _ => new CornerRadius(safeRadius)
            };
        }
        public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public class SmartIconMarginConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue) return new Thickness(0);

            double margin = System.Convert.ToDouble(values[0]);
            DockPosition position = values[1] is DockPosition pos ? pos : DockPosition.Bottom;

            double half = margin / 2.0;
            return position switch
            {
                DockPosition.Left or DockPosition.Right => new Thickness(0, half, 0, half),
                DockPosition.Top or DockPosition.Bottom => new Thickness(half, 0, half, 0),
                _ => new Thickness(half)
            };
        }
        public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public class HexToBrushConverter : System.Windows.Data.IMultiValueConverter, System.Windows.Data.IValueConverter
    {
        // 1. Старый метод для MultiBinding (Цвет + Прозрачность панели)
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            try
            {
                if (values == null || values.Length == 0 || values[0] == null)
                    return System.Windows.Media.Brushes.Transparent;

                string hex = values[0].ToString() ?? "#FFFFFF";

                // Если передали прозрачность вторым параметром - используем ее, иначе 1.0 (полностью непрозрачно)
                double opacity = 1.0;
                if (values.Length > 1 && values[1] is double d)
                {
                    opacity = d;
                }

                if (!hex.StartsWith("#")) hex = "#" + hex;
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                return new System.Windows.Media.SolidColorBrush(color) { Opacity = opacity };
            }
            catch
            {
                return System.Windows.Media.Brushes.Transparent;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        // =========================================================================
        // 2. НОВЫЙ МЕТОД для обычного Binding (Только Цвет, как у нашей точки!)
        // =========================================================================
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            // Просто перенаправляем одиночное значение в наш основной метод с дефолтной прозрачностью 1.0
            return Convert(new object[] { value, 1.0 }, targetType, parameter, culture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}