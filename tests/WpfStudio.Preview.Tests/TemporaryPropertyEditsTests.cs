using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfStudio.Contracts;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class TemporaryPropertyEditsTests(PreviewFixture fixture)
{
    [Theory]
    [InlineData(false, BindingMode.TwoWay)]
    [InlineData(true, BindingMode.TwoWay)]
    [InlineData(false, BindingMode.Default)]
    [InlineData(true, BindingMode.Default)]
    public void RepeatedOverridesAndResetNeverWriteThroughTwoWayBindings(bool styled, BindingMode mode) => fixture.OnDispatcher(() =>
    {
        var source = new CounterSource();
        var target = new EditProbe();
        var binding = new Binding(nameof(CounterSource.Value)) { Source = source, Mode = mode, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        Install(target, binding, styled);
        Assert.Equal("Original", target.EditValue);
        int writes = source.Writes;
        var editor = new TemporaryPropertyEdits();
        Assert.True(editor.Apply(target, EditProbe.EditValueProperty, "Temporary").Success);
        Assert.True(TemporaryPropertyEdits.IsOverrideBinding(BindingOperations.GetBindingExpressionBase(target, EditProbe.EditValueProperty)));
        Flush();
        Assert.Equal("Temporary", target.EditValue);
        Assert.Equal("Original", source.Value);
        Assert.Equal(writes, source.Writes);
        Assert.True(editor.Apply(target, EditProbe.EditValueProperty, "Another").Success);
        source.Value = "New source";
        Flush();
        Assert.Equal("Another", target.EditValue);
        Assert.Equal(writes + 1, source.Writes);
        Assert.True(editor.Reset(target, EditProbe.EditValueProperty).Success);
        Flush();
        Assert.Equal("New source", target.EditValue);
        Assert.Equal(writes + 1, source.Writes);
        Assert.Same(binding, BindingOperations.GetBindingBase(target, EditProbe.EditValueProperty));
        Assert.Equal(styled ? BaseValueSource.Style : BaseValueSource.Local, DependencyPropertyHelper.GetValueSource(target, EditProbe.EditValueProperty).BaseValueSource);
        Assert.Empty(editor.GetEditedTargets());
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneWayToSourceIsRejectedWithoutChangingItsBindingOrWritingItsSource(bool styled) => fixture.OnDispatcher(() =>
    {
        var source = new CounterSource();
        var target = new EditProbe();
        var binding = new Binding(nameof(CounterSource.Value)) { Source = source, Mode = BindingMode.OneWayToSource, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        Install(target, binding, styled);
        Flush();
        int writes = source.Writes;
        var editor = new TemporaryPropertyEdits();
        Assert.Contains("OneWayToSource", editor.Describe(target, EditProbe.EditValueProperty).Reason);
        Assert.False(editor.Apply(target, EditProbe.EditValueProperty, "Must not reach source").Success);
        Assert.True(editor.Reset(target, EditProbe.EditValueProperty).Success);
        Flush();
        Assert.Equal(writes, source.Writes);
        Assert.Same(binding, BindingOperations.GetBindingBase(target, EditProbe.EditValueProperty));
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiAndPriorityBindingsRestoreWithoutConvertBack(bool priority) => fixture.OnDispatcher(() =>
    {
        var source = new CounterSource();
        var target = new EditProbe();
        var child = new Binding(nameof(CounterSource.Value)) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        var converter = new MultiConverter();
        BindingBase binding;
        if (priority) binding = new PriorityBinding { Bindings = { child } };
        else binding = new MultiBinding { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, Converter = converter, Bindings = { child } };
        BindingOperations.SetBinding(target, EditProbe.EditValueProperty, binding);
        Flush();
        int writes = source.Writes;
        var editor = new TemporaryPropertyEdits();
        Assert.True(editor.Apply(target, EditProbe.EditValueProperty, "Temporary").Success);
        Flush();
        Assert.Equal("Temporary", target.EditValue);
        Assert.True(editor.Reset(target, EditProbe.EditValueProperty).Success);
        Flush();
        Assert.Equal("Original", target.EditValue);
        Assert.Equal(writes, source.Writes);
        Assert.Equal(0, converter.ConvertBackCalls);
        Assert.Same(binding, BindingOperations.GetBindingBase(target, EditProbe.EditValueProperty));
        return true;
    });

    [Fact]
    public void ApplicationReplacementIsPreservedAndReportedAsConflict() => fixture.OnDispatcher(() =>
    {
        var target = new EditProbe { EditValue = "Original" };
        var editor = new TemporaryPropertyEdits();
        Assert.True(editor.Apply(target, EditProbe.EditValueProperty, "Temporary").Success);
        var replacement = new Binding(nameof(CounterSource.Value)) { Source = new CounterSource(), Mode = BindingMode.OneWay };
        BindingOperations.SetBinding(target, EditProbe.EditValueProperty, replacement);
        Assert.False(editor.Describe(target, EditProbe.EditValueProperty).CanEdit);
        Assert.True(editor.Reset(target, EditProbe.EditValueProperty).Conflict);
        Assert.True(Assert.Single(editor.ResetAll(target)).Conflict);
        Assert.Same(replacement, BindingOperations.GetBindingBase(target, EditProbe.EditValueProperty));
        Assert.True(editor.Apply(target, EditProbe.EditValueProperty, "Do not overwrite app").Conflict);
        return true;
    });

    [Fact]
    public void ScalarParsingIsInvariantExplicitAboutNullAndIndependentOfTypeDescriptionProviders() => fixture.OnDispatcher(() =>
    {
        var previous = CultureInfo.CurrentCulture;
        var trap = new ConverterTrapProvider();
        TypeDescriptor.AddProvider(trap, typeof(Thickness));
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(ScalarPropertyValues.TryConvert(typeof(double), "1.25", false, out var number, out _));
            Assert.Equal(1.25, number);
            Assert.False(ScalarPropertyValues.TryConvert(typeof(double), "1,25", false, out _, out _));
            Assert.True(ScalarPropertyValues.TryConvert(typeof(Thickness), "1,2,3,4", false, out var thickness, out _));
            Assert.Equal(new Thickness(1, 2, 3, 4), thickness);
            Assert.True(ScalarPropertyValues.TryConvert(typeof(string), "", false, out var empty, out _));
            Assert.Equal("", empty);
            Assert.True(ScalarPropertyValues.TryConvert(typeof(string), "ignored", true, out var nil, out _));
            Assert.Null(nil);
            Assert.True(ScalarPropertyValues.TryConvert(typeof(double?), null, true, out _, out _));
            Assert.False(ScalarPropertyValues.TryConvert(typeof(double), null, true, out _, out _));
            string longText = new('x', 10000);
            Assert.Equal(longText, ScalarPropertyValues.ToEditableText(typeof(string), longText));
            Assert.False(ScalarPropertyValues.TryConvert(typeof(Uri), "https://example.com", false, out _, out _));
        }
        finally { CultureInfo.CurrentCulture = previous; TypeDescriptor.RemoveProvider(trap, typeof(Thickness)); }
        return true;
    });

    [Fact]
    public void ValidationDoesNotMutateAndNullEmptyAndLiteralRestorationRemainDistinct() => fixture.OnDispatcher(() =>
    {
        var target = new Button { Width = 30, Tag = "Original" };
        var editor = new TemporaryPropertyEdits();
        Assert.True(editor.Validate(target, FrameworkElement.WidthProperty, "60").Success);
        Assert.False(editor.Validate(target, FrameworkElement.WidthProperty, "-1").Success);
        Assert.Equal(30, target.Width);
        Assert.Empty(editor.GetEditedTargets());
        Assert.True(editor.Apply(target, FrameworkElement.TagProperty, null, isNull: true).Success);
        Assert.Null(target.Tag);
        Assert.True(editor.Describe(target, FrameworkElement.TagProperty).IsNull);
        Assert.True(editor.Apply(target, FrameworkElement.TagProperty, "").Success);
        Assert.Equal("", target.Tag);
        Assert.False(editor.Describe(target, FrameworkElement.TagProperty).IsNull);
        Assert.True(editor.Reset(target, FrameworkElement.TagProperty).Success);
        Assert.Equal("Original", target.Tag);
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedCurrentTextIsOmittedAndCannotBeTemporarilyEdited(bool objectProperty) => fixture.OnDispatcher(() =>
    {
        var target = new EditProbe();
        var property = objectProperty ? FrameworkElement.TagProperty : EditProbe.EditValueProperty;
        string large = new('x', ScalarPropertyValues.MaximumTextLength + 1);
        target.SetValue(property, large);
        var editor = new TemporaryPropertyEdits();
        var info = editor.Describe(target, property);
        Assert.False(info.CanEdit);
        Assert.False(info.IsNull);
        Assert.Null(info.EditableValue);
        Assert.Contains(ScalarPropertyValues.MaximumTextLength.ToString(CultureInfo.InvariantCulture), info.Reason);
        Assert.Null(ScalarPropertyValues.ToEditableText(property.PropertyType, large));
        Assert.False(editor.Validate(target, property, "Replacement").Success);
        Assert.False(editor.Apply(target, property, "Replacement").Success);
        Assert.Same(large, target.GetValue(property));
        Assert.Empty(editor.GetEditedTargets());
        string accepted = new('y', 6000);
        target.SetValue(property, accepted);
        Assert.Equal(accepted, editor.Describe(target, property).EditableValue);
        Assert.True(editor.Describe(target, property).CanEdit);
        return true;
    });

    [Fact]
    public void DefaultStyleAndInheritedValuesRecoverTheirOriginalPrecedence() => fixture.OnDispatcher(() =>
    {
        var editor = new TemporaryPropertyEdits();
        var parent = new StackPanel { DataContext = "Inherited" };
        var target = new Button { Style = new Style(typeof(Button)) { Setters = { new Setter(FrameworkElement.WidthProperty, 40d) } } };
        parent.Children.Add(target);
        foreach (var (property, value, expected) in new[]
        {
            (FrameworkElement.DataContextProperty, "Temporary", BaseValueSource.Inherited),
            (FrameworkElement.WidthProperty, "90", BaseValueSource.Style),
            (UIElement.OpacityProperty, "0.5", BaseValueSource.Default)
        })
        {
            var before = target.GetValue(property);
            Assert.True(editor.Apply(target, property, value).Success);
            Assert.True(editor.Reset(target, property).Success);
            Assert.Equal(before, target.GetValue(property));
            Assert.Equal(expected, DependencyPropertyHelper.GetValueSource(target, property).BaseValueSource);
            Assert.Same(DependencyProperty.UnsetValue, target.ReadLocalValue(property));
        }
        return true;
    });

    [Fact]
    public void UnsupportedExpressionsAnimationRichTextAndObjectContentAreNotModified() => fixture.OnDispatcher(() =>
    {
        var editor = new TemporaryPropertyEdits();
        var button = new Button();
        button.Resources["Accent"] = Brushes.Coral;
        button.SetResourceReference(Control.BackgroundProperty, "Accent");
        var originalResource = button.ReadLocalValue(Control.BackgroundProperty);
        Assert.False(editor.Apply(button, Control.BackgroundProperty, "Blue").Success);
        Assert.Same(originalResource, button.ReadLocalValue(Control.BackgroundProperty));
        var clock = (AnimationClock)new DoubleAnimation(1, 0.5, TimeSpan.FromSeconds(60)).CreateClock(true);
        button.ApplyAnimationClock(UIElement.OpacityProperty, clock);
        clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(1), TimeSeekOrigin.BeginTime);
        Assert.True(DependencyPropertyHelper.GetValueSource(button, UIElement.OpacityProperty).IsAnimated);
        Assert.False(editor.Apply(button, UIElement.OpacityProperty, "0.9").Success);
        button.BeginAnimation(UIElement.OpacityProperty, null);
        var run = new Run("Keep me") { FontWeight = FontWeights.Bold };
        var text = new TextBlock(run);
        Assert.False(editor.Apply(text, TextBlock.TextProperty, "Flattened").Success);
        Assert.Same(run, text.Inlines.FirstInline);
        button.Content = text;
        Assert.False(editor.Apply(button, ContentControl.ContentProperty, "Replacement").Success);
        Assert.Same(text, button.Content);
        return true;
    });

    [Fact]
    public void CapacityIsBoundedAndResetAllWorksForDetachedObjects() => fixture.OnDispatcher(() =>
    {
        var editor = new TemporaryPropertyEdits(1);
        var first = new Button { Tag = "First" };
        var second = new Button { Tag = "Second" };
        Assert.True(editor.Apply(first, FrameworkElement.TagProperty, "Override").Success);
        Assert.False(editor.Apply(second, FrameworkElement.TagProperty, "Overflow").Success);
        Assert.True(Assert.Single(editor.ResetAll(first)).Success);
        Assert.Equal("First", first.Tag);
        Assert.True(editor.Apply(second, FrameworkElement.TagProperty, "Now allowed").Success);
        Assert.True(Assert.Single(editor.ResetAll(second)).Success);
        return true;
    });

    [Fact]
    public void SavedBindingGraphsDoNotKeepTargetsAlive()
    {
        var editor = new TemporaryPropertyEdits();
        var weak = fixture.OnDispatcher(() => CreateWeakTarget(editor));
        for (int attempt = 0; attempt < 8 && Alive(weak); attempt++)
        {
            fixture.OnDispatcher(() => { Flush(); return true; });
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.False(Alive(weak));
        Assert.Empty(editor.GetEditedTargets());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewRpcTwoWayOverrideDoesNotChangeSourceAndHidesMarkerPath(bool styled)
    {
        const string ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
        const string binding = "{Binding Text, ElementName=Source, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}";
        string subject = styled ? $"<TextBox x:Name='Subject'><TextBox.Style><Style TargetType='TextBox'><Setter Property='Text' Value='{binding}'/></Style></TextBox.Style></TextBox>" : $"<TextBox x:Name='Subject' Text='{binding}'/>";
        var snapshot = await fixture.Engine.RenderAsync(new PreviewRequest("C:/preview/SafeEdit.xaml", $"<StackPanel {ns}><TextBox x:Name='Source' Text='Original'/>{subject}</StackPanel>", 101, 400, 300), default);
        Assert.True(snapshot.Success, string.Join(" ", snapshot.Diagnostics.Select(item => item.Message)));
        var target = Assert.Single(snapshot.Nodes, node => node.Name == "Subject");
        var source = Assert.Single(snapshot.Nodes, node => node.Name == "Source");
        var edited = await fixture.Engine.SetPropertyAsync(new(101, target.Id, "Text", "Temporary"), default);
        Assert.True(edited.Success, edited.Error);
        var property = Assert.Single(edited.Inspection.Properties, item => item.Name == "Text");
        Assert.True(property.IsOverridden);
        Assert.Equal("Temporary", property.Value);
        Assert.Null(property.BindingPath);
        Assert.Null(property.BindingStatus);
        Assert.Equal("Original", Assert.Single((await fixture.Engine.InspectAsync(new(101, source.Id), default)).Properties, item => item.Name == "Text").Value);
        var reset = await fixture.Engine.SetPropertyAsync(new(101, target.Id, "Text", null, Reset: true), default);
        Assert.True(reset.Success, reset.Error);
        Assert.Equal("Original", Assert.Single(reset.Inspection.Properties, item => item.Name == "Text").Value);
        Assert.Equal(styled ? "Style" : "Local", Assert.Single(reset.Inspection.Properties, item => item.Name == "Text").ValueSource);
    }

    private static void Install(EditProbe target, BindingBase binding, bool styled)
    {
        if (styled) target.Style = new Style(typeof(EditProbe)) { Setters = { new Setter(EditProbe.EditValueProperty, binding) } };
        else BindingOperations.SetBinding(target, EditProbe.EditValueProperty, binding);
        Flush();
    }
    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<DependencyObject> CreateWeakTarget(TemporaryPropertyEdits editor)
    {
        var target = new EditProbe { Name = "Original" };
        BindingOperations.SetBinding(target, EditProbe.EditValueProperty, new Binding(nameof(FrameworkElement.Name)) { Source = target, Mode = BindingMode.OneWay });
        Assert.True(editor.Apply(target, EditProbe.EditValueProperty, "Temporary").Success);
        return new(target);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Alive(WeakReference<DependencyObject> weak) => weak.TryGetTarget(out _);

    public sealed class EditProbe : FrameworkElement
    {
        public static readonly DependencyProperty EditValueProperty = DependencyProperty.Register(nameof(EditValue), typeof(string), typeof(EditProbe),
            new FrameworkPropertyMetadata("Default", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
        public string? EditValue { get => (string?)GetValue(EditValueProperty); set => SetValue(EditValueProperty, value); }
    }
    public sealed class CounterSource : INotifyPropertyChanged
    {
        private string? _value = "Original";
        public int Writes { get; private set; }
        public string? Value { get => _value; set { _value = value; Writes++; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class MultiConverter : IMultiValueConverter
    {
        public int ConvertBackCalls;
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values[0];
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) { ConvertBackCalls++; return [value]; }
    }
    private sealed class ConverterTrapProvider : TypeDescriptionProvider
    {
        public override ICustomTypeDescriptor GetTypeDescriptor(Type objectType, object? instance) => new TrapDescriptor();
        private sealed class TrapDescriptor : CustomTypeDescriptor
        {
            public override TypeConverter GetConverter() => throw new InvalidOperationException("Application converter must not be invoked.");
        }
    }
}
