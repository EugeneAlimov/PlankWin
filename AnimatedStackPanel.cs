using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PlankWin
{
    public class AnimatedStackPanel : StackPanel
    {
        public static bool IsAnimationSuspended { get; set; } = false;

        private readonly TimeSpan _animationLength = TimeSpan.FromMilliseconds(200);
        private readonly Dictionary<UIElement, Point> _elementPositions = new();

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Size finalSize = base.ArrangeOverride(arrangeSize);
            var currentChildren = new HashSet<UIElement>();

            foreach (UIElement? child in InternalChildren)
            {
                if (child is not FrameworkElement fe) continue;

                currentChildren.Add(fe);

                Rect slot = LayoutInformation.GetLayoutSlot(fe);
                Point currentPos = slot.TopLeft;

                if (_elementPositions.TryGetValue(fe, out Point oldPos))
                {
                    if (oldPos != currentPos)
                    {
                        var transformGroup = fe.RenderTransform as TransformGroup;
                        TranslateTransform? trans = null;

                        if (transformGroup == null)
                        {
                            transformGroup = new TransformGroup();
                            if (fe.RenderTransform != null && fe.RenderTransform != Transform.Identity)
                                transformGroup.Children.Add(fe.RenderTransform);
                            fe.RenderTransform = transformGroup;
                        }

                        foreach (var t in transformGroup.Children)
                        {
                            if (t is TranslateTransform tt) { trans = tt; break; }
                        }

                        if (trans == null)
                        {
                            trans = new TranslateTransform();
                            transformGroup.Children.Add(trans);
                        }

                        if (IsAnimationSuspended)
                        {
                            trans.BeginAnimation(TranslateTransform.XProperty, null);
                            trans.BeginAnimation(TranslateTransform.YProperty, null);
                            trans.X = 0;
                            trans.Y = 0;
                        }
                        else
                        {
                            double startX = trans.X + (oldPos.X - currentPos.X);
                            double startY = trans.Y + (oldPos.Y - currentPos.Y);

                            trans.BeginAnimation(TranslateTransform.XProperty, null);
                            trans.BeginAnimation(TranslateTransform.YProperty, null);

                            trans.X = startX;
                            trans.Y = startY;

                            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                            var animX = new DoubleAnimation(0, _animationLength) { EasingFunction = ease };
                            var animY = new DoubleAnimation(0, _animationLength) { EasingFunction = ease };

                            trans.BeginAnimation(TranslateTransform.XProperty, animX);
                            trans.BeginAnimation(TranslateTransform.YProperty, animY);
                        }
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
                    else if (fe.RenderTransform is TransformGroup group)
                    {
                        foreach (var t in group.Children)
                        {
                            if (t is TranslateTransform trans)
                            {
                                trans.BeginAnimation(TranslateTransform.XProperty, null);
                                trans.BeginAnimation(TranslateTransform.YProperty, null);
                                trans.X = 0;
                                trans.Y = 0;
                            }
                        }
                    }
                }

                _elementPositions[fe] = currentPos;
            }

            var keysToRemove = new List<UIElement>();
            foreach (var key in _elementPositions.Keys)
            {
                if (!currentChildren.Contains(key)) keysToRemove.Add(key);
            }
            foreach (var key in keysToRemove)
            {
                _elementPositions.Remove(key);
            }

            return finalSize;
        }
    }
}