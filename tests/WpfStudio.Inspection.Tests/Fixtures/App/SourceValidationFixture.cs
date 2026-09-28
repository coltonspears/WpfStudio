using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace WpfStudio.InspectionFixture
{
    internal sealed class SourceValidationFixture(string directory, PrimaryWindow primary)
    {
        private StackPanel? _container;
        private SourceValidationProbe? _probe;
        public void Apply(string command)
        {
            if (!command.StartsWith("source-validation-", StringComparison.Ordinal)) return;
            if (command == "source-validation-create" && _container is null) Create();
            if (command == "source-validation-change") _probe!.Value = "Changed by application";
            if (command == "source-validation-remove") _container!.Children.Remove(_probe!);
            if (command == "source-validation-french") CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            File.WriteAllText(Path.Combine(directory, "source-validation-state.json"), JsonSerializer.Serialize(new
            {
                Value = _probe?.GetValue(SourceValidationProbe.ValueProperty), Guarded = _probe?.GetValue(SourceValidationProbe.GuardedProperty),
                SourceValidationCounters.Gets, SourceValidationCounters.Writes, SourceValidationCounters.ValidationCalls,
                SourceValidationCounters.ConverterCalls, SourceValidationCounters.AttributeConstructions, SourceValidationCounters.WrapperCalls
            }));
        }

        private void Create()
        {
            _container = new StackPanel { Name = "SourceValidationContainer" };
            primary.PrimaryContent.Children.Add(_container);
            _probe = new SourceValidationProbe { Name = "SourceValidationProbe", Value = "Original source value", Number = 17 };
            SourceValidationProbe.LastTarget = new(_probe);
            _container.Children.Add(_probe);
            SourceA.SourceOptions.SetMode(_probe, 3);
            SourceB.SourceOptions.SetMode(_probe, "other provider");
            var inherited = new SourceInheritedProbe { Name = "SourceInheritedProbe", SharedValue = "Inherited value", RuntimeOnly = "Subclass property" };
            _container.Children.Add(inherited);
            var button = new Button { Name = "SourceStandardButton", Content = "Normal button", Width = 120 };
            _container.Children.Add(button);
            var dynamic = new Button { Name = "SourceDynamicButton" };
            dynamic.Resources["content"] = "Dynamic original";
            dynamic.SetResourceReference(ContentControl.ContentProperty, "content");
            _container.Children.Add(dynamic);
            var oneWay = new SourceValidationProbe { Name = "SourceOneWayToSource" };
            oneWay.SetBinding(SourceValidationProbe.ValueProperty, new Binding(nameof(SourceValidationModel.Value))
                { Source = new SourceValidationModel(), Mode = BindingMode.OneWayToSource });
            _container.Children.Add(oneWay);
            primary.UpdateLayout();
        }
    }

    internal static class SourceValidationCounters
    {
        public static int Gets, Writes, ValidationCalls, ConverterCalls, AttributeConstructions, WrapperCalls;
    }

    public sealed class SourceValidationProbe : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(SourceValidationProbe), new PropertyMetadata("Default"));
        public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(nameof(Number), typeof(double), typeof(SourceValidationProbe), new PropertyMetadata(0d));
        public static readonly DependencyProperty GuardedProperty = DependencyProperty.Register(nameof(Guarded), typeof(int), typeof(SourceValidationProbe), new PropertyMetadata(0), ValidateGuarded);
        public static readonly DependencyProperty ConvertedProperty = DependencyProperty.Register(nameof(Converted), typeof(string), typeof(SourceValidationProbe), new PropertyMetadata("No converter"));
        public static readonly DependencyProperty DangerousProperty = DependencyProperty.Register(nameof(Dangerous), typeof(string), typeof(SourceValidationProbe), new PropertyMetadata("No wrapper call"));
        public static readonly DependencyProperty NullableEnumProperty = DependencyProperty.Register(nameof(NullableEnum), typeof(SourceValidationEnum?), typeof(SourceValidationProbe), new PropertyMetadata(null));
        private static readonly DependencyPropertyKey ReadOnlyValuePropertyKey = DependencyProperty.RegisterReadOnly(nameof(ReadOnlyValue), typeof(string), typeof(SourceValidationProbe), new PropertyMetadata("Read only"));
        public static readonly DependencyProperty ReadOnlyValueProperty = ReadOnlyValuePropertyKey.DependencyProperty;
        public static WeakReference<SourceValidationProbe>? LastTarget;

        [SourceValidationAudit]
        public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double Number { get => (double)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
        public int Guarded { get => (int)GetValue(GuardedProperty); set => SetValue(GuardedProperty, value); }
        [TypeConverter(typeof(SourceValidationConverter))]
        public string Converted { get => (string)GetValue(ConvertedProperty); set => SetValue(ConvertedProperty, value); }
        public string Dangerous { get { SourceValidationCounters.WrapperCalls++; return (string)GetValue(DangerousProperty); } set => SetValue(DangerousProperty, value); }
        public SourceValidationEnum? NullableEnum { get => (SourceValidationEnum?)GetValue(NullableEnumProperty); set => SetValue(NullableEnumProperty, value); }
        public string ReadOnlyValue => (string)GetValue(ReadOnlyValueProperty);

        private static bool ValidateGuarded(object value)
        {
            SourceValidationCounters.ValidationCalls++;
            if ((int)value == 99 && LastTarget?.TryGetTarget(out var target) == true) target.SetCurrentValue(GuardedProperty, 7);
            return (int)value >= 0;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SourceValidationAuditAttribute : Attribute
    {
        public SourceValidationAuditAttribute() => SourceValidationCounters.AttributeConstructions++;
    }
    public sealed class SourceValidationConverter : TypeConverter
    {
        public SourceValidationConverter() => SourceValidationCounters.ConverterCalls++;
        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        { SourceValidationCounters.ConverterCalls++; return "Application converter"; }
    }
    [TypeConverter(typeof(SourceValidationConverter))]
    public enum SourceValidationEnum { First, Second }
    public sealed class SourceValidationModel
    {
        private string? _value;
        public string? Value { get { SourceValidationCounters.Gets++; return _value; } set { SourceValidationCounters.Writes++; _value = value; } }
    }
    public class SourceOriginalOwner : DependencyObject
    {
        public static readonly DependencyProperty SharedValueProperty = DependencyProperty.Register("SharedValue", typeof(string), typeof(SourceOriginalOwner), new PropertyMetadata("Shared default"));
    }
    [ContentProperty(nameof(SharedValue))]
    public class SourceAddOwnerProbe : FrameworkElement
    {
        public static readonly DependencyProperty SharedValueProperty = SourceOriginalOwner.SharedValueProperty.AddOwner(typeof(SourceAddOwnerProbe));
        public string SharedValue { get => (string)GetValue(SharedValueProperty); set => SetValue(SharedValueProperty, value); }
    }
    [ContentProperty(nameof(Width))]
    public sealed class SourceInheritedProbe : SourceAddOwnerProbe
    {
        public static readonly DependencyProperty RuntimeOnlyProperty = DependencyProperty.Register(nameof(RuntimeOnly), typeof(string), typeof(SourceInheritedProbe), new PropertyMetadata(""));
        public string RuntimeOnly { get => (string)GetValue(RuntimeOnlyProperty); set => SetValue(RuntimeOnlyProperty, value); }
    }
}

namespace WpfStudio.InspectionFixture.SourceA
{
    public static class SourceOptions
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(int), typeof(SourceOptions), new PropertyMetadata(0));
        public static int GetMode(DependencyObject target) => (int)target.GetValue(ModeProperty);
        public static void SetMode(DependencyObject target, int value) => target.SetValue(ModeProperty, value);
    }
}
namespace WpfStudio.InspectionFixture.SourceB
{
    public static class SourceOptions
    {
        public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(string), typeof(SourceOptions), new PropertyMetadata(""));
        public static string GetMode(DependencyObject target) => (string)target.GetValue(ModeProperty);
        public static void SetMode(DependencyObject target, string value) => target.SetValue(ModeProperty, value);
    }
}
