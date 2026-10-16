using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PlankWin
{
    public class EvenWrapPanel : Panel
    {
        private readonly TimeSpan _animationLength = TimeSpan.FromMilliseconds(200);
        private readonly Dictionary<UIElement, Point> _elementPositions = new();

        public UIElement? DraggedChild { get; set; }

        private void ItemSize(out double w, out double h)
        {
            w = 0; h = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child.DesiredSize.Width > w) w = child.DesiredSize.Width;
                if (child.DesiredSize.Height > h) h = child.DesiredSize.Height;
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            ItemSize(out double iw, out double ih);
            int count = InternalChildren.Count;
            if (count == 0 || iw <= 0) return new Size(0, 0);

            double aw = double.IsInfinity(availableSize.Width) ? iw * count : availableSize.Width;
            int perRow = Math.Max(1, (int)(aw / iw));
            int rows = (count + perRow - 1) / perRow;
            double height = rows * ih;
            return new Size(double.IsInfinity(availableSize.Width) ? iw * Math.Min(count, perRow) : aw, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int count = InternalChildren.Count;
            if (count == 0) { _elementPositions.Clear(); return finalSize; }

            ItemSize(out double iw, out double ih);
            if (iw <= 0) return finalSize;

            int perRow = Math.Max(1, (int)(finalSize.Width / iw));
            double free = Math.Max(0, finalSize.Width - perRow * iw);
            double gap = free / (perRow + 1);

            var current = new HashSet<UIElement>();
            for (int i = 0; i < count; i++)
            {
                int row = i / perRow;
                int col = i % perRow;
                double x = gap + col * (iw + gap);
                double y = row * ih;
                var child = InternalChildren[i];
                child.Arrange(new Rect(x, y, iw, ih));

                if (child is FrameworkElement fe)
                {
                    current.Add(fe);
                    AnimateTo(fe, new Point(x, y));
                }
            }

            var toRemove = new List<UIElement>();
            foreach (var key in _elementPositions.Keys)
                if (!current.Contains(key)) toRemove.Add(key);
            foreach (var key in toRemove) _elementPositions.Remove(key);

            return finalSize;
        }

        private void AnimateTo(FrameworkElement fe, Point currentPos)
        {
            if (fe == DraggedChild)
            {
                if (fe.RenderTransform is TransformGroup dg)
                    foreach (var t in dg.Children)
                        if (t is TranslateTransform dt)
                        {
                            dt.BeginAnimation(TranslateTransform.XProperty, null);
                            dt.BeginAnimation(TranslateTransform.YProperty, null);
                            dt.X = 0; dt.Y = 0;
                        }
                _elementPositions[fe] = currentPos;
                return;
            }
            if (_elementPositions.TryGetValue(fe, out Point oldPos))
            {
                if (oldPos != currentPos)
                {
                    var group = fe.RenderTransform as TransformGroup;
                    if (group == null)
                    {
                        group = new TransformGroup();
                        if (fe.RenderTransform != null && fe.RenderTransform != Transform.Identity)
                            group.Children.Add(fe.RenderTransform);
                        fe.RenderTransform = group;
                    }

                    TranslateTransform? trans = null;
                    foreach (var t in group.Children)
                        if (t is TranslateTransform tt) { trans = tt; break; }
                    if (trans == null)
                    {
                        trans = new TranslateTransform();
                        group.Children.Add(trans);
                    }

                    double startX = trans.X + (oldPos.X - currentPos.X);
                    double startY = trans.Y + (oldPos.Y - currentPos.Y);

                    trans.BeginAnimation(TranslateTransform.XProperty, null);
                    trans.BeginAnimation(TranslateTransform.YProperty, null);
                    trans.X = startX;
                    trans.Y = startY;

                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    trans.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, _animationLength) { EasingFunction = ease });
                    trans.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, _animationLength) { EasingFunction = ease });
                }
            }
            else
            {
                if (fe.RenderTransform is not TransformGroup)
                {
                    var group = new TransformGroup();
                    group.Children.Add(new TranslateTransform(0, 0));
                    fe.RenderTransform = group;
                }
            }

            _elementPositions[fe] = currentPos;
        }
    }
}