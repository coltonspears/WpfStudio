using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.InspectionFixture;

public partial class PrimaryWindow : Window
{
    public PrimaryWindow()
    {
        InitializeComponent();
        DataContext = new RuntimeViewModel();
    }

    public void ApplyBindingCommand(string command)
    {
        var model = (RuntimeViewModel)DataContext;
        switch (command)
        {
            case "binding-success": model.LifecycleSource = new ValueSource(); break;
            case "binding-failure": model.LifecycleSource = new MissingValueSource(); break;
            case "binding-pending": model.LifecycleSource = null; break;
            case "binding-delayed-ready": model.DelayedSource = new ValueSource(); break;
            case "binding-null-ready": model.OptionalChild = new ValueSource(); break;
            case "binding-replace": ReplacementText.SetBinding(TextBlock.TextProperty, new Binding(nameof(RuntimeViewModel.DisplayName))); break;
            case "binding-remove": BindingOperations.ClearBinding(RemovalText, TextBlock.TextProperty); break;
            case "binding-unload": DetachableContainer.Children.Remove(DetachableText); break;
            case "binding-reload": DetachableContainer.Children.Add(DetachableText); break;
            case "binding-bulk":
                for (int index = 0; index < 600; index++)
                {
                    var text = new TextBlock { Name = "BulkBinding" + index.ToString("D3") };
                    text.SetBinding(TextBlock.TextProperty, new Binding("Misspelled"));
                    BulkBindingContainer.Children.Add(text);
                }
                break;
        }
    }
}

public sealed class RuntimeViewModel : INotifyPropertyChanged
{
    private string _displayName = "Runtime data context";
    private object? _lifecycleSource = new MissingValueSource();
    private object? _delayedSource;
    private ValueSource? _optionalChild;
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; PropertyChanged?.Invoke(this, new(nameof(DisplayName))); }
    }
    public object? LifecycleSource { get => _lifecycleSource; set { _lifecycleSource = value; Changed(); } }
    public object? DelayedSource { get => _delayedSource; set { _delayedSource = value; Changed(); } }
    public ValueSource? OptionalChild { get => _optionalChild; set { _optionalChild = value; Changed(); } }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MissingValueSource { }
public sealed class ValueSource { public string Value => "Binding now resolves"; }

public static class AttachedBindingProbe
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached("Value", typeof(string),
        typeof(AttachedBindingProbe), new FrameworkPropertyMetadata(""));
    public static string GetValue(DependencyObject target) => (string)target.GetValue(ValueProperty);
    public static void SetValue(DependencyObject target, string value) => target.SetValue(ValueProperty, value);
}
