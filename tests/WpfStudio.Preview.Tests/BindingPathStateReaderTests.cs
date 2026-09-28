using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class BindingPathStateReaderTests(PreviewFixture fixture)
{
    [Fact]
    public void ObservesActualResolvedPrefixAndFirstFailureWithoutEvaluatingProperties() => fixture.OnDispatcher(() =>
    {
        var source = new Root(new object());
        var target = new TextBlock();
        var expression = (BindingExpression)target.SetBinding(TextBlock.TextProperty,
            new Binding("Child.Value.Tail") { Source = source });
        int before = source.Reads;

        var snapshot = BindingPathStateReader.Capture(expression);

        Assert.True(snapshot.Available, snapshot.UnavailableReason);
        Assert.Equal("PathError", snapshot.Status);
        Assert.Equal(1, snapshot.FirstUnresolvedLevel);
        Assert.Collection(snapshot.Segments,
            segment => { Assert.Equal("Child", segment.Name); Assert.Equal("Resolved", segment.State); Assert.EndsWith(nameof(Root), segment.OwnerType!); },
            segment => { Assert.Equal("Value", segment.Name); Assert.Equal("Unresolved", segment.State); Assert.Null(segment.OwnerType); },
            segment => { Assert.Equal("Tail", segment.Name); Assert.Equal("NotObserved", segment.State); Assert.Null(segment.OwnerType); });
        Assert.Contains("do not distinguish", snapshot.UnavailableReason!);
        Assert.Equal(before, source.Reads);
        Assert.Equal("PathError", BindingReader.Read(expression).Category);
        Assert.Equal(before, source.Reads);
        BindingOperations.ClearAllBindings(target);
        return true;
    });

    [Fact]
    public void SameRootMissingThenNullNeverReusesHistoricalOwnerAndRecoveryUsesNewCache() => fixture.OnDispatcher(() =>
    {
        var source = new Root(new object());
        var target = new TextBlock();
        var expression = (BindingExpression)target.SetBinding(TextBlock.TextProperty,
            new Binding("Child.Value") { Source = source });
        var missing = BindingPathStateReader.Capture(expression);
        source.Child = null;
        int afterNull = source.Reads;
        var empty = BindingPathStateReader.Capture(expression);
        Assert.Equal(1, missing.FirstUnresolvedLevel);
        Assert.Equal(1, empty.FirstUnresolvedLevel);
        Assert.Null(empty.Segments[1].OwnerType);
        Assert.Equal("Unresolved", empty.Segments[1].State);
        Assert.Equal(afterNull, source.Reads);

        var child = new Child();
        source.Child = child;
        int afterRecovery = source.Reads, childReads = child.Reads;
        var recovered = BindingReader.Read(expression);
        Assert.Equal("Active", recovered.Status);
        Assert.Null(recovered.Details!.PathState!.FirstUnresolvedLevel);
        Assert.Equal("Value", recovered.ResolvedProperty);
        Assert.EndsWith(nameof(Child), recovered.ResolvedSourceType!);
        Assert.All(recovered.Details.PathState.Segments, segment => Assert.Equal("Resolved", segment.State));
        Assert.Equal(afterRecovery, source.Reads);
        Assert.Equal(childReads, child.Reads);
        BindingOperations.ClearAllBindings(target);
        return true;
    });

    [Fact]
    public void ConversionAndFallbackAreObservedWithoutRerunningConverterOrConflatingValidation() => fixture.OnDispatcher(() =>
    {
        var source = new Child();
        var converter = new RejectingConverter();
        var target = new TextBlock();
        var expression = (BindingExpression)target.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(Child.Value)) { Source = source, Converter = converter, FallbackValue = "Fallback shown" });
        Assert.Equal("Fallback shown", target.Text);
        int reads = source.Reads, conversions = converter.Calls;
        var observation = BindingReader.Read(expression);
        Assert.Equal("UpdateTargetError", observation.Status);
        Assert.False(observation.HasValidationError);
        Assert.Equal(true, observation.Details!.PathState!.UsesFallbackValue);
        Assert.Equal(typeof(string).FullName, observation.Details.PathState.TargetType);
        Assert.Equal("Resolved", Assert.Single(observation.Details.PathState.Segments).State);
        Assert.Equal(typeof(string).FullName, observation.Details.PathState.Segments[0].ValueType);
        Assert.Equal(reads, source.Reads);
        Assert.Equal(conversions, converter.Calls);
        BindingOperations.ClearAllBindings(target);
        return true;
    });

    [Fact]
    public void DetachedAndWrongDispatcherObservationsAreUnavailable()
    {
        var created = fixture.OnDispatcher(() =>
        {
            var target = new TextBlock();
            var result = (BindingExpression)target.SetBinding(TextBlock.TextProperty, new Binding(nameof(Child.Value)) { Source = new Child() });
            return (Target: target, Expression: result);
        });
        BindingExpression expression = created.Expression;
        var wrongThread = BindingPathStateReader.Capture(expression);
        Assert.False(wrongThread.Available);
        Assert.Empty(wrongThread.Segments);
        fixture.OnDispatcher(() =>
        {
            BindingOperations.ClearBinding(created.Target, expression.TargetProperty);
            var detached = BindingPathStateReader.Capture(expression);
            Assert.False(detached.Available);
            Assert.Empty(detached.Segments);
            return true;
        });
    }

    [Theory]
    [InlineData("iTransferPending")]
    [InlineData("iNeedDataTransfer")]
    [InlineData("iTransferDeferred")]
    public void PendingTransferFlagsWithholdPriorSegmentAttribution(string flag) => fixture.OnDispatcher(() =>
    {
        var source = new Child();
        var target = new TextBlock();
        var expression = (BindingExpression)target.SetBinding(TextBlock.TextProperty, new Binding(nameof(Child.Value)) { Source = source });
        var flags = typeof(BindingExpressionBase).GetField("_flags", Private)!;
        object original = flags.GetValue(expression)!;
        uint bit = Convert.ToUInt32(flags.FieldType.GetField(flag, Private)!.GetRawConstantValue());
        int reads = source.Reads;
        try
        {
            flags.SetValue(expression, Enum.ToObject(flags.FieldType, Convert.ToUInt32(original) | bit));
            var snapshot = BindingPathStateReader.Capture(expression);
            Assert.True(snapshot.Available, snapshot.UnavailableReason);
            Assert.Equal("NotObserved", snapshot.Status);
            Assert.Empty(snapshot.Segments);
            Assert.Null(snapshot.FirstUnresolvedLevel);
            Assert.Equal(reads, source.Reads);
        }
        finally { flags.SetValue(expression, original); BindingOperations.ClearAllBindings(target); }
        return true;
    });

    [Fact]
    public void UnsupportedRuntimeArrayShapeReturnsExplicitUnavailable() => fixture.OnDispatcher(() =>
    {
        var target = new TextBlock();
        var expression = (BindingExpression)target.SetBinding(TextBlock.TextProperty, new Binding(nameof(Child.Value)) { Source = new Child() });
        var (worker, states) = StateField(expression);
        var original = (Array)states.GetValue(worker)!;
        try
        {
            states.SetValue(worker, Array.CreateInstance(original.GetType().GetElementType()!, 0));
            var snapshot = BindingPathStateReader.Capture(expression);
            Assert.False(snapshot.Available);
            Assert.Contains("shape", snapshot.UnavailableReason!);
            Assert.Empty(snapshot.Segments);
        }
        finally { states.SetValue(worker, original); BindingOperations.ClearAllBindings(target); }
        return true;
    });

    [Fact]
    public void CustomCachedTypeMetadataDoesNotRunVirtualNameGetters() => fixture.OnDispatcher(() =>
    {
        var target = new TextBlock();
        var expression = (BindingExpression)target.SetBinding(TextBlock.TextProperty, new Binding(nameof(Child.Value)) { Source = new Child() });
        var (worker, statesField) = StateField(expression);
        var states = (Array)statesField.GetValue(worker)!;
        object original = states.GetValue(0)!;
        object replacement = states.GetValue(0)!;
        var custom = new GuardedType();
        replacement.GetType().GetField("type", Private)!.SetValue(replacement, custom);
        try
        {
            states.SetValue(replacement, 0);
            var snapshot = BindingPathStateReader.Capture(expression);
            Assert.True(snapshot.Available, snapshot.UnavailableReason);
            Assert.Contains("custom type metadata omitted", Assert.Single(snapshot.Segments).ValueType!);
            Assert.Equal(0, custom.Reads);
        }
        finally { states.SetValue(original, 0); BindingOperations.ClearAllBindings(target); }
        return true;
    });

    [Fact]
    public void LongPathsAndDetailPayloadsAreBoundedWithoutEvaluatingTruncatedTail() => fixture.OnDispatcher(() =>
    {
        var source = new Link();
        var target = new TextBlock();
        var expression = (BindingExpression)target.SetBinding(TextBlock.TextProperty,
            new Binding(string.Join(".", Enumerable.Repeat(nameof(Link.Next), 80)) + ".Value") { Source = source });
        int reads = source.Reads;
        var snapshot = BindingPathStateReader.Capture(expression);
        Assert.True(snapshot.Available, snapshot.UnavailableReason);
        Assert.True(snapshot.Truncated);
        Assert.Equal(64, snapshot.Segments.Count);
        Assert.Null(snapshot.FirstUnresolvedLevel);
        var zero = BindingPathStateReader.Capture(expression, 0);
        Assert.Empty(zero.Segments);
        Assert.True(zero.Truncated);
        var observation = BindingReader.Read(expression);
        Assert.True(BindingDetailBudget.GetCharacterCount(observation) > BindingDetailBudget.GetCharacterCount(observation with { Details = null }));
        var omitted = new BindingDetailBudget(1).Take(observation);
        Assert.Equal(observation.Status, omitted.Status);
        Assert.Equal("Omitted", omitted.Details!.SourceKind);
        Assert.Null(omitted.Details.PathState);
        Assert.Equal(reads, source.Reads);
        BindingOperations.ClearAllBindings(target);
        return true;
    });

    [Fact]
    public void CompositeObservationsKeepExactChildIdentityAndBudgetWithoutRecursiveSources() => fixture.OnDispatcher(() =>
    {
        var target = new TextBlock();
        var multi = new MultiBinding { StringFormat = "{0} {1}" };
        multi.Bindings.Add(new Binding(nameof(Child.Value)) { Source = new Child() });
        multi.Bindings.Add(new Binding("Missing") { Source = new Child() });
        var expression = target.SetBinding(TextBlock.TextProperty, multi);
        var catalog = new BindingSourceCatalog();
        var snapshot = catalog.Capture(expression, observe: child => BindingReader.Read(child) with { Sources = new("must-not-recurse", []) });
        Assert.Equal(3, snapshot.Declarations.Count);
        Assert.Equal("(MultiBinding)", snapshot.Declarations[0].Observation!.Path);
        Assert.Null(snapshot.Declarations[0].Observation!.Details!.PathState);
        var good = snapshot.Declarations[1];
        var bad = snapshot.Declarations[2];
        Assert.Equal(0, good.ChildIndex);
        Assert.Equal(1, bad.ChildIndex);
        Assert.Equal(snapshot.BindingId, good.ParentExpressionId);
        Assert.Equal("Active", good.Observation!.Status);
        Assert.Equal("MissingProperty", bad.Observation!.Category);
        Assert.NotEqual(good.ExpressionId, bad.ExpressionId);
        Assert.All(snapshot.Declarations, row => Assert.Null(row.Observation!.Sources));
        var limited = catalog.Capture(expression, maximumCharacters: 2048, observe: child => BindingReader.Read(child));
        Assert.True(limited.Truncated);
        Assert.InRange(BindingSourceCatalog.GetCharacterCount(limited), 0, 2048);
        Assert.Equal(snapshot.Declarations.Select(row => row.ExpressionId), limited.Declarations.Select(row => row.ExpressionId));
        BindingOperations.ClearAllBindings(target);
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildOnlyValidationRemainsOnItsExactExpressionWithoutCompositeGetterFailure(bool priority) => fixture.OnDispatcher(() =>
    {
        var source = new Child();
        var target = new TextBlock();
        BindingBase declaration;
        if (priority)
        {
            var candidates = new PriorityBinding();
            candidates.Bindings.Add(new Binding(nameof(Child.Value)) { Source = source });
            candidates.Bindings.Add(new Binding("Missing") { Source = source });
            declaration = candidates;
        }
        else
        {
            var multi = new MultiBinding { StringFormat = "{0} {1}" };
            multi.Bindings.Add(new Binding(nameof(Child.Value)) { Source = source });
            multi.Bindings.Add(new Binding(nameof(Child.Value)) { Source = source });
            declaration = multi;
        }
        var expression = target.SetBinding(TextBlock.TextProperty, declaration);
        var children = expression is MultiBindingExpression multiExpression
            ? multiExpression.BindingExpressions : ((PriorityBindingExpression)expression).BindingExpressions;
        var child = children[0];
        var opaque = new OpaqueValidationContent();
        Validation.MarkInvalid(child, new ValidationError(new RejectingRule(), child, opaque, null));
        int reads = source.Reads;
        try
        {
            // WPF's aggregate getter may construct a collection from null for this
            // real child-only error. The reader must never call that getter.
            var root = BindingReader.Read(expression);
            Assert.True(root.HasValidationError);
            Assert.Empty(root.Details!.ValidationErrors);
            Assert.Contains("select a child", root.Details.SourceDescription);
            Assert.False(root.Details.ValidationTruncated);
            var snapshot = new BindingSourceCatalog().Capture(expression, observe: current => BindingReader.Read(current));
            var selected = Assert.Single(snapshot.Declarations, row => row.ChildIndex == 0);
            Assert.True(selected.Observation!.HasValidationError);
            Assert.Equal("Validation", selected.Observation.Category);
            Assert.Single(selected.Observation.Details!.ValidationErrors);
            Assert.Contains("content was not evaluated", selected.Observation.Details.ValidationErrors[0].Message!);
            Assert.Equal(0, opaque.Formats);
            Assert.Equal(reads, source.Reads);
        }
        finally { Validation.ClearInvalid(child); BindingOperations.ClearAllBindings(target); }
        return true;
    });

    [Fact]
    public void ErrorBeyondCompositeTraversalBudgetCannotProduceFalseRecovery() => fixture.OnDispatcher(() =>
    {
        var target = new TextBlock();
        var multi = new MultiBinding { StringFormat = "{0}" };
        for (int i = 0; i < 100; i++) multi.Bindings.Add(new Binding { Source = "Value" });
        var expression = (MultiBindingExpression)target.SetBinding(TextBlock.TextProperty, multi);
        var tail = expression.BindingExpressions[99];
        Validation.MarkInvalid(tail, new ValidationError(new RejectingRule(), tail, "Only the tail is invalid", null));
        try
        {
            var observed = BindingReader.Read(expression);
            Assert.Equal(expression.Status.ToString(), observed.Status);
            Assert.Equal("Pending", observed.Category);
            Assert.False(observed.HasValidationError); // No error was claimed outside the inspected prefix.
            Assert.True(observed.Details!.ValidationTruncated);
            Assert.Empty(observed.Details.ValidationErrors);
        }
        finally { Validation.ClearInvalid(tail); BindingOperations.ClearAllBindings(target); }
        return true;
    });

    [Fact]
    public void EditIdentityDoesNotReadDescriptorMetadataAndRejectsChangedOrUnavailableCache() => fixture.OnDispatcher(() =>
    {
        var source = new Child();
        var target = new TextBlock();
        var expression = (BindingExpression)target.SetBinding(TextBlock.TextProperty, new Binding(nameof(Child.Value)) { Source = source });
        var (worker, stateField) = StateField(expression);
        var states = (Array)stateField.GetValue(worker)!;
        object original = states.GetValue(0)!;
        var accessor = original.GetType().GetField("info", Private)!;
        var descriptor = new GuardedDescriptor();
        object altered = states.GetValue(0)!;
        accessor.SetValue(altered, descriptor);
        states.SetValue(altered, 0);
        int reads = source.Reads;
        try
        {
            var first = BindingPathStateReader.CaptureIdentity(expression);
            Assert.NotNull(first);
            Assert.True(first.Matches(BindingPathStateReader.CaptureIdentity(expression)));
            accessor.SetValue(altered, new GuardedDescriptor());
            states.SetValue(altered, 0);
            Assert.False(first.Matches(BindingPathStateReader.CaptureIdentity(expression)));
            stateField.SetValue(worker, Array.CreateInstance(states.GetType().GetElementType()!, 0));
            Assert.Null(BindingPathStateReader.CaptureIdentity(expression));
            Assert.False(first.Matches(null));
            Assert.Equal(0, descriptor.Reads);
            Assert.Equal(reads, source.Reads);
        }
        finally { stateField.SetValue(worker, states); states.SetValue(original, 0); BindingOperations.ClearAllBindings(target); }
        return true;
    });

    [Fact]
    public void InactivePriorityCandidateHasStableOpaqueEditIdentity() => fixture.OnDispatcher(() =>
    {
        var target = new TextBlock();
        var priority = new PriorityBinding();
        priority.Bindings.Add(new Binding { Source = "Selected" });
        priority.Bindings.Add(new Binding("Missing") { Source = new Child() });
        var expression = (PriorityBindingExpression)target.SetBinding(TextBlock.TextProperty, priority);
        var inactive = (BindingExpression)expression.BindingExpressions[1];
        Assert.Equal(BindingStatus.Inactive, inactive.Status);
        var first = BindingPathStateReader.CaptureIdentity(inactive);
        Assert.NotNull(first);
        Assert.True(first.Matches(BindingPathStateReader.CaptureIdentity(inactive)));
        BindingOperations.ClearAllBindings(target);
        return true;
    });

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static (object Worker, FieldInfo States) StateField(BindingExpression expression)
    {
        object worker = typeof(BindingExpression).GetField("_worker", Private)!.GetValue(expression)!;
        object path = worker.GetType().GetField("_pathWorker", Private)!.GetValue(worker)!;
        return (path, path.GetType().GetField("_arySVS", Private)!);
    }

    public sealed class Root(object? child) : INotifyPropertyChanged
    {
        private object? _child = child;
        public int Reads;
        public event PropertyChangedEventHandler? PropertyChanged;
        public object? Child { get { Reads++; return _child; } set { _child = value; PropertyChanged?.Invoke(this, new(nameof(Child))); } }
    }
    public sealed class Child { public int Reads; public string Value { get { Reads++; return "Value"; } } }
    public sealed class Link { public int Reads; public Link Next { get { Reads++; return this; } } public string Value => "Value"; }
    private sealed class RejectingConverter : IValueConverter
    {
        public int Calls;
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) { Calls++; return DependencyProperty.UnsetValue; }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    private sealed class GuardedType() : TypeDelegator(typeof(string))
    {
        public int Reads;
        public override string? FullName { get { Reads++; throw new InvalidOperationException("Do not inspect custom Type metadata."); } }
        public override string Name { get { Reads++; throw new InvalidOperationException("Do not inspect custom Type metadata."); } }
    }
    private sealed class RejectingRule : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo) => throw new InvalidOperationException("Observation must not execute validation.");
    }
    private sealed class OpaqueValidationContent
    {
        public int Formats;
        public override string ToString() { Formats++; throw new InvalidOperationException("Observation must not format opaque validation content."); }
    }
    private sealed class GuardedDescriptor() : PropertyDescriptor("Value", null)
    {
        public int Reads;
        public override string Name { get { Reads++; throw new InvalidOperationException("Descriptor metadata must not be evaluated."); } }
        public override Type ComponentType => typeof(Child);
        public override Type PropertyType => typeof(string);
        public override bool IsReadOnly => true;
        public override bool CanResetValue(object component) => false;
        public override object? GetValue(object? component) => throw new InvalidOperationException("Descriptor value must not be evaluated.");
        public override void ResetValue(object component) => throw new NotSupportedException();
        public override void SetValue(object? component, object? value) => throw new NotSupportedException();
        public override bool ShouldSerializeValue(object component) => false;
    }
}
