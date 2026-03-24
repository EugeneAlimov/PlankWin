using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media; // Обязательно для генерации градиентов

namespace PlankWin
{
    public class DockMorphEngineConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 3 || !(values[0] is double T) || !(values[1] is double S))
                return new Thickness(0);

            string pos = values[2]?.ToString() ?? "Bottom";
            string param = parameter as string ?? "";
            bool isVert = pos == "Left" || pos == "Right";

            // Коэффициент масштабирования (базовый размер иконки = 44px)
            double k = S / 44.0;

            // 1. ПАРАМЕТРЫ ФОРМЫ (Масштабируемые)
            double H_ideal = S + Math.Max(18.0, 30.0 * k); // Твои базовые 34px

            double m = 0.0;
            if (values.Length >= 4 && values[3] is double progress) m = progress;
            else m = Math.Clamp((H_ideal - T) / 20.0, 0, 1.0);

            // 2. ФИЗИКА ВЫДЕЛЕНИЯ
            double outerCross = H_ideal - ((H_ideal - T) * m);

            double outerLongIdeal = S + Math.Max(10.0, 10.0 * k);
            double outerLong = outerLongIdeal - ((outerLongIdeal - T) * m);

            double outerMargin = (T - outerCross) / 2.0;
            double standCenter = outerMargin + (outerCross / 2.0);

            // 3. ФИЗИКА ТОЧЕК
            // Твои базовые -20px от края выделения
            double dotPanelOffset = Math.Max(10.0, 20.0 * k);
            double dotPanel = outerMargin + outerCross - dotPanelOffset;

            // Твои базовые -3px центровки в полочке
            double dotShelf = standCenter - (3.0 * k);

            double dotOffset = (m < 0.5) ? dotPanel : dotShelf;
            double dotOpacity = Math.Clamp(Math.Abs(m - 0.5) * 4.0, 0.0, 1.0);

            // 4. ФИЗИКА ИКОНКИ
            // базовые 5px зазора между точкой и иконкой
            double iconGap = Math.Max(2.0, 4.0 * k);
            double iconStart = dotPanel - iconGap - S;

            // базовые 10px левитации в полочке
            double iconEnd = standCenter + (10.0 * k);
            double iconOffset = iconStart + (iconEnd - iconStart) * m;

            // 5. ГАБАРИТЫ ЯЧЕЙКИ
            double cellLength = outerLongIdeal;
            double cellDepthMax = Math.Max(T, Math.Max(outerCross, iconOffset + S + Math.Max(4.0, 8.0 * k)));

            switch (param)
            {
                case "CellW": return isVert ? cellDepthMax : cellLength;
                case "CellH": return isVert ? cellLength : cellDepthMax;
                case "OuterW": return isVert ? outerCross : outerLong;
                case "OuterH": return isVert ? outerLong : outerCross;
                case "OuterMargin": return CalculateMargin(outerMargin, pos);
                case "IconMargin": return CalculateMargin(iconOffset, pos);
                case "IndicatorMargin": return CalculateMargin(dotOffset, pos);
                case "IndicatorOpacity": return dotOpacity;

                // Масштабируем скругление краев
                case "OuterRadius":
                    double baseRadius = Math.Max(6.0, 12.0 * k);
                    return new CornerRadius(baseRadius + ((outerCross / 2.0) - baseRadius) * m);

                // --- МАГИЯ ОТРАЖЕНИЙ ---
                case "ReflectMargin":
                    // зазор отражения 8px
                    double gap = Math.Max(2.0, 8.0 * k);
                    return pos switch
                    {
                        "Bottom" => new Thickness(0, S + gap, 0, -(S + gap)),
                        "Top" => new Thickness(0, -(S + gap), 0, S + gap),
                        "Left" => new Thickness(-(S + gap), 0, S + gap, 0),
                        "Right" => new Thickness(S + gap, 0, -(S + gap), 0),
                        _ => new Thickness(0)
                    };

                case "ReflectTransform":
                    ScaleTransform scale = new ScaleTransform();
                    switch (pos)
                    {
                        case "Bottom": scale.ScaleX = 1; scale.ScaleY = -1; break;
                        case "Top": scale.ScaleX = 1; scale.ScaleY = -1; break;
                        case "Left": scale.ScaleX = -1; scale.ScaleY = 1; break;
                        case "Right": scale.ScaleX = -1; scale.ScaleY = 1; break;
                    }
                    return scale;

                case "ReflectMask":
                    // зазор гильотины 8px
                    double rGap = Math.Max(2.0, 8.0 * k);
                    double maxSpace = Math.Max(0, iconOffset - rGap);
                    double fadeOffset = Math.Min(0.85, maxSpace / S);

                    LinearGradientBrush brush = new LinearGradientBrush();
                    // стартовая прозрачность (70)
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(100, 255, 255, 255), 0.0));
                    brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), fadeOffset));

                    switch (pos)
                    {
                        case "Bottom": brush.StartPoint = new Point(0, 1); brush.EndPoint = new Point(0, 0); break;
                        case "Top": brush.StartPoint = new Point(0, 0); brush.EndPoint = new Point(0, 1); break;
                        case "Left": brush.StartPoint = new Point(0, 0); brush.EndPoint = new Point(1, 0); break;
                        case "Right": brush.StartPoint = new Point(1, 0); brush.EndPoint = new Point(0, 0); break;
                    }
                    return brush;

                default: return 0.0;
            }
        }

        private Thickness CalculateMargin(double offset, string pos)
        {
            return pos switch
            {
                "Bottom" => new Thickness(0, 0, 0, offset),
                "Top" => new Thickness(0, offset, 0, 0),
                "Left" => new Thickness(offset, 0, 0, 0),
                "Right" => new Thickness(0, 0, offset, 0),
                _ => new Thickness(0)
            };
        }

        public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotImplementedException();
    }
}