using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Inspection.Agent;

/// <summary>Reads existing WPF objects only; it owns no application or presentation source.</summary>
internal sealed partial class RunningInspector(BindingTraceBuffer traces, Wpf.Diagnostics.StaticResourceEvidenceCollector? resources = null) : IAsyncDisposable
{
    private sealed record Identity(string Id);
    private sealed record Entry(WeakReference<DependencyObject> Target, WeakReference<PresentationSource> Source,
        WeakReference<Visual> Root);
    private sealed record SourceTree(List<InspectionNode> Nodes, Dictionary<string, Entry> Entries,
        bool Truncated, string? Status = null, BindingScan? Bindings = null);
    private readonly ConditionalWeakTable<DependencyObject, Identity> _identities = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _revision;

    public async Task<InspectionTree> SnapshotAsync(InspectionTreeRequest request)
    {
        int maximum = Math.Clamp(request.MaximumNodes, 1, 3000);
        // CurrentSources enumerates a thread-safe copy backed by weak references.
        // A source's root and all its descendants are read only on its dispatcher.
        var sources = PresentationSource.CurrentSources.Cast<PresentationSource>().Take(65).ToArray();
        bool truncated = sources.Length > 64;
        sources = sources.Take(64).ToArray();
        await UpdatePickingSourcesAsync(sources).ConfigureAwait(false);
        int share = sources.Length == 0 ? maximum : Math.Max(1, maximum / sources.Length);
        int bindingShare = sources.Length == 0 ? MaximumBindingObservations : Math.Max(1, MaximumBindingObservations / sources.Length);
        var captured = await Task.WhenAll(sources.Select(source => ReadSourceAsync(source, share, bindingShare))).ConfigureAwait(false);
        var nodes = new List<InspectionNode>();
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var statuses = new List<string>();
        foreach (var tree in captured)
        {
            truncated |= tree.Truncated;
            if (tree.Status is not null) statuses.Add(tree.Status);
            foreach (var node in tree.Nodes)
            {
                if (nodes.Count == maximum) { truncated = true; break; }
                if (!entries.TryAdd(node.Id, tree.Entries[node.Id])) continue;
                nodes.Add(node);
            }
        }
        _entries = entries; // Retain only weak identities from the latest snapshot.
        _revision++;
        var bindings = captured.SelectMany(tree => tree.Bindings?.Observations ?? [])
            .Where(binding => entries.ContainsKey(binding.NodeId)).Take(MaximumBindingObservations + 1).ToArray();
        bool bindingScanTruncated = bindings.Length > MaximumBindingObservations
            || captured.Any(tree => tree.Bindings is null || tree.Bindings.Truncated);
        var scanned = captured.SelectMany(tree => tree.Bindings?.ScannedNodes ?? [])
            .Where(entries.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        if (bindings.Length > MaximumBindingObservations)
        {
            // Once observations were dropped, no node may be advertised as completely
            // scanned: doing so could misclassify a retained issue as binding removal.
            bindings = bindings.Take(MaximumBindingObservations).ToArray();
            scanned = [];
        }
        return new InspectionTree(_revision, nodes.Select(node => node with
        {
            ParentId = node.ParentId is { } parent && entries.ContainsKey(parent) ? parent : null,
            LogicalParentId = node.LogicalParentId is { } logical && entries.ContainsKey(logical) ? logical : null
        }).ToArray(), traces.Snapshot(), truncated,
            statuses.Count > 0 ? Limit(string.Join(" ", statuses.Distinct()), 2000) :
            nodes.Count == 0 ? "No presentation roots are currently available. Refresh after the application opens a window." : null,
            bindings, scanned, bindingScanTruncated, Pick: GetPickState());
    }

    private async Task<SourceTree> ReadSourceAsync(PresentationSource source, int maximum, int maximumBindings)
    {
        try { return await OnDispatcherAsync(source.Dispatcher, token => ReadSource(source, maximum, maximumBindings, token)).ConfigureAwait(false); }
        catch (Exception exception)
        {
            return new([], new(StringComparer.Ordinal), true,
                $"Dispatcher {source.Dispatcher.Thread.ManagedThreadId} unavailable: {Limit(exception.GetBaseException().Message, 250)}");
        }
    }

    private SourceTree ReadSource(PresentationSource source, int maximum, int maximumBindings, CancellationToken token)
    {
        if (source.IsDisposed || source.RootVisual is not Visual root) return new([], new(StringComparer.Ordinal), false, Bindings: new([], [], false));
        var nodes = new List<InspectionNode>();
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(DependencyObject Target, DependencyObject? Parent)>();
        var watch = Stopwatch.StartNew();
        bool omitted = false;
        stack.Push((root, null));
        while (stack.Count > 0 && nodes.Count < maximum && watch.ElapsedMilliseconds < 500)
        {
            token.ThrowIfCancellationRequested();
            var (target, parent) = stack.Pop();
            if (IsInspectionAdornment(target)) continue;
            if (!visited.Add(target)) continue;
            // Cross-dispatcher objects can only appear as external references.
            if (!target.CheckAccess()) { omitted = true; continue; }
            string id = GetId(target);
            nodes.Add(Node(target, parent, root));
            entries[id] = new(new(target), new(source), new(root));
            var children = new List<DependencyObject>();
            if (target is Visual or Visual3D)
            {
                int actualCount = VisualTreeHelper.GetChildrenCount(target);
                int count = Math.Min(actualCount, maximum);
                omitted |= actualCount > count;
                for (int i = 0; i < count; i++) children.Add(VisualTreeHelper.GetChild(target, i));
            }
            foreach (object child in LogicalTreeHelper.GetChildren(target))
            {
                token.ThrowIfCancellationRequested();
                if (child is DependencyObject dependency && !children.Contains(dependency)) children.Add(dependency);
                if (children.Count >= maximum || watch.ElapsedMilliseconds >= 500) { omitted = true; break; }
            }
            for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], target));
        }
        return new(nodes, entries, omitted || stack.Count > 0, Bindings: ScanBindings(source, entries, maximumBindings, token));
    }

    public async Task<InspectionElement> InspectAsync(InspectionNodeRequest request)
    {
        if (request.Revision != _revision || !_entries.TryGetValue(request.NodeId, out var entry) ||
            !entry.Target.TryGetTarget(out var target))
            return Unavailable(request, "The tree has changed or this element is no longer available. Refresh the tree.");
        try
        {
            return await OnDispatcherAsync(target.Dispatcher, token =>
            {
                if (!entry.Source.TryGetTarget(out var source) || source.IsDisposed ||
                    !entry.Root.TryGetTarget(out var root) || source.RootVisual != root || !IsAttached(target, root))
                    return Unavailable(request, "This element has left the presentation tree. Refresh the tree.");
                return ReadElement(request, target, token);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) { return Unavailable(request, Limit(exception.GetBaseException().Message, 1500)); }
    }

    private InspectionElement ReadElement(InspectionNodeRequest request, DependencyObject target, CancellationToken token)
    {
        var properties = new List<InspectionProperty>();
        var errors = new List<string>();
        var observations = new Dictionary<string, ObservedProperty>(StringComparer.Ordinal);
        var sourceObservations = new Dictionary<string, SourceObservation>(StringComparer.Ordinal);
        var appearanceObservations = new Dictionary<string, AppearanceProperty>(StringComparer.Ordinal);
        var bindingSourceObservations = new Dictionary<string, BindingSourceProperty>(StringComparer.Ordinal);
        int bindingSourceRowsRemaining = 256, bindingSourceCharactersRemaining = 131_072;
        int editableTextRemaining = 262_144;
        var detailBudget = new BindingDetailBudget(65_536);
        var candidates = BindingProperties(target, Stopwatch.StartNew(), token);
        if (!candidates.Complete) errors.Add("The property discovery limit was reached; some properties are not shown.");
        foreach (var property in candidates.Properties)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var valueSource = DependencyPropertyHelper.GetValueSource(target, property);
                var expression = BindingOperations.GetBindingExpressionBase(target, property);
                var display = new InspectionProperty(PropertyName(target, property),
                    TypeName(property.OwnerType), property.OwnerType.Assembly.GetName().Name ?? "",
                    TypeName(property.PropertyType), Format(target.GetValue(property)), valueSource.BaseValueSource.ToString(),
                    valueSource.IsExpression, valueSource.IsAnimated, valueSource.IsCoerced,
                    expression is null || Wpf.PropertyEditing.TemporaryPropertyEdits.IsOverrideBinding(expression) ? null : detailBudget.Take(BindingReader.Read(expression, traces.Evidence)));
                var editable = DescribeEditableProperty(target, property, display, observations, ref editableTextRemaining);
                var described = DescribeSourceProperty(target, property, editable, _propertyEdits.Describe(target, property), sourceObservations);
                if (expression is not null && described.Binding is { } binding && described.PropertyId is { } bindingPropertyId)
                {
                    var sources = _bindingSources.Capture(expression, BindingIdentity,
                        Math.Min(65, bindingSourceRowsRemaining), Math.Min(16384, bindingSourceCharactersRemaining),
                        observed => detailBudget.Take(BindingReader.Read(observed, traces.Evidence)));
                    bindingSourceRowsRemaining -= sources.Declarations.Count;
                    bindingSourceCharactersRemaining = Math.Max(0, bindingSourceCharactersRemaining - Wpf.Diagnostics.BindingSourceCatalog.GetCharacterCount(sources));
                    described = described with { Binding = binding with { Sources = sources } };
                    bindingSourceObservations.Add(bindingPropertyId,
                        new(property, described.Name, described.OwnerType, described.OwnerAssembly, new(expression), sources));
                }
                properties.Add(described);
                if (described.PropertyId is { } propertyId)
                    appearanceObservations.Add(propertyId, new(property, described.Name, described.OwnerType, described.OwnerAssembly));
            }
            catch (Exception exception) { errors.Add($"{property.Name}: {Limit(exception.GetBaseException().Message, 150)}"); }
        }
        _propertyObservations.GetOrCreateValue(target).Properties = observations;
        _sourceObservations.GetOrCreateValue(target).Properties = sourceObservations;
        _appearanceProperties.GetOrCreateValue(target).Observation = new(_revision, appearanceObservations);
        _bindingSourceProperties.GetOrCreateValue(target).Observation = new(_revision, bindingSourceObservations);
        object? dataContext = target is FrameworkElement element ? element.DataContext :
            target is FrameworkContentElement content ? content.DataContext : null;
        return new InspectionElement(_revision, request.NodeId, properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(),
            dataContext is null ? null : TypeName(dataContext.GetType()), true,
            errors.Count == 0 ? null : Limit(string.Join(" ", errors), 1500), Layout: CaptureLayout(request, target));
    }

    private InspectionElement Unavailable(InspectionNodeRequest request, string status) => new(_revision, request.NodeId, [], null, false, status);
    private string GetId(DependencyObject target) => _identities.GetValue(target, _ => new(Guid.NewGuid().ToString("N"))).Id;

    private InspectionNode Node(DependencyObject target, DependencyObject? parent, Visual root)
    {
        string? name = target is FrameworkElement element ? element.Name : target is FrameworkContentElement content ? content.Name : null;
        DependencyObject? logical = LogicalTreeHelper.GetParent(target);
        InspectionBounds? bounds = null;
        if (target is Visual visual)
        {
            try
            {
                Rect rect = target is UIElement ui ? new Rect(ui.RenderSize) : VisualTreeHelper.GetDescendantBounds(visual);
                if (!ReferenceEquals(visual, root)) rect = visual.TransformToAncestor(root).TransformBounds(rect);
                if (!rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height))
                    bounds = new(rect.X, rect.Y, rect.Width, rect.Height);
            }
            catch (InvalidOperationException) { }
        }
        InspectionSourceHint? hint = null;
        try
        {
            var info = VisualDiagnostics.GetXamlSourceInfo(target);
            if (info?.SourceUri is not null && info.LineNumber > 0 && info.LinePosition > 0 && info.SourceUri.ToString() is { Length: <= 2048 } uri)
                hint = new(uri, info.LineNumber, info.LinePosition);
        }
        catch (Exception) { }
        return new(GetId(target), parent is null ? null : GetId(parent), logical is null ? null : GetId(logical),
            target.Dispatcher.Thread.ManagedThreadId, TypeName(target.GetType()), string.IsNullOrEmpty(name) ? null : Limit(name, 256), bounds,
            target is Visual or Visual3D, target is Window window ? Limit(window.Title, 256) : null, hint);
    }

    private static bool IsAttached(DependencyObject target, Visual root)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        for (DependencyObject? current = target; current is not null && visited.Count < 512 && visited.Add(current);)
        {
            if (ReferenceEquals(current, root)) return true;
            current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private static IReadOnlyList<DependencyProperty> Properties(DependencyObject target)
    {
        var result = new HashSet<DependencyProperty>();
        var local = target.GetLocalValueEnumerator();
        while (local.MoveNext() && result.Count < 512) result.Add(local.Current.Property);
        for (Type? current = target.GetType(); current is not null && result.Count < 512; current = current.BaseType)
            foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(DependencyProperty)) continue;
                try { if (field.GetValue(null) is DependencyProperty property) result.Add(property); }
                catch (Exception) { }
                if (result.Count == 512) break;
            }
        return result.ToArray();
    }

    private static string PropertyName(DependencyObject target, DependencyProperty property)
    {
        var wrapper = target.GetType().GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
        return wrapper?.PropertyType == property.PropertyType ? property.Name : property.OwnerType.Name + "." + property.Name;
    }

    private static async Task<T> OnDispatcherAsync<T>(Dispatcher dispatcher, Func<CancellationToken, T> action)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) throw new InvalidOperationException("The owning dispatcher has shut down.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var operation = dispatcher.InvokeAsync(() => action(timeout.Token), DispatcherPriority.Background, timeout.Token);
        try { return await operation.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            operation.Abort();
            throw new TimeoutException("The owning dispatcher did not respond within two seconds. The application remains running.");
        }
    }

    internal static string TypeName(Type type) => Limit(type.FullName ?? type.Name, 512);
    internal static string Limit(string text, int maximum) => text.Length <= maximum ? text : text[..maximum] + "…";
    internal static string Format(object? value)
    {
        if (value is null) return "(null)";
        if (ReferenceEquals(value, DependencyProperty.UnsetValue)) return "(unset)";
        // Never invoke arbitrary model/collection ToString or property getters.
        string text = value switch
        {
            string s => s,
            bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            Enum e => e.ToString(),
            Thickness v => FormattableString.Invariant($"{v.Left},{v.Top},{v.Right},{v.Bottom}"),
            CornerRadius v => FormattableString.Invariant($"{v.TopLeft},{v.TopRight},{v.BottomRight},{v.BottomLeft}"),
            GridLength v => v.IsAuto ? "Auto" : v.Value.ToString(CultureInfo.InvariantCulture) + (v.IsStar ? "*" : ""),
            Point v => v.ToString(CultureInfo.InvariantCulture),
            Size v => v.ToString(CultureInfo.InvariantCulture),
            Rect v => v.ToString(CultureInfo.InvariantCulture),
            Color v => v.ToString(CultureInfo.InvariantCulture),
            SolidColorBrush v => v.Color.ToString(CultureInfo.InvariantCulture),
            _ => "(" + TypeName(value.GetType()) + ")"
        };
        return Limit(text, 2048);
    }
}
