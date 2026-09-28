using System.Windows;

namespace WpfStudio.Preview.Tests.First
{
    public static class Options
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(int), typeof(Options), new PropertyMetadata(0));
        public static int GetMode(DependencyObject target) => (int)target.GetValue(ModeProperty);
        public static void SetMode(DependencyObject target, int value) => target.SetValue(ModeProperty, value);
    }
}

namespace WpfStudio.Preview.Tests.Second
{
    public static class Options
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(string), typeof(Options), new PropertyMetadata(""));
        public static string GetMode(DependencyObject target) => (string)target.GetValue(ModeProperty);
        public static void SetMode(DependencyObject target, string value) => target.SetValue(ModeProperty, value);
    }
}
