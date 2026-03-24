using System.Windows;

namespace PlankWin
{
    public static class DockPhysics
    {
        // Прикрепляемое свойство MorphProgress
        public static readonly DependencyProperty MorphProgressProperty =
            DependencyProperty.RegisterAttached("MorphProgress", typeof(double), typeof(DockPhysics),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static double GetMorphProgress(DependencyObject obj) => (double)obj.GetValue(MorphProgressProperty);
        public static void SetMorphProgress(DependencyObject obj, double value) => obj.SetValue(MorphProgressProperty, value);
    }
}