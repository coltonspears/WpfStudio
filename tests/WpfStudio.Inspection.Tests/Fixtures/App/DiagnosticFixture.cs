using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.InspectionFixture;

internal sealed class DiagnosticFixture(string directory, PrimaryWindow primary)
{
    private readonly Dictionary<string, DiagnosticProbe> _probes = new();
    private StackPanel? _container;
    private DiagnosticRoot? _missingRoot;

    public void Apply(string command)
    {
        if (!command.StartsWith("diagnostic-", StringComparison.Ordinal)) return;
        if (command == "diagnostic-create" && _container is null) Create();
        if (command == "diagnostic-same-root-null") _missingRoot!.Child = null;
        if (command == "diagnostic-new-source") _probes["DiagnosticMissing"].DataContext = new DiagnosticRoot(new DiagnosticChild());
        if (command == "diagnostic-validation")
        {
            _probes["DiagnosticConversionValidation"].SetCurrentValue(DiagnosticProbe.TextProperty, "not an integer");
            _probes["DiagnosticConversionValidation"].GetBindingExpression(DiagnosticProbe.TextProperty)!.UpdateSource();
            _probes["DiagnosticExceptionValidation"].SetCurrentValue(DiagnosticProbe.TextProperty, "not an integer");
            _probes["DiagnosticExceptionValidation"].GetBindingExpression(DiagnosticProbe.TextProperty)!.UpdateSource();
            _probes["DiagnosticRuleValidation"].SetCurrentValue(DiagnosticProbe.TextProperty, "invalid proposal");
            _probes["DiagnosticRuleValidation"].GetBindingExpression(DiagnosticProbe.TextProperty)!.UpdateSource();
            _probes["DiagnosticOpaqueValidation"].SetCurrentValue(DiagnosticProbe.TextProperty, "invalid proposal");
            _probes["DiagnosticOpaqueValidation"].GetBindingExpression(DiagnosticProbe.TextProperty)!.UpdateSource();
        }
        if (command == "diagnostic-many-failures")
            for (int i = 0; i < 12; i++) _missingRoot!.Child = i % 2 == 0 ? null : new DiagnosticMissingChild();
        File.WriteAllText(Path.Combine(directory, "diagnostic-state.json"), JsonSerializer.Serialize(new
        {
            DiagnosticCounters.Gets, DiagnosticCounters.Conversions, DiagnosticCounters.OpaqueToStrings, DiagnosticCounters.SourceWrites,
            DiagnosticCounters.DescriptorNames, DiagnosticCounters.DescriptorGets
        }));
    }

    private void Create()
    {
        _container = new StackPanel { Name = "DiagnosticContainer" };
        primary.PrimaryContent.Children.Add(_container);
        _missingRoot = new(new DiagnosticMissingChild());
        Add("DiagnosticMissing", _missingRoot).SetBinding(DiagnosticProbe.TextProperty, new Binding("Child.Value"));
        Add("DiagnosticNull", new DiagnosticRoot(null)).SetBinding(DiagnosticProbe.TextProperty, new Binding("Child.Value"));
        Add("DiagnosticStable", new DiagnosticRoot(new DiagnosticChild())).SetBinding(DiagnosticProbe.TextProperty,
            new Binding("Child.Value") { Converter = new DiagnosticConverter(), Mode = BindingMode.OneWay });
        Add("DiagnosticDescriptor", new DiagnosticDescriptorSource()).SetBinding(DiagnosticProbe.TextProperty, new Binding("Value"));
        Add("DiagnosticFallback", _missingRoot).SetBinding(DiagnosticProbe.TextProperty,
            new Binding("Child.Value") { FallbackValue = "Fallback displayed" });
        Add("DiagnosticTargetNull", new DiagnosticRoot(null)).SetBinding(DiagnosticProbe.TextProperty,
            new Binding("Child") { TargetNullValue = "Null displayed" });
        var multi = new MultiBinding { StringFormat = "{0} {1}" };
        multi.Bindings.Add(new Binding("Value") { Source = new DiagnosticChild() });
        multi.Bindings.Add(new Binding("Child.Value") { Source = _missingRoot });
        BindingOperations.SetBinding(Add("DiagnosticMulti", null), DiagnosticProbe.TextProperty, multi);
        var priority = new PriorityBinding();
        priority.Bindings.Add(new Binding("Value") { Source = new DiagnosticChild() });
        priority.Bindings.Add(new Binding("Missing") { Source = new DiagnosticChild() });
        priority.Bindings.Add(new Binding("UnusedMissing") { Source = new DiagnosticChild() });
        BindingOperations.SetBinding(Add("DiagnosticPriority", null), DiagnosticProbe.TextProperty, priority);
        var priorityAfterFailure = new PriorityBinding();
        priorityAfterFailure.Bindings.Add(new Binding("Missing") { Source = new DiagnosticChild() });
        priorityAfterFailure.Bindings.Add(new Binding("Value") { Source = new DiagnosticChild() });
        BindingOperations.SetBinding(Add("DiagnosticPriorityAfterFailure", null), DiagnosticProbe.TextProperty, priorityAfterFailure);
        Add("DiagnosticTransfer", null).SetBinding(DiagnosticProbe.NumberProperty,
            new Binding { Source = "definitely not a number" });
        Add("DiagnosticConversionValidation", null).SetBinding(DiagnosticProbe.TextProperty,
            new Binding(nameof(DiagnosticNumberSource.Number)) { Source = new DiagnosticNumberSource(), Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.Explicit });
        Add("DiagnosticExceptionValidation", null).SetBinding(DiagnosticProbe.TextProperty,
            new Binding(nameof(DiagnosticNumberSource.Number)) { Source = new DiagnosticNumberSource(), Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.Explicit, ValidatesOnExceptions = true });
        AddRule("DiagnosticRuleValidation", opaque: false);
        AddRule("DiagnosticOpaqueValidation", opaque: true);
        primary.UpdateLayout();
    }

    private void AddRule(string name, bool opaque)
    {
        var binding = new Binding(nameof(DiagnosticNumberSource.Text)) { Source = new DiagnosticNumberSource(),
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit };
        binding.ValidationRules.Add(new DiagnosticRule(opaque));
        Add(name, null).SetBinding(DiagnosticProbe.TextProperty, binding);
    }

    private DiagnosticProbe Add(string name, object? source)
    {
        var result = new DiagnosticProbe { Name = name, DataContext = source };
        _container!.Children.Add(result);
        _probes.Add(name, result);
        return result;
    }
}

public sealed class DiagnosticProbe : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(DiagnosticProbe), new PropertyMetadata(""));
    public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(nameof(Number), typeof(double), typeof(DiagnosticProbe), new PropertyMetadata(0d));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Number { get => (double)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
}

internal static class DiagnosticCounters { public static int Gets, Conversions, OpaqueToStrings, SourceWrites, DescriptorNames, DescriptorGets; }
public sealed class DiagnosticRoot(object? child) : INotifyPropertyChanged
{
    private object? _child = child;
    public event PropertyChangedEventHandler? PropertyChanged;
    public object? Child
    {
        get { DiagnosticCounters.Gets++; return _child; }
        set { _child = value; PropertyChanged?.Invoke(this, new(nameof(Child))); }
    }
}
public sealed class DiagnosticMissingChild { }
public sealed class DiagnosticChild
{
    public string Value { get { DiagnosticCounters.Gets++; return "Value resolved"; } }
}
public sealed class DiagnosticNumberSource
{
    private int _number = 17;
    private string _text = "Original";
    public int Number { get { DiagnosticCounters.Gets++; return _number; } set { DiagnosticCounters.SourceWrites++; _number = value; } }
    public string Text { get { DiagnosticCounters.Gets++; return _text; } set { DiagnosticCounters.SourceWrites++; _text = value; } }
}
public sealed class DiagnosticConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) { DiagnosticCounters.Conversions++; return value; }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class DiagnosticRule(bool opaque) : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) => new(false, opaque ? new DiagnosticOpaqueContent() : "The application rejected this proposal.");
}
public sealed class DiagnosticOpaqueContent
{
    public override string ToString() { DiagnosticCounters.OpaqueToStrings++; throw new InvalidOperationException("Diagnostic content must not be formatted by tooling."); }
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
    public override string Name { get { DiagnosticCounters.DescriptorNames++; return base.Name; } }
    public override Type ComponentType => typeof(DiagnosticDescriptorSource);
    public override bool IsReadOnly => true;
    public override Type PropertyType => typeof(string);
    public override bool CanResetValue(object component) => false;
    public override object GetValue(object? component) { DiagnosticCounters.DescriptorGets++; return "Descriptor value"; }
    public override void ResetValue(object component) => throw new NotSupportedException();
    public override void SetValue(object? component, object? value) => throw new NotSupportedException();
    public override bool ShouldSerializeValue(object component) => false;
}
