using System;
using System.Globalization;
using System.Windows.Data;

namespace PlankWin
{
    /// <summary>
    /// Возвращает true, только если режим видимости — AlwaysVisible.
    /// Используется, чтобы галочка "Резервировать место на экране" была активна
    /// только в режиме "Всегда видима" (в режимах автоскрытия резервирование не имеет смысла).
    /// </summary>
    public class IsAlwaysVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is DockVisibilityMode mode && mode == DockVisibilityMode.AlwaysVisible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
