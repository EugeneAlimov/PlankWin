using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PlankWin
{
    // Перенос по строкам с выравниванием по ширине: элементы каждой строки растягиваются
    // так, чтобы строка заполняла всю доступную ширину (лишнее место делится поровну).
    // Используется для вкладок настроек — ширина чипов не «сбивается в кучу» на коротких языках.
    public class JustifiedWrapPanel : Panel
    {
        protected override Size MeasureOverride(Size available)
        {
            double maxW = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
            double rowW = 0, rowH = 0, totalH = 0, widest = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var d = c.DesiredSize;
                if (rowW > 0 && rowW + d.Width > maxW)
                {
                    widest = Math.Max(widest, rowW); totalH += rowH; rowW = 0; rowH = 0;
                }
                rowW += d.Width; rowH = Math.Max(rowH, d.Height);
            }
            widest = Math.Max(widest, rowW); totalH += rowH;
            return new Size(double.IsInfinity(available.Width) ? widest : available.Width, totalH);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var row = new List<UIElement>();
            double rowW = 0, rowH = 0, y = 0;

            void Flush()
            {
                if (row.Count == 0) return;
                double extra = Math.Max(0, (final.Width - rowW) / row.Count);
                double x = 0;
                foreach (var c in row)
                {
                    double w = c.DesiredSize.Width + extra;
                    c.Arrange(new Rect(x, y, w, rowH));
                    x += w;
                }
                y += rowH; row.Clear(); rowW = 0; rowH = 0;
            }

            foreach (UIElement c in InternalChildren)
            {
                var d = c.DesiredSize;
                if (row.Count > 0 && rowW + d.Width > final.Width) Flush();
                row.Add(c); rowW += d.Width; rowH = Math.Max(rowH, d.Height);
            }
            Flush();
            return new Size(final.Width, y);
        }
    }
}
