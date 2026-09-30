using System.Windows;

namespace WpfStudio.PreviewFixture;

public static class FixtureWindowScope
{
    public static readonly DependencyProperty RequireWindowProperty = DependencyProperty.RegisterAttached(
        "RequireWindow", typeof(bool), typeof(FixtureWindowScope), new PropertyMetadata(false, (target, _) =>
        {
            if (target is not Window) throw new InvalidOperationException("This behavior requires a Window target.");
        }));
    public static bool GetRequireWindow(Window target) => (bool)target.GetValue(RequireWindowProperty);
    public static void SetRequireWindow(Window target, bool value) => target.SetValue(RequireWindowProperty, value);
}
