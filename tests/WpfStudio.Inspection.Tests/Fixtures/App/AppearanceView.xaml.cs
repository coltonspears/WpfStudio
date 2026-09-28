using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.InspectionFixture;

public partial class AppearanceView : UserControl
{
    public AppearanceView() => InitializeComponent();
    public void PrefixChildNames()
    {
        foreach (var child in Body.Children.OfType<FrameworkElement>()) child.Name = "Child" + child.Name;
    }
}

public sealed class AppearanceDeferredValue
{
    public static int Constructors;
    public static int Formattings;
    public AppearanceDeferredValue() => Interlocked.Increment(ref Constructors);
    public override string ToString() { Interlocked.Increment(ref Formattings); throw new InvalidOperationException("Do not inspect resource values."); }
}

public sealed class AppearanceProbe : FrameworkElement
{
    private static readonly DependencyPropertyKey ReadOnlyTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ReadOnlyText), typeof(string), typeof(AppearanceProbe), new PropertyMetadata("Read-only evidence"));
    public static readonly DependencyProperty ReadOnlyTextProperty = ReadOnlyTextPropertyKey.DependencyProperty;
    public string ReadOnlyText => (string)GetValue(ReadOnlyTextProperty);
}

internal sealed class AppearanceFixture
{
    private readonly string _directory;
    private readonly PrimaryWindow _primary;
    private AppearanceView? _main;
    private WeakReference? _removed;
    public AppearanceFixture(string directory, PrimaryWindow primary, Button childButton)
    {
        _directory = directory; _primary = primary;
        _main = new AppearanceView();
        primary.PrimaryContent.Children.Add(_main);
        childButton.Dispatcher.Invoke(() =>
        {
            var child = new AppearanceView();
            child.PrefixChildNames();
            ((StackPanel)childButton.Parent).Children.Add(child);
        });
    }

    public void Apply(string command)
    {
        if (!command.StartsWith("appearance-", StringComparison.Ordinal)) return;
        if (command == "appearance-unload" && _main is { } main)
        {
            _primary.PrimaryContent.Children.Remove(main);
            _removed = new WeakReference(main); _main = null;
        }
        if (command == "appearance-collect")
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        File.WriteAllText(Path.Combine(_directory, "appearance-state.json"), JsonSerializer.Serialize(new
        { AppearanceDeferredValue.Constructors, AppearanceDeferredValue.Formattings, RemovedAlive = _removed?.IsAlive ?? false }));
    }
}
