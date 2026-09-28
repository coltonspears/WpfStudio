using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.InspectionFixture;

internal sealed class EditingFixture(string directory, PrimaryWindow primary, Button childButton)
{
    private readonly Dictionary<string, EditProbe> _probes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditSource> _sources = new(StringComparer.Ordinal);
    private StackPanel? _container;
    private bool _agentContextObserved, _agentContextCollectible;
    private int _agentUnloading;

    public void Apply(string command)
    {
        if (!command.StartsWith("edit-", StringComparison.Ordinal)) return;
        if (command == "edit-create" && _container is null) Create();
        if (command == "edit-observe-agent") ObserveAgentContext();
        if (command == "edit-collect-agent")
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        if (command is "edit-observe-agent" or "edit-collect-agent" or "edit-agent-state") WriteAgentState();
        if (command is "edit-model-update" or "edit-model-update-again")
            foreach (var pair in _sources) pair.Value.UpdateFromModel(command + ": " + pair.Key);
        if (command == "edit-app-replace-binding")
        {
            var source = new EditSource("Application replaced binding");
            _sources["EditTwoWay"] = source;
            _probes["EditTwoWay"].SetBinding(EditProbe.ValueProperty, new Binding(nameof(EditSource.Value))
                { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        }
        if (command == "edit-app-change-literal") _probes["EditLiteral"].Value = "Application changed literal";
        if (command == "edit-large-payload")
        {
            // Reuse the application string; the regression concerns serialization
            // of independent properties, not allocating a large application model.
            string shared = new('\u6F22', 60000);
            foreach (var property in EditPayloadOptions.Properties) _probes["EditLiteral"].SetValue(property, shared);
            _probes["EditNullable"].LongValue = new string('\u754C', 65537);
        }
        if (command == "edit-remove-target") _container!.Children.Remove(_probes["EditLiteral"]);
        if (command == "edit-culture-french") CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        if (command == "edit-block-main")
        {
            File.WriteAllText(Path.Combine(directory, "edit-block-started"), "started");
            Thread.Sleep(1500);
        }
        WriteState();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ObserveAgentContext()
    {
        if (_agentContextObserved) return;
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(value => value.GetName().Name == "WpfStudio.Inspection.Agent");
        var context = AssemblyLoadContext.GetLoadContext(assembly)!;
        _agentContextCollectible = context.IsCollectible;
        // Context owns this callback; the fixture does not retain the context.
        // The no-inline frame must return before forced collection starts.
        context.Unloading += _ => Interlocked.Increment(ref _agentUnloading);
        _agentContextObserved = true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteAgentState()
    {
        var state = new
        {
            Observed = _agentContextObserved,
            Collectible = _agentContextCollectible,
            Unloading = Volatile.Read(ref _agentUnloading),
            DiagnosticsLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(value => value.GetName().Name == "WpfStudio.Wpf.Diagnostics")
        };
        var path = Path.Combine(directory, "agent-lifetime.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void Create()
    {
        _container = new StackPanel { Name = "EditingContainer" };
        _container.SetValue(EditProbe.InheritedValueProperty, "Inherited from parent");
        primary.PrimaryContent.Children.Add(_container);
        Add("EditLiteral").Value = "Literal original";
        Add("EditDefault");
        Add("EditInherited");
        Add("EditStyled").Style = new Style(typeof(EditProbe)) { Setters = { new Setter(EditProbe.ValueProperty, "Style original") } };
        var nullable = Add("EditNullable");
        nullable.LongValue = new string('L', 5000);
        nullable.Number = 12.5;
        nullable.PaddingValue = new Thickness(1, 2, 3, 4);
        AddBinding("EditTwoWay", BindingMode.TwoWay, fromStyle: false);
        AddBinding("EditStyleTwoWay", BindingMode.TwoWay, fromStyle: true);
        AddBinding("EditOneWayToSource", BindingMode.OneWayToSource, fromStyle: false);
        AddBinding("EditStyleOneWayToSource", BindingMode.OneWayToSource, fromStyle: true);
        var multiSource = new EditSource("Multi original");
        _sources["EditMulti"] = multiSource;
        var multi = new MultiBinding { Mode = BindingMode.OneWay, StringFormat = "{0} | {1}" };
        multi.Bindings.Add(new Binding(nameof(EditSource.Value)) { Source = multiSource });
        multi.Bindings.Add(new Binding { Source = "tail" });
        Add("EditMulti").SetBinding(EditProbe.ValueProperty, multi);
        var prioritySource = new EditSource("Priority original");
        _sources["EditPriority"] = prioritySource;
        var priority = new PriorityBinding();
        priority.Bindings.Add(new Binding("MissingCandidate") { Source = prioritySource });
        priority.Bindings.Add(new Binding(nameof(EditSource.Value)) { Source = prioritySource });
        Add("EditPriority").SetBinding(EditProbe.ValueProperty, priority);
        var collision = Add("EditCollision");
        First.EditOptions.SetMode(collision, 7);
        Second.EditOptions.SetMode(collision, "second original");
        primary.UpdateLayout();
    }

    private EditProbe Add(string name)
    {
        var probe = new EditProbe { Name = name, Width = 200, Height = 12,
            SlowStarted = () => File.WriteAllText(Path.Combine(directory, "edit-slow-started"), "started") };
        _probes.Add(name, probe);
        _container!.Children.Add(probe);
        return probe;
    }

    private void AddBinding(string name, BindingMode mode, bool fromStyle)
    {
        var source = new EditSource(name + " original");
        _sources[name] = source;
        var binding = new Binding(nameof(EditSource.Value))
        { Source = source, Mode = mode, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        var probe = Add(name);
        if (fromStyle) probe.Style = new Style(typeof(EditProbe)) { Setters = { new Setter(EditProbe.ValueProperty, binding) } };
        else probe.SetBinding(EditProbe.ValueProperty, binding);
    }

    private void WriteState()
    {
        var values = _probes.Values.Select(probe =>
        {
            var binding = BindingOperations.GetBindingBase(probe, EditProbe.ValueProperty);
            _sources.TryGetValue(probe.Name, out var source);
            return new
            {
                probe.Name, probe.Value, probe.NullableValue, probe.LongValue, probe.Number,
                PaddingValue = string.Join(",", new[] { probe.PaddingValue.Left, probe.PaddingValue.Top, probe.PaddingValue.Right, probe.PaddingValue.Bottom }
                    .Select(value => value.ToString(CultureInfo.InvariantCulture))), probe.InheritedValue,
                probe.ThrowingValue, probe.SlowValue, probe.ValueChanges,
                BindingKind = binding?.GetType().Name,
                BindingIdentity = binding is null ? 0 : RuntimeHelpers.GetHashCode(binding),
                SourceValue = source?.Value, SourceWrites = source?.Writes ?? 0,
                ValueSource = DependencyPropertyHelper.GetValueSource(probe, EditProbe.ValueProperty).BaseValueSource.ToString(),
                HasLocalValue = probe.ReadLocalValue(EditProbe.ValueProperty) != DependencyProperty.UnsetValue,
                FirstMode = First.EditOptions.GetMode(probe), SecondMode = Second.EditOptions.GetMode(probe)
            };
        }).ToArray();
        var path = Path.Combine(directory, "edit-state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
        {
            Probes = values, ChildWidth = childButton.Dispatcher.Invoke(() => childButton.Width)
        }));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

public sealed class EditSource(string value) : INotifyPropertyChanged
{
    private string _value = value;
    public int Writes { get; private set; }
    public string Value
    {
        get => _value;
        set { Writes++; _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
    }
    public void UpdateFromModel(string value) { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class EditProbe : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(EditProbe),
        new FrameworkPropertyMetadata("Default original", (target, _) => ((EditProbe)target).ValueChanges++));
    public static readonly DependencyProperty NullableValueProperty = DependencyProperty.Register(nameof(NullableValue), typeof(string), typeof(EditProbe), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty LongValueProperty = DependencyProperty.Register(nameof(LongValue), typeof(string), typeof(EditProbe), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(nameof(Number), typeof(double), typeof(EditProbe), new FrameworkPropertyMetadata(5d), value => value is double number && double.IsFinite(number) && number >= 0);
    public static readonly DependencyProperty PaddingValueProperty = DependencyProperty.Register(nameof(PaddingValue), typeof(Thickness), typeof(EditProbe), new FrameworkPropertyMetadata(default(Thickness)));
    public static readonly DependencyProperty InheritedValueProperty = DependencyProperty.RegisterAttached(nameof(InheritedValue), typeof(string), typeof(EditProbe), new FrameworkPropertyMetadata("Inherited default", FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty ThrowingValueProperty = DependencyProperty.Register(nameof(ThrowingValue), typeof(string), typeof(EditProbe), new FrameworkPropertyMetadata("safe", (_, args) =>
    {
        if (Equals(args.NewValue, "throw")) throw new InvalidOperationException("Fixture property callback failed.");
    }));
    public static readonly DependencyProperty SlowValueProperty = DependencyProperty.Register(nameof(SlowValue), typeof(string), typeof(EditProbe),
        new FrameworkPropertyMetadata("steady", (target, args) =>
        {
            if (!Equals(args.NewValue, "slow")) return;
            ((EditProbe)target).SlowStarted?.Invoke();
            Thread.Sleep(3500);
        }));
    public int ValueChanges { get; private set; }
    public Action? SlowStarted { get; set; }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? NullableValue { get => (string?)GetValue(NullableValueProperty); set => SetValue(NullableValueProperty, value); }
    public string LongValue { get => (string)GetValue(LongValueProperty); set => SetValue(LongValueProperty, value); }
    public double Number { get => (double)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public Thickness PaddingValue { get => (Thickness)GetValue(PaddingValueProperty); set => SetValue(PaddingValueProperty, value); }
    public string InheritedValue { get => (string)GetValue(InheritedValueProperty); set => SetValue(InheritedValueProperty, value); }
    public string ThrowingValue { get => (string)GetValue(ThrowingValueProperty); set => SetValue(ThrowingValueProperty, value); }
    public string SlowValue { get => (string)GetValue(SlowValueProperty); set => SetValue(SlowValueProperty, value); }
}
