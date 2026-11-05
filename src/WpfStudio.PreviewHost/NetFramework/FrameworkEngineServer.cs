using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using Newtonsoft.Json;
using WpfStudio.Contracts;
using WpfStudio.PreviewDependencies;

namespace WpfStudio.PreviewHost;

/// <summary>Work the engine asks of the transport's domain, because it needs libraries project code must not share a binder with.</summary>
public sealed class FrameworkHostServices(Func<string, string?> readLibrary) : MarshalByRefObject
{
    public override object? InitializeLifetimeService() => null;
    /// <summary>A library's XAML namespace declarations as JSON, or null when it declares none.</summary>
    public string? ReadLibrary(string path) => readLibrary(path);
}

/// <summary>The outcome of one engine call, in the transport's domain.</summary>
public sealed class EngineCompletion : MarshalByRefObject
{
    private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string> Task => _result.Task;
    public override object? InitializeLifetimeService() => null;
    public void Succeed(string json) => _result.TrySetResult(json);
    public void Cancel() => _result.TrySetCanceled();
    public void Fail(string message) => _result.TrySetException(new InvalidOperationException(message));
}

/// <summary>Cancels one engine call from the transport's domain.</summary>
public sealed class EngineCallCancellation : MarshalByRefObject
{
    internal CancellationTokenSource Source { get; } = new();
    public override object? InitializeLifetimeService() => null;
    public void Cancel()
    {
        try { Source.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

/// <summary>
/// The engine, inside the project's domain. Calls arrive as a method name and JSON text, so the contract types never cross the
/// boundary and every call reaches the engine exactly as the transport received it.
/// </summary>
public sealed class FrameworkEngineServer : MarshalByRefObject
{
    private static FrameworkHostServices? _services;
    // Created here rather than through JsonConvert: project code can change the library's process-wide defaults.
    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore,
        ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
        DateParseHandling = DateParseHandling.None,
        Culture = CultureInfo.InvariantCulture,
    });
    private Application? _application;
    private PreviewEngine? _engine;

    public override object? InitializeLifetimeService() => null;

    /// <summary>Starts the dedicated WPF thread that owns every preview object. Returns once it can take requests.</summary>
    public void Start(string? shadowDirectory, string? shadowToken, string? sessionId, int parentProcessId, FrameworkHostServices services)
    {
        _services = services;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var engine = new PreviewEngine(application.Dispatcher, shadowDirectory, shadowToken, sessionId, parentProcessId);
                application.DispatcherUnhandledException += (_, e) =>
                {
                    engine.ReportUnhandledException(e.Exception);
                    e.Handled = true;
                };
                (_application, _engine) = (application, engine);
            }
            catch (Exception exception)
            {
                failure = exception;
                return;
            }
            finally
            {
                ready.Set();
            }
            _application.Run();
            _engine.Dispose();
        })
        { IsBackground = true, Name = "WPF preview" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null) throw new InvalidOperationException("The preview engine could not start: " + failure);
    }

    public EngineCallCancellation Invoke(string method, string? argument, EngineCompletion completion)
    {
        var cancellation = new EngineCallCancellation();
        InvokeAsync(method, argument, completion, cancellation).Forget();
        return cancellation;
    }

    private async Task InvokeAsync(string method, string? argument, EngineCompletion completion, EngineCallCancellation cancellation)
    {
        string? result = null;
        Exception? failure = null;
        try
        {
            var target = typeof(IPreviewRpc).GetMethod(method) ?? throw new MissingMethodException(nameof(IPreviewRpc), method);
            var parameters = target.GetParameters();
            object?[] arguments = parameters.Length == 2
                ? [Deserialize(argument, parameters[0].ParameterType), cancellation.Source.Token]
                : [cancellation.Source.Token];
            var call = (Task)target.Invoke(_engine, arguments);
            await call.ConfigureAwait(false);
            result = Serialize(call.GetType().GetProperty(nameof(Task<>.Result))!.GetValue(call));
        }
        catch (TargetInvocationException exception) { failure = exception.GetBaseException(); }
        catch (Exception exception) { failure = exception; }
        if (failure is OperationCanceledException) completion.Cancel();
        else if (failure is not null) completion.Fail(failure.Message);
        else completion.Succeed(result!);
    }

    /// <summary>Ends the WPF thread. The caller bounds this, because a control can block its dispatcher.</summary>
    public void Shutdown()
    {
        if (_application is { } application) _ = application.Dispatcher.InvokeAsync(application.Shutdown);
    }

    internal static XamlNamespaceLibrary? ReadLibrary(string path) =>
        _services?.ReadLibrary(path) is { } json ? Deserialize(json, typeof(XamlNamespaceLibrary)) as XamlNamespaceLibrary : null;

    private static object? Deserialize(string? json, Type type)
    {
        if (json is null) return null;
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return Serializer.Deserialize(reader, type);
    }

    internal static string Serialize(object? value)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Serializer.Serialize(writer, value);
        return writer.ToString();
    }
}
