using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.PreviewFixture;

public sealed class BindingDiagnosticView : StackPanel
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(string),
        typeof(BindingDiagnosticView), new PropertyMetadata("", (target, args) => ((BindingDiagnosticView)target).Execute((string)args.NewValue)));
    public string Command { get => (string)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    private readonly DiagnosticRoot _missing = new(new DiagnosticMissing());
    private readonly DiagnosticRoot _null = new(null);
    private readonly DiagnosticRoot _doNothing = new("Initial accepted value");
    private readonly DiagnosticTarget _validation;
    private readonly TextBlock _counters = new() { Name = "Counters" };

    public BindingDiagnosticView()
    {
        Name = "DiagnosticRoot";
        Add("Missing", new Binding("Child.Value") { Source = _missing });
        Add("Null", new Binding("Child.Value") { Source = _null });
        Add("Fallback", new Binding("Child.Value") { Source = _missing, FallbackValue = "Fallback displayed" });
        Add("TargetNull", new Binding(nameof(DiagnosticChild.Nullable)) { Source = new DiagnosticChild(), TargetNullValue = "Null displayed" });
        Add("Stable", new Binding("Child.Value") { Source = new DiagnosticRoot(new DiagnosticChild()), Converter = new DiagnosticConverter("pass") });
        Add("Descriptor", new Binding("Value") { Source = new DiagnosticDescriptorSource() });
        Add("ConverterUnset", new Binding("Value") { Source = new DiagnosticChild(), Converter = new DiagnosticConverter("unset") });
        Add("ConverterDoNothing", new Binding("Child") { Source = _doNothing, Converter = new DiagnosticConverter("nothing") });
        var conversion = new DiagnosticTarget { Name = "Conversion" };
        conversion.SetBinding(DiagnosticTarget.NumberProperty, new Binding { Source = "not a number" });
        Children.Add(conversion);
        var multi = new MultiBinding { StringFormat = "{0} {1}" };
        multi.Bindings.Add(new Binding("Value") { Source = new DiagnosticChild() });
        multi.Bindings.Add(new Binding("Child.Value") { Source = _missing });
        Add("Multi", multi);
        var priority = new PriorityBinding();
        priority.Bindings.Add(new Binding("Value") { Source = new DiagnosticChild() });
        priority.Bindings.Add(new Binding("Absent") { Source = new DiagnosticChild() });
        priority.Bindings.Add(new Binding("UnusedAbsent") { Source = new DiagnosticChild() });
        Add("Priority", priority);
        var priorityAfterFailure = new PriorityBinding();
        priorityAfterFailure.Bindings.Add(new Binding("Absent") { Source = new DiagnosticChild() });
        priorityAfterFailure.Bindings.Add(new Binding("Value") { Source = new DiagnosticChild() });
        Add("PriorityAfterFailure", priorityAfterFailure);
        var validation = new Binding("Value") { Source = new DiagnosticWritable(), Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.Explicit };
        validation.ValidationRules.Add(new DiagnosticRule());
        _validation = Add("Validation", validation);
        Children.Add(_counters);
    }

    private DiagnosticTarget Add(string name, BindingBase binding)
    {
        var target = new DiagnosticTarget { Name = name };
        target.SetBinding(DiagnosticTarget.TextProperty, binding);
        Children.Add(target);
        return target;
    }

    private void Execute(string command)
    {
        if (command == "null-intermediate") _missing.Child = null;
        if (command == "do-nothing") _doNothing.Child = "Skip transfer";
        if (command == "recover") { _missing.Child = new DiagnosticChild(); _null.Child = new DiagnosticChild(); }
        if (command == "validate")
        {
            _validation.SetCurrentValue(DiagnosticTarget.TextProperty, "invalid");
            _validation.GetBindingExpression(DiagnosticTarget.TextProperty)!.UpdateSource();
        }
        _counters.Text = JsonSerializer.Serialize(new { DiagnosticCounts.Gets, DiagnosticCounts.Conversions,
            DiagnosticCounts.DescriptorNames, DiagnosticCounts.DescriptorGets, DiagnosticCounts.Writes, DiagnosticCounts.ToStrings });
    }
}

public sealed class DiagnosticTarget : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(DiagnosticTarget), new PropertyMetadata(""));
    public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(nameof(Number), typeof(double), typeof(DiagnosticTarget), new PropertyMetadata(0d));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Number { get => (double)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
}
internal static class DiagnosticCounts { internal static int Gets, Conversions, DescriptorNames, DescriptorGets, Writes, ToStrings; }
public sealed class DiagnosticRoot(object? child) : INotifyPropertyChanged
{
    private object? _child = child;
    public event PropertyChangedEventHandler? PropertyChanged;
    public object? Child { get { DiagnosticCounts.Gets++; return _child; } set { _child = value; PropertyChanged?.Invoke(this, new(nameof(Child))); } }
}
public sealed class DiagnosticMissing { }
public sealed class DiagnosticChild
{
    public string Value { get { DiagnosticCounts.Gets++; return "Healthy value"; } }
    public string? Nullable { get { DiagnosticCounts.Gets++; return null; } }
}
public sealed class DiagnosticWritable
{
    public string Value { get { DiagnosticCounts.Gets++; return "Initial"; } set { DiagnosticCounts.Writes++; } }
}
public sealed class DiagnosticConverter(string mode) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    { DiagnosticCounts.Conversions++; return mode == "unset" ? DependencyProperty.UnsetValue : mode == "nothing" && value is "Skip transfer" ? Binding.DoNothing : value; }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class DiagnosticRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) => new(false, new DiagnosticOpaqueError());
}
public sealed class DiagnosticOpaqueError
{
    public override string ToString() { DiagnosticCounts.ToStrings++; return "Application error content"; }
}
public sealed class DiagnosticDescriptorSource : CustomTypeDescriptor
{
    private static readonly PropertyDescriptorCollection Properties = new([new DiagnosticValueDescriptor()]);
    public override PropertyDescriptorCollection GetProperties() => Properties;
    public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => Properties;
    public override object GetPropertyOwner(PropertyDescriptor? pd) => this;
}
public sealed class DiagnosticValueDescriptor() : PropertyDescriptor("Value", null)
{
    public override string Name { get { DiagnosticCounts.DescriptorNames++; return base.Name; } }
    public override Type ComponentType => typeof(DiagnosticDescriptorSource);
    public override bool IsReadOnly => true;
    public override Type PropertyType => typeof(string);
    public override bool CanResetValue(object component) => false;
    public override object GetValue(object? component) { DiagnosticCounts.DescriptorGets++; return "Descriptor value"; }
    public override void ResetValue(object component) => throw new NotSupportedException();
    public override void SetValue(object? component, object? value) => throw new NotSupportedException();
    public override bool ShouldSerializeValue(object component) => false;
}
