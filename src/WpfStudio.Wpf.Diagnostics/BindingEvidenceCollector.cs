using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Diagnostics;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

/// <summary>Copies bounded facts from WPF events without retaining their model objects.</summary>
public sealed class BindingEvidenceCollector : IDisposable
{
    private const int MaximumBindings = 500;
    private const int MaximumEventsPerBinding = 4;
    private sealed record Captured(InspectionBindingEvidence Evidence, WeakReference<object>? Source);
    private sealed class History { public List<Captured> Events { get; } = []; public bool Truncated; }
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<BindingExpressionBase, History> _histories = new();
    private readonly Queue<WeakReference<BindingExpressionBase>> _order = new();
    private readonly bool _knownCodes = typeof(Binding).Assembly.GetName().Version?.Major is >= 8 and <= 10;
    private long _sequence;
    private bool _truncated;
    private bool _disposed;
    private readonly Func<BindingExpressionBase, bool>? _ignore;

    public BindingEvidenceCollector(Func<BindingExpressionBase, bool>? ignore = null)
    {
        _ignore = ignore;
        BindingDiagnostics.BindingFailed += OnBindingFailed;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _histories.Clear();
            _order.Clear();
            _truncated = false;
        }
    }

    public (IReadOnlyList<InspectionBindingEvidence> Events, bool Truncated) Read(BindingExpressionBase binding)
    {
        object? source = Source(binding);
        lock (_gate)
        {
            if (_disposed || !_histories.TryGetValue(binding, out var history)) return ([], _truncated);
            var events = history.Events.TakeLast(2).Select(captured => captured.Evidence with
            {
                SourceMatches = source is not null && captured.Source?.TryGetTarget(out var old) == true && ReferenceEquals(old, source)
            }).ToArray();
            return (events, _truncated || history.Truncated || history.Events.Count > events.Length);
        }
    }

    private void OnBindingFailed(object? sender, BindingFailedEventArgs args)
    {
        try
        {
            if (args.Binding is not { } binding || _ignore?.Invoke(binding) == true) return;
            string? kind = _knownCodes ? args.Code switch
            {
                5 => "TargetRejectedValue",
                6 or 7 or 22 or 23 or 27 or 28 or 29 or 30 or 32 => "ConversionFailure",
                8 or 18 => "SourceUpdateFailure",
                17 => "SourceGetterFailure",
                19 => "SourceUnavailable",
                40 => "MissingMember",
                41 => "NullPathItem",
                42 => "CollectionPlaceholder",
                86 => "FormattingFailure",
                _ => null
            } : "WpfNotification";
            // Codes 20/21 describe the consequent missing cached final item. Both
            // follow code40 (missing member) as well as code41 (null intermediate),
            // so they must not evict the distinct causal observations from history.
            if (kind is null) return;
            object? source = Source(binding);
            string? member = null;
            string? owner = null;
            if (_knownCodes && args.Code == 40 && args.Parameters is { Length: >= 2 })
            {
                member = args.Parameters[0] is string text ? BindingDiagnosticText.Limit(text, 256) : null;
                owner = args.Parameters[1] is { } value ? BindingDiagnosticText.TypeName(value.GetType()) : null;
            }
            string? path = (binding as BindingExpression)?.ParentBinding.Path?.Path;
            // The code values and parameter shapes are verified against the .NET
            // 8/9/10 WPF AvTraceMessages and PropertyPathWorker sources. Other
            // versions remain unclassified. Never parse localized trace prose.
            var evidence = new InspectionBindingEvidence(Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow,
                kind, args.Code, BindingDiagnosticText.Limit(args.Message, 768), member, owner,
                source is null ? null : BindingDiagnosticText.TypeName(source.GetType()), binding.Status.ToString(),
                false, path is null ? null : BindingDiagnosticText.Limit(path, 512));
            lock (_gate)
            {
                if (_disposed) return;
                if (!_histories.TryGetValue(binding, out var history))
                {
                    while (_order.Count >= MaximumBindings)
                    {
                        if (_order.Dequeue().TryGetTarget(out var previous)) _histories.Remove(previous);
                        _truncated = true;
                    }
                    _histories.Add(binding, history = new());
                    _order.Enqueue(new(binding));
                }
                if (history.Events.Count == MaximumEventsPerBinding)
                {
                    history.Events.RemoveAt(0);
                    history.Truncated = true;
                }
                history.Events.Add(new(evidence, source is null ? null : new(source)));
            }
        }
        catch (Exception) { } // Diagnostic callbacks never escape into the app's binding engine.
    }

    private static object? Source(BindingExpressionBase binding)
    {
        object? source = (binding as BindingExpression)?.DataItem;
        return ReferenceEquals(source, DependencyProperty.UnsetValue) || ReferenceEquals(source, BindingOperations.DisconnectedSource) ? null : source;
    }

    public void Dispose()
    {
        BindingDiagnostics.BindingFailed -= OnBindingFailed;
        lock (_gate)
        {
            _disposed = true;
            _histories.Clear();
            _order.Clear();
        }
    }
}
