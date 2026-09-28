using System.Windows;

namespace WpfStudio.InspectionFixture.First
{
    public static class EditOptions
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(int), typeof(EditOptions), new FrameworkPropertyMetadata(0));
        public static int GetMode(DependencyObject target) => (int)target.GetValue(ModeProperty);
        public static void SetMode(DependencyObject target, int value) => target.SetValue(ModeProperty, value);
    }
}

namespace WpfStudio.InspectionFixture.Second
{
    public static class EditOptions
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(string), typeof(EditOptions), new FrameworkPropertyMetadata(""));
        public static string GetMode(DependencyObject target) => (string)target.GetValue(ModeProperty);
        public static void SetMode(DependencyObject target, string value) => target.SetValue(ModeProperty, value);
    }
}

namespace WpfStudio.InspectionFixture
{
    public static class EditPayloadOptions
    {
        public static readonly DependencyProperty[] Properties = Enumerable.Range(0, 160)
            .Select(index => DependencyProperty.RegisterAttached($"Payload{index:D3}", typeof(string),
                typeof(EditPayloadOptions), new FrameworkPropertyMetadata(""))).ToArray();
    }
}
