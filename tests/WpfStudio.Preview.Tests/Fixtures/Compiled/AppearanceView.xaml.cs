using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.PreviewFixture;

public partial class AppearanceView : UserControl
{
    public AppearanceView()
    {
        InitializeComponent();
        AppearanceProbe.Attach(AppearanceCounters);
        Resources.Add(new AppearanceKey(), "custom key value");
    }
}

public sealed class DeferredAppearanceValue
{
    public DeferredAppearanceValue() => AppearanceProbe.Constructed();
}

public sealed class AppearanceKey
{
    public override string ToString()
    {
        AppearanceProbe.Formatted();
        throw new InvalidOperationException("Inspection must not format a custom resource key.");
    }
}

internal static class AppearanceProbe
{
    private static WeakReference<TextBlock>? _display;
    private static int _constructed, _formatted;
    internal static void Attach(TextBlock display) { _display = new(display); Update(); }
    internal static void Constructed() { _constructed++; Update(); }
    internal static void Formatted() { _formatted++; Update(); }
    private static void Update()
    {
        if (_display?.TryGetTarget(out var display) == true)
            display.Text = $"constructed={_constructed}; formatted={_formatted}";
    }
}
