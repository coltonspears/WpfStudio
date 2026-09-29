using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Xml;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.PreviewHost;

/// <summary>Dispatcher-owned preview and inspection, reusable by a future running-app agent.</summary>
public sealed partial class PreviewEngine : IPreviewRpc, IDisposable
{
    static PreviewEngine()
    {
        // Opt in before acquiring a trace source. Otherwise WPF's lazy AvTrace
        // initialization can clear DataBindingSource when no debugger is
        // attached, silently stranding a listener on the old source instance.
        PresentationTraceSources.Refresh();
        // Custom controls can use the standard WPF design-mode check in their
        // constructors to avoid starting application-only services in this host.
        DesignerProperties.IsInDesignModeProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(true));
        DesignerProperties.IsInDesignModeProperty.OverrideMetadata(typeof(FrameworkContentElement), new FrameworkPropertyMetadata(true));
    }

    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _requests = new(1);
    private readonly PreviewAssemblyResolver _assemblies;
    private readonly BindingTraceListener _trace;
    private readonly TraceSource _bindingTraceSource;
    private readonly SourceLevels _previousTraceLevel;
    private readonly List<PreviewDiagnostic> _diagnostics = [];
    private readonly List<PreviewDiagnostic> _traceDiagnostics = [];
    private readonly Dictionary<string, DependencyObject> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<DependencyObject, string> _ids = new(ReferenceEqualityComparer.Instance);
    private TemporaryPropertyEdits _edits = new();
    private readonly Dictionary<Type, IReadOnlyList<DependencyProperty>> _propertyCache = [];
    private HwndSource? _surface;
    private FrameworkElement? _viewport;
    private Window? _compiledWindow;
    private FrameworkElement? _root;
    private PreviewDocument? _document;
    private long _version;
    private int _width = 960, _height = 640;
    private bool _disposed;
    private string? _status;
    private PreviewBuildProvenance? _build;
    private PreviewScenarioActivation? _scenarioActivation;
    private PreviewScenarioProvenance? _scenario;

    public PreviewEngine(Dispatcher dispatcher, string? shadowDirectory = null, string? shadowToken = null,
        string? sessionId = null, int parentProcessId = 0)
    {
        _dispatcher = dispatcher;
        _session = new PreviewHostSession(sessionId ?? Guid.NewGuid().ToString("N"), Environment.ProcessId,
            parentProcessId, PreviewNativeNavigation.ProtocolVersion, Guid.TryParseExact(sessionId, "N", out _) && parentProcessId > 0 &&
                OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063));
        _assemblies = new PreviewAssemblyResolver(shadowDirectory, shadowToken);
        _trace = new BindingTraceListener(message =>
        {
            lock (_traceDiagnostics)
                if (_traceDiagnostics.Count < 200)
                    _traceDiagnostics.Add(new PreviewDiagnostic("Historical WPF binding trace: " + message, "Warning"));
        });
        _bindingTraceSource = PresentationTraceSources.DataBindingSource;
        _previousTraceLevel = _bindingTraceSource.Switch.Level;
        _bindingTraceSource.Switch.Level = SourceLevels.Information;
        _bindingTraceSource.Listeners.Add(_trace);
    }

    public Task<PreviewSnapshot> RenderAsync(PreviewRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(async () =>
        {
            ClearPreview();
            _appearanceEvidence.Clear();
            _version = request.Version;
            lock (_diagnostics) _diagnostics.Clear();
            lock (_traceDiagnostics) _traceDiagnostics.Clear();
            _status = null;
            _build = null;
            _scenario = null;
            try
            {
                if (!double.IsFinite(request.Width) || !double.IsFinite(request.Height) || request.Width < 32 || request.Height < 32 || request.Width > 4096 || request.Height > 4096)
                    throw new ArgumentException("Preview dimensions must be between 32 and 4096 pixels.");
                _width = (int)Math.Ceiling(request.Width);
                _height = (int)Math.Ceiling(request.Height);
                if (request.Mode is not (PreviewMode.Source or PreviewMode.Compiled)) throw new InvalidOperationException("Unknown preview mode.");
                var assembly = _assemblies.Load(request);
                _scenarioActivation = PreviewScenarioActivation.Validate(request, assembly);
                if (request.Mode == PreviewMode.Compiled)
                {
                    if (assembly is null) throw new InvalidOperationException("Compiled preview requires a built project assembly.");
                    (_root, _build) = CompiledPreview.Create(request, assembly, _scenarioActivation);
                    _status = $"Compiled view {_build.ViewTypeName} · build {_build.AssemblySha256[..12]}. Unsaved XAML is not included. " +
                        (_build.ApplicationResourcePath is null ? "Application resources were not requested. " : "Compiled application resources loaded. ") +
                        "Project App constructor and startup are not executed.";
                }
                else if (request.Mode == PreviewMode.Source)
                {
                if (_scenarioActivation is not null) CompiledPreview.PrepareApplication(request, assembly!);
                _document = PreviewDocument.Parse(request, assembly);
                lock (_diagnostics) _diagnostics.AddRange(_document.Diagnostics);
                // The secure source parse retains the original base URI and XML
                // positions, including attributes moved by preview transforms.
                using var markup = _document.CreateSourceReader();
                object loaded = XamlReader.Load(markup);
                if (loaded is ResourceDictionary resources)
                {
                    _root = new Border { Resources = resources, Child = new TextBlock { Text = $"Resource dictionary loaded · {resources.Count} resources", Margin = new Thickness(24), Foreground = Brushes.DimGray } };
                    _status = "Resources loaded. Open a view using this dictionary to see its appearance.";
                }
                else _root = loaded as FrameworkElement ?? throw new InvalidOperationException("The preview root must be a Window, Page, UserControl, FrameworkElement, or ResourceDictionary.");
                }
                if (_scenarioActivation is not null)
                {
                    _scenarioActivation.ApplyDataContext(_root!);
                    _scenario = _scenarioActivation.Provenance(request, _root!, _build);
                    _status = $"Scenario '{_scenario.Configuration.Name}' · build {_scenario.AssemblySha256[..12]}. " +
                        (_status ?? "Source preview uses the current XAML buffer. ") +
                        (_scenario.Configuration.DataContextFactory is null ? "" : " Scenario data overrides the root DataContext before layout.");
                }
                if (_root is Window window)
                {
                    _compiledWindow = window;
                    _viewport = window;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -32000; window.Top = -32000;
                    window.Width = _width; window.Height = _height;
                    window.WindowState = WindowState.Normal;
                    window.SizeToContent = SizeToContent.Manual;
                    window.ShowActivated = false; window.ShowInTaskbar = false; window.Topmost = false;
                    window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
                    window.Show();
                    // Keep the managed Window and its ancestor/resource scopes;
                    // hide only its native surface after offscreen initialization.
                    var handle = new WindowInteropHelper(window).Handle;
                    ShowWindow(handle, 0);
                    _surface = HwndSource.FromHwnd(handle);
                }
                else
                {
                UIElement content = _root is Page page ? new Frame { Content = page, NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden } : _root!;
                _viewport = new Border { Width = _width, Height = _height, Background = Brushes.White, Child = content, ClipToBounds = true };
                // A real, hidden presentation source activates bindings, templates,
                // resource inheritance and Loaded without showing a second window.
                _surface = new HwndSource(new HwndSourceParameters("WpfStudio isolated preview")
                {
                    Width = _width, Height = _height, WindowStyle = unchecked((int)0x80000000),
                    PositionX = -32000, PositionY = -32000
                }) { RootVisual = _viewport };
                }
                await SettleLayoutAsync();
                InitializeNativeSurface();
                return Snapshot();
            }
            catch (Exception exception)
            {
                RecordException(exception, sourceCoordinates: exception is XmlException);
                ClearPreview();
                return Snapshot();
            }
        }, cancellationToken);

    public Task<PreviewInspection> InspectAsync(PreviewNodeRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() => Task.FromResult(Inspect(request.Version, request.NodeId)), cancellationToken);

    public Task<PreviewInspection> PickAsync(PreviewPickRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() =>
        {
            if (request.Version != _version || _viewport is null) return Task.FromResult(EmptyInspection("The preview has changed. Refresh the selection."));
            if (!double.IsFinite(request.X) || !double.IsFinite(request.Y) || request.X < 0 || request.Y < 0 || request.X >= _width || request.Y >= _height)
                return Task.FromResult(EmptyInspection("The point is outside the preview."));
            DependencyObject? hit = _viewport.InputHitTest(new Point(request.X, request.Y)) as DependencyObject;
            while (hit is not null && !_ids.ContainsKey(hit)) hit = VisualParent(hit);
            return Task.FromResult(hit is not null ? Inspect(request.Version, _ids[hit]) : EmptyInspection("No element at this point."));
        }, cancellationToken);

    public Task<PreviewEditResult> SetPropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken) =>
        OnDispatcher(async () =>
        {
            string? error = null;
            try
            {
                if (request.Version != _version || !_objects.TryGetValue(request.NodeId, out var target))
                    throw new InvalidOperationException("The preview has changed. Refresh the selection.");
                var property = FindProperty(target, request);
                // Trace output describes the previous evaluation. Current binding
                // expression status below is the authority after a live edit.
                lock (_traceDiagnostics) _traceDiagnostics.Clear();
                var edited = request.Reset ? _edits.Reset(target, property) : _edits.Apply(target, property, request.Value, request.Value is null);
                if (!edited.Success) error = edited.Error;
                await SettleLayoutAsync();
            }
            catch (Exception exception) { error = exception.GetBaseException().Message; }
            return new PreviewEditResult(error is null, Snapshot(), Inspect(request.Version, request.NodeId), error);
        }, cancellationToken);

    public void ReportUnhandledException(Exception exception) => RecordException(exception, false);

    public Task<PreviewPropertyValidation> ValidatePropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken) =>
        OnDispatcher(() =>
        {
            try
            {
                if (request.Version != _version || !_objects.TryGetValue(request.NodeId, out var target))
                    throw new InvalidOperationException("The preview has changed. Refresh the selection.");
                var property = FindProperty(target, request);
                if (request.Reset || property.ReadOnly || !ScalarPropertyValues.Supports(property.PropertyType))
                    throw new InvalidOperationException("This property does not support writing a scalar value.");
                if (_document is not null && !_document.CanWriteProperty(target, property, IsAttached(property, target)))
                    throw new InvalidOperationException("The authored XAML element does not expose this writable property.");
                if (_document?.IsDesignTimeProperty(target, property) == true || _scenarioActivation?.IsDataContextProperty(target, property) == true)
                    throw new InvalidOperationException("This property uses a design-time value or scenario data. Runtime XAML writeback is unavailable for that preview override.");
                if (!ScalarPropertyValues.TryConvert(property.PropertyType, request.Value, request.Value is null, out var value, out var error))
                    throw new InvalidOperationException(error);
                if (!property.IsValidValue(value))
                    throw new InvalidOperationException("The value is not valid for this property.");
                return Task.FromResult(new PreviewPropertyValidation(true));
            }
            catch (Exception exception) { return Task.FromResult(new PreviewPropertyValidation(false, exception.GetBaseException().Message)); }
        }, cancellationToken);

    private async Task<T> OnDispatcher<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task.Unwrap().ConfigureAwait(false);
        }
        finally { _requests.Release(); }
    }

    private async Task SettleLayoutAsync()
    {
        if (_viewport is null) return;
        _viewport.Measure(new Size(_width, _height));
        _viewport.Arrange(new Rect(0, 0, _width, _height));
        _viewport.UpdateLayout();
        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        _viewport.UpdateLayout();
    }

    private PreviewSnapshot Snapshot()
    {
        if (_viewport is null || _root is null) return new(_version, false, null, _width, _height, [], Diagnostics(), _status, _build, _scenario);
        var nodes = CollectNodes();
        var bitmap = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_viewport);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var bindingFailures = _objects.SelectMany(pair => BindingFailures(pair.Key, pair.Value));
        return new(_version, true, stream.ToArray(), _width, _height, nodes, Diagnostics().Concat(bindingFailures).Distinct().ToArray(), _status ??
            "Isolated XAML preview. Code-behind and application startup are not executed. Source navigation tracks authored elements, including template instances.", _build, _scenario, _surfaceIdentity);
    }

    private IReadOnlyList<PreviewNode> CollectNodes()
    {
        if (_root is null) return [];
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var ordered = new List<DependencyObject>();
        var parents = new Dictionary<DependencyObject, DependencyObject?>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(DependencyObject Node, DependencyObject? Parent)>();
        stack.Push((_root, null));
        while (stack.Count > 0 && visited.Count < 10_000)
        {
            var current = stack.Pop();
            if (!visited.Add(current.Node)) continue;
            ordered.Add(current.Node);
            parents[current.Node] = current.Parent;
            GetId(current.Node);
            var children = new List<DependencyObject>();
            if (current.Node is Visual or Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current.Node); i++) children.Add(VisualTreeHelper.GetChild(current.Node, i));
            foreach (var child in LogicalTreeHelper.GetChildren(current.Node).OfType<DependencyObject>())
                if (!children.Contains(child)) children.Add(child);
            for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], current.Node));
        }
        if (stack.Count > 0)
            lock (_diagnostics) _diagnostics.Add(new PreviewDiagnostic("The inspector tree is limited to 10,000 elements. The preview image still renders the complete view.", "Warning"));
        // Remove template instances and items no longer present after live edits.
        foreach (var stale in _ids.Keys.Where(o => !visited.Contains(o)).ToArray())
        {
            _objects.Remove(_ids[stale]);
            _ids.Remove(stale);
        }
        return ordered.Select(o => Node(o, parents[o])).ToArray();
    }

    private string GetId(DependencyObject target)
    {
        if (_ids.TryGetValue(target, out var existing)) return existing;
        string id = Guid.NewGuid().ToString("N");
        _ids[target] = id;
        _objects[id] = target;
        return id;
    }

    private PreviewNode Node(DependencyObject target, DependencyObject? parent = null)
    {
        string? name = target is FrameworkElement element ? element.Name : target is FrameworkContentElement content ? content.Name : null;
        parent ??= VisualParent(target) ?? LogicalTreeHelper.GetParent(target);
        DependencyObject? logicalParent = LogicalTreeHelper.GetParent(target);
        PreviewBounds? bounds = null;
        if (target is Visual visual && _viewport is not null)
        {
            try
            {
                Rect rectangle = target is UIElement ui ? new Rect(ui.RenderSize) : VisualTreeHelper.GetDescendantBounds(visual);
                rectangle = visual.TransformToAncestor(_viewport).TransformBounds(rectangle);
                if (!rectangle.IsEmpty && double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y) && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height))
                    bounds = new PreviewBounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            }
            catch (InvalidOperationException) { }
        }
        return new PreviewNode(GetId(target), parent is not null ? _ids.GetValueOrDefault(parent) : null,
            logicalParent is not null ? _ids.GetValueOrDefault(logicalParent) : null, target.GetType().FullName ?? target.GetType().Name,
            string.IsNullOrEmpty(name) ? null : name, bounds, _document?.FindSource(target, ReferenceEquals(target, _root)), target is Visual or Visual3D);
    }

    private PreviewInspection Inspect(long version, string nodeId)
    {
        if (version != _version || !_objects.TryGetValue(nodeId, out var target)) return EmptyInspection("The preview has changed. Refresh the selection.");
        string? contextType = target is FrameworkElement element ? element.DataContext?.GetType().FullName : target is FrameworkContentElement content ? content.DataContext?.GetType().FullName : null;
        var properties = new List<PreviewProperty>();
        var diagnostics = new List<PreviewDiagnostic>();
        int bindingRowsRemaining = 256, bindingCharactersRemaining = 131072;
        var bindingBudget = new BindingDetailBudget(65536);
        foreach (var property in Properties(target))
        {
            try
            {
                var source = DependencyPropertyHelper.GetValueSource(target, property);
                BindingExpressionBase? expression = BindingOperations.GetBindingExpressionBase(target, property);
                bool scenarioData = _scenarioActivation?.IsDataContextProperty(target, property) == true;
                bool ownsScenarioData = _scenarioActivation?.OwnsDataContext(target, property) == true;
                bool designTime = _document?.IsDesignTimeProperty(target, property) == true;
                if (TemporaryPropertyEdits.IsOverrideBinding(expression) || ownsScenarioData) expression = null;
                string? path = expression is BindingExpression binding ? binding.ParentBinding.Path?.Path : expression is MultiBindingExpression ? "(MultiBinding)" : expression is PriorityBindingExpression ? "(PriorityBinding)" : null;
                string propertyName = PropertyName(property, target);
                object? value = target.GetValue(property);
                var observation = expression is null ? null : bindingBudget.Take(BindingReader.Read(expression, _bindingEvidence));
                var bindingSources = CaptureBindingSources(expression, ref bindingRowsRemaining, ref bindingCharactersRemaining, bindingBudget);
                var editInfo = _edits.Describe(target, property);
                string? editableValue = editInfo.EditableValue;
                properties.Add(new PreviewProperty(propertyName, property.PropertyType.FullName ?? property.PropertyType.Name,
                    Format(value), editInfo.IsOverridden && designTime ? "Preview override (design baseline)" : ownsScenarioData ? "Scenario" :
                        source.BaseValueSource + (designTime ? " (design baseline)" : ""), source.IsExpression, source.IsAnimated,
                    source.IsCoerced, editInfo.CanEdit, path, expression?.Status.ToString(), contextType,
                    editInfo.IsOverridden, property.OwnerType.FullName, property.OwnerType.Assembly.GetName().Name,
                    IsAttached(property, target), editableValue, !scenarioData && !designTime && !property.ReadOnly && ScalarPropertyValues.Supports(property.PropertyType) &&
                        _document?.CanWriteProperty(target, property, IsAttached(property, target)) == true,
                    target.GetType().GetCustomAttribute<ContentPropertyAttribute>(inherit: true)?.Name,
                    bindingSources, observation));
                if (observation is not null && IsBindingFailure(observation))
                    diagnostics.Add(BindingDiagnostic(nodeId, propertyName, observation, bindingSources?.BindingId));
            }
            catch (Exception exception) { diagnostics.Add(new PreviewDiagnostic($"Cannot inspect {property.Name}: {exception.GetBaseException().Message}", "Warning", NodeId: nodeId, Property: property.Name)); }
        }
        return new PreviewInspection(_version, Node(target), properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(), diagnostics,
            Layout: _viewport is null ? null : Wpf.Diagnostics.LayoutReader.Capture(target, _viewport), LayoutEditing: CaptureLayoutEditing(target));
    }

    private IEnumerable<PreviewDiagnostic> BindingFailures(string nodeId, DependencyObject target)
    {
        foreach (var property in Properties(target))
        {
            var expression = BindingOperations.GetBindingExpressionBase(target, property);
            if (expression is null || TemporaryPropertyEdits.IsOverrideBinding(expression) || _scenarioActivation?.OwnsDataContext(target, property) == true) continue;
            var observation = BindingReader.Read(expression, _bindingEvidence);
            if (!IsBindingFailure(observation)) continue;
            string id = _bindingSources.Capture(expression, maximumDeclarations: 0, maximumCharacters: 0).BindingId;
            yield return BindingDiagnostic(nodeId, PropertyName(property, target), observation, id);
        }
    }

    private IReadOnlyList<DependencyProperty> Properties(DependencyObject target)
    {
        Type type = target.GetType();
        if (!_propertyCache.TryGetValue(type, out var cached))
        {
            var properties = new HashSet<DependencyProperty>();
            for (Type? current = type; current is not null; current = current.BaseType)
                foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (field.FieldType == typeof(DependencyProperty) && field.GetValue(null) is DependencyProperty property) properties.Add(property);
            foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(target))
                if (DependencyPropertyDescriptor.FromProperty(descriptor)?.DependencyProperty is { } property) properties.Add(property);
            cached = properties.ToArray();
            _propertyCache[type] = cached;
        }
        var local = target.GetLocalValueEnumerator();
        var all = new HashSet<DependencyProperty>(cached);
        while (local.MoveNext()) all.Add(local.Current.Property);
        all.Remove(PreviewSource.IdProperty);
        return all.ToArray();
    }

    private static string PropertyName(DependencyProperty property, DependencyObject target) =>
        !IsAttached(property, target) ? property.Name : property.OwnerType.Name + "." + property.Name;

    private DependencyProperty FindProperty(DependencyObject target, PreviewPropertyEdit request)
    {
        var candidates = Properties(target).Where(p => PropertyName(p, target) == request.Property);
        if (request.OwnerType is not null || request.OwnerAssembly is not null)
        {
            if (string.IsNullOrWhiteSpace(request.OwnerType) || string.IsNullOrWhiteSpace(request.OwnerAssembly))
                throw new InvalidOperationException("Both property owner type and assembly are required for an exact property identity.");
            candidates = candidates.Where(p => p.OwnerType.FullName == request.OwnerType && p.OwnerType.Assembly.GetName().Name == request.OwnerAssembly);
        }
        var matches = candidates.Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            > 1 => throw new InvalidOperationException("The property name is ambiguous. Select a property using its full owner type and assembly."),
            _ => throw new InvalidOperationException("The dependency property was not found.")
        };
    }

    private static bool IsAttached(DependencyProperty property, DependencyObject target)
    {
        var wrapper = target.GetType().GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
        if (wrapper?.SetMethod?.IsPublic != true || wrapper.PropertyType != property.PropertyType) return true;
        var descriptor = TypeDescriptor.GetProperties(target)[property.Name];
        return descriptor is null || DependencyPropertyDescriptor.FromProperty(descriptor)?.DependencyProperty != property;
    }

    private static string Format(object? value)
    {
        if (value is null) return "(null)";
        string text = value is IFormattable formatted ? formatted.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? "";
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }

    private static DependencyObject? VisualParent(DependencyObject target) => target is Visual or Visual3D ? VisualTreeHelper.GetParent(target) : null;
    private PreviewInspection EmptyInspection(string status) => new(_version, null, [], [], status);
    private IReadOnlyList<PreviewDiagnostic> Diagnostics()
    {
        lock (_diagnostics)
        lock (_traceDiagnostics)
            return _diagnostics.Concat(_traceDiagnostics).Distinct().ToArray();
    }

    private void RecordException(Exception exception, bool sourceCoordinates)
    {
        int? line = sourceCoordinates && exception is XmlException xml ? xml.LineNumber : null;
        int? column = sourceCoordinates && exception is XmlException xmlError ? xmlError.LinePosition : null;
        // XamlReader positions refer to transformed markup, so never present them
        // as precise positions in the original source buffer.
        lock (_diagnostics) _diagnostics.Add(new PreviewDiagnostic(exception.GetBaseException().Message, "Error", line, column));
    }

    private void ClearPreview()
    {
        _layoutObservations.Clear();
        ClearNativeSurface();
        foreach (var weak in _edits.GetEditedTargets())
            if (weak.TryGetTarget(out var target)) _edits.ResetAll(target);
        _edits = new();
        if (_compiledWindow is not null)
        {
            _compiledWindow.Close();
            _compiledWindow = null;
        }
        if (_surface is not null)
        {
            if (!_surface.IsDisposed) { _surface.RootVisual = null; _surface.Dispose(); }
            _surface = null;
        }
        if (_viewport is Border border) border.Child = null;
        _viewport = null;
        _root = null;
        _document = null;
        _scenarioActivation = null;
        _bindingSources = new();
        _bindingEvidence.Clear();
        _objects.Clear();
        _ids.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_dispatcher.CheckAccess()) ClearPreview();
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(ClearPreview);
        _bindingTraceSource.Listeners.Remove(_trace);
        _bindingTraceSource.Switch.Level = _previousTraceLevel;
        _trace.Dispose();
        _appearanceEvidence.Dispose();
        _bindingEvidence.Dispose();
        _assemblies.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    private sealed class BindingTraceListener(Action<string> report) : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) report(message); }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (Keep(eventType, id) && !string.IsNullOrWhiteSpace(message)) report(Limit(message, 4096));
        }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (!Keep(eventType, id) || string.IsNullOrWhiteSpace(format)) return;
            // Composite formatting may execute arbitrary application ToString or
            // IFormattable implementations. Preserve bounded text and safe facts.
            var text = Limit(format, 3072);
            if (args is { Length: > 0 }) text += " | " + string.Join(", ", args.Take(16).Select(Describe));
            report(Limit(text, 4096));
        }

        private static bool Keep(TraceEventType type, int id) =>
            (type & (TraceEventType.Critical | TraceEventType.Error | TraceEventType.Warning)) != 0 || id is 40 or 41 or 42;
        private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
        private static string Describe(object? value) => value switch
        {
            null => "null",
            string text => Limit(text, 512),
            bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            _ => "(" + Limit(value.GetType().FullName ?? value.GetType().Name, 512) + ")"
        };
    }
}
