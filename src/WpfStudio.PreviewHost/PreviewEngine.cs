using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using WpfStudio.Contracts;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.PreviewHost;

/// <summary>Dispatcher-owned preview and inspection, reusable by a future running-app agent. This is the RPC surface: each request is serialized here and the work is done by the parts it composes.</summary>
public sealed class PreviewEngine : IPreviewRpc, IDisposable
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
    private readonly PreviewDiagnosticLog _log = new();
    private readonly PreviewScene _scene = new();
    private readonly PreviewXamlLoader _loader;
    private readonly PreviewVisualTree _tree;
    private readonly PreviewNativeSurface _native;
    private readonly PreviewLayoutEditing _layout;
    private readonly PreviewInspector _inspector;
    private string? _status;
    private PreviewBuildProvenance? _build;
    private PreviewScenarioProvenance? _scenario;

    public PreviewEngine(Dispatcher dispatcher, string? shadowDirectory = null, string? shadowToken = null,
        string? sessionId = null, int parentProcessId = 0)
    {
        _dispatcher = dispatcher;
        _native = new PreviewNativeSurface(_scene, dispatcher, PreviewHostSessions.Create(sessionId, parentProcessId));
        _tree = new PreviewVisualTree(_scene, _log);
        _layout = new PreviewLayoutEditing(_scene, _tree, _native);
        _inspector = new PreviewInspector(_scene, _tree, _layout, _log);
        _assemblies = new PreviewAssemblyResolver(shadowDirectory, shadowToken);
        _loader = new PreviewXamlLoader(_assemblies, _log);
        _log.StartBindingTrace();
    }

    public Task<PreviewSnapshot> RenderAsync(PreviewRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(async () =>
        {
            var failure = await TryRenderAsync(request, cancellationToken);
            // Design-time markup is an aid to the view, not part of it. When a render that ran some fails, the authored
            // view is still worth showing, so it is rendered again without any. A second failure is the view's own.
            if (failure is not null && request.UseDesignTimeValues && _loader.UsedDesignMarkup)
            {
                string reason = failure.GetBaseException().Message;
                failure = await TryRenderAsync(request with { UseDesignTimeValues = false }, cancellationToken);
                if (failure is null)
                    _log.Add(new($"The preview failed with its design-time values applied, so it is shown without them: {reason}", "Warning"));
            }
            if (failure is not null)
            {
                _log.Record(failure, sourceCoordinates: failure is XmlException);
                // Name every assembly the loader asked for and could not find, with the places it looked.
                _log.AddRange(_assemblies.UnresolvedAssemblies().Select(message => new PreviewDiagnostic(message, "Warning")));
            }
            return Snapshot();
        }, cancellationToken);

    /// <summary>Loads, hosts and lays out the requested view. Returns what stopped it, with the preview cleared, or null once the view is ready to capture.</summary>
    private async Task<Exception?> TryRenderAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        ClearPreview();
        _loader.BeginRender();
        _inspector.ForgetAppearanceEvidence();
        _scene.Version = request.Version;
        _log.Clear();
        _assemblies.ForgetUnresolved();
        _log.ClearTrace();
        _status = null;
        _build = null;
        _scenario = null;
#if NETFRAMEWORK
        _log.Add(new($"Preview is running on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {(Environment.Is64BitProcess ? 64 : 32)}-bit, chosen from this project's build.", "Information"));
#endif
        try
        {
            if (!double.IsFinite(request.Width) || !double.IsFinite(request.Height) || request.Width < 32 || request.Height < 32 || request.Width > 4096 || request.Height > 4096)
                throw new ArgumentException("Preview dimensions must be between 32 and 4096 pixels.");
            _scene.Width = (int)Math.Ceiling(request.Width);
            _scene.Height = (int)Math.Ceiling(request.Height);
            if (request.Mode is not (PreviewMode.Source or PreviewMode.Compiled)) throw new InvalidOperationException("Unknown preview mode.");
            var assembly = _assemblies.Load(request);
            // The steps below can each take a while. A render a newer one has replaced stops between them, so the newer one starts sooner.
            cancellationToken.ThrowIfCancellationRequested();
            _scene.ScenarioActivation = PreviewScenarioActivation.Validate(request, assembly);
            var loaded = _loader.Load(request, assembly, _scene.ScenarioActivation, cancellationToken);
            _scene.Root = loaded.Root; _scene.Document = loaded.Document; _build = loaded.Build; _status = loaded.Status;
            if (request.SizeToRoot) _scene.ApplyRootSize();
            if (_scene.ScenarioActivation is { } activation)
            {
                activation.ApplyDataContext(loaded.Root);
                _scenario = activation.Provenance(request, loaded.Root, _build);
                _status = $"Scenario '{_scenario.Configuration.Name}' · build {_scenario.AssemblySha256[..12]}. " +
                    (_status ?? "Source preview uses the current XAML buffer. ") +
                    (_scenario.Configuration.DataContextFactory is null ? "" : " Scenario data overrides the root DataContext before layout.");
            }
            _scene.Host();
            cancellationToken.ThrowIfCancellationRequested();
            await _scene.SettleLayoutAsync(_dispatcher);
            cancellationToken.ThrowIfCancellationRequested();
            _native.Initialize();
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearPreview();
            throw;
        }
        catch (Exception exception)
        {
            ClearPreview();
            return exception;
        }
    }

    public Task<PreviewSnapshot> CaptureAsync(PreviewCaptureRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(async () =>
        {
            var root = _scene.Root;
            var viewport = _scene.Viewport;
            var surface = _scene.Surface;
            bool Current() => !_scene.IsClosed && request.Version == _scene.Version && root is not null && viewport is not null
                && surface is { IsDisposed: false } && ReferenceEquals(root, _scene.Root)
                && ReferenceEquals(viewport, _scene.Viewport) && ReferenceEquals(surface, _scene.Surface);
            PreviewSnapshot Unavailable() => new(request.Version, false, null, 0, 0, [], [],
                "The preview changed or is unavailable. Refresh the preview before updating its snapshot.");

            if (!Current()) return Unavailable();
            // Let ordinary binding/layout work run. Capturing must not reapply
            // scenario data, recreate controls, or force new layout constraints.
            await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Current()) return Unavailable();
            return Snapshot();
        }, cancellationToken);

    public Task<PreviewInspection> InspectAsync(PreviewNodeRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() => Task.FromResult(_inspector.Inspect(request.Version, request.NodeId)), cancellationToken);

    public Task<PreviewInspection> PickAsync(PreviewPickRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() => Task.FromResult(_inspector.Pick(request)), cancellationToken);

    public Task<PreviewEditResult> SetPropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken) =>
        OnDispatcher(async () =>
        {
            string? error = null;
            try
            {
                error = _inspector.ApplyEdit(request);
                await _scene.SettleLayoutAsync(_dispatcher);
            }
            catch (Exception exception) { error = exception.GetBaseException().Message; }
            return new PreviewEditResult(error is null, Snapshot(), _inspector.Inspect(request.Version, request.NodeId), error);
        }, cancellationToken);

    public void ReportUnhandledException(Exception exception) => _log.Record(exception, false);

    public Task<PreviewPropertyValidation> ValidatePropertyAsync(PreviewPropertyEdit request, CancellationToken cancellationToken) =>
        OnDispatcher(() => Task.FromResult(_inspector.ValidateProperty(request)), cancellationToken);

    public Task<PreviewLayoutValidationResult> ValidateLayoutEditAsync(PreviewLayoutValidationRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_layout.Validate(request));
        }, cancellationToken);

    public Task<AppearanceResponse> GetAppearanceAsync(AppearanceRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() => Task.FromResult(_inspector.GetAppearance(request)), cancellationToken);

    public Task<BindingSourceResponse> GetBindingSourceAsync(BindingSourceRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() => Task.FromResult(_inspector.GetBindingSource(request)), cancellationToken);

    public Task<PreviewHostSession> GetSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_native.Session);
    }

    public Task<PreviewSurfaceHeartbeat> HeartbeatSurfaceAsync(PreviewSurfaceHeartbeatRequest request, CancellationToken cancellationToken) =>
        // This acknowledgement deliberately bypasses _requests and runs no application
        // getters, layout or rendering. The client can observe a dispatcher blocked by an
        // input handler even while the ordinary RPC semaphore is occupied.
        _dispatcher.InvokeAsync(() => _native.Heartbeat(request), DispatcherPriority.Normal, cancellationToken).Task;

    public Task<PreviewSurfaceResponse> UpdateSurfaceAsync(PreviewSurfaceRequest request, CancellationToken cancellationToken) =>
        OnDispatcher(() => Task.FromResult(_native.Update(request)), cancellationToken);

    /// <summary>Holds <see cref="_requests"/> until the whole action ends: a render yields to the dispatcher, and nothing may interleave with it.</summary>
    private async Task<T> OnDispatcher<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_scene.IsClosed, this);
            return await _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task.Unwrap().ConfigureAwait(false);
        }
        finally { _requests.Release(); }
    }

    private PreviewSnapshot Snapshot()
    {
        if (_scene.Viewport is null || _scene.Root is null) return new(_scene.Version, false, null, _scene.Width, _scene.Height, [], _log.Snapshot(), _status, _build, _scenario);
        var nodes = _tree.CollectNodes();
        var image = _scene.CapturePng();
        var bindingFailures = _inspector.BindingFailures();
        string? dataNotice = _scene.Root?.DataContext is null && bindingFailures.Any(failure => failure.UnavailableDataContext)
            ? "The preview has no root DataContext, and some binding sources are unavailable. Declare d:DataContext on the view, for example d:DataContext=\"{d:DesignInstance Type=local:MainViewModel}\", or choose a preview scenario with a data factory; application startup is not run." : null;
        var diagnostics = _log.Snapshot().Concat(bindingFailures.Select(failure => failure.Diagnostic));
        if (dataNotice is not null) diagnostics = diagnostics.Prepend(new PreviewDiagnostic(dataNotice, "Information"));
        string status = _status ?? "Isolated XAML preview. Code-behind and application startup are not executed. Source navigation tracks authored elements, including template instances.";
        return new(_scene.Version, true, image, _scene.Width, _scene.Height, nodes, diagnostics.Distinct().ToArray(),
            dataNotice is null ? status : dataNotice + " " + status, _build, _scenario, _native.Identity,
            _scene.Document?.DesignData is { Count: > 0 } designData ? designData.ToArray() : null);
    }

    private void ClearPreview()
    {
        _layout.Clear();
        _native.Clear();
        _inspector.ResetEdits();
        _scene.Close();
        _inspector.ForgetBindings();
        _tree.Clear();
    }

    public void Dispose()
    {
        if (_scene.IsClosed) return;
        _scene.IsClosed = true;
        if (_dispatcher.CheckAccess()) ClearPreview();
#pragma warning disable RS0030 // Dispose is synchronous, and the preview must clear on its thread before the log and inspector below go. Program bounds a blocked dispatcher with its exit deadline.
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(ClearPreview);
#pragma warning restore RS0030
        _log.Dispose();
        _inspector.Dispose();
        _assemblies.Dispose();
    }
}
