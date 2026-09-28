using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Diagnostics;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

/// <summary>Bounded historical WPF notifications. No resource values or application objects are retained.</summary>
public sealed class StaticResourceEvidenceCollector : IDisposable
{
    private const int MaximumEvents = 1024;
    private sealed record Entry(WeakReference<object> Target, WeakReference<object>? Member,
        string? MemberOwner, string MemberName, AppearanceResourceEvidence Evidence);
    private readonly object _gate = new();
    private readonly Queue<Entry> _events = new();
    private long _sequence;
    private bool _disposed, _dropped;

    public StaticResourceEvidenceCollector() => ResourceDictionaryDiagnostics.StaticResourceResolved += Resolved;

    public void Clear()
    {
        lock (_gate) { _events.Clear(); _dropped = false; }
    }

    private void Resolved(object? sender, StaticResourceResolvedEventArgs args)
    {
        // This callback can run inside XAML loading on any dispatcher. Do not perform
        // lookups, dispatch work, inspect values, or allow diagnostics to break loading.
        try
        {
            if (args.TargetObject is not { } target) return;
            object? member = args.TargetProperty;
            var reflected = member is PropertyInfo info && info.GetType().Assembly == typeof(PropertyInfo).Assembly ? info : null;
            string name = member is DependencyProperty dp ? dp.Name : reflected?.Name ?? "(unknown member)";
            Type? ownerType = member is DependencyProperty property ? property.OwnerType : reflected?.DeclaringType;
            if (ownerType is not null && ownerType.GetType().Assembly != typeof(Type).Assembly) return;
            string? owner = ownerType?.FullName ?? ownerType?.Name;
            // Keep exact names for identity fallback. Truncated names must never
            // compare equal merely because their retained prefixes happen to match.
            if (name.Length > 512 || owner?.Length > 512) return;
            string propertyText = owner is null ? name : owner + "." + name;
            var captured = new AppearanceResourceEvidence(0, DateTimeOffset.UtcNow,
                AppearanceText.TypeName(target.GetType()), AppearanceText.Limit(propertyText), AppearanceText.Value(args.ResourceKey),
                AppearanceText.UriText(args.ResourceDictionary?.Source), AppearanceText.Source(target));
            lock (_gate)
            {
                if (_disposed) return;
                if (_events.Count == MaximumEvents) { _events.Dequeue(); _dropped = true; }
                _events.Enqueue(new(new(target), member is null ? null : new(member), owner, name,
                    captured with { Id = ++_sequence }));
            }
        }
        catch (Exception) { }
    }

    internal (IReadOnlyList<AppearanceResourceEvidence> Events, bool Truncated) Snapshot(
        IReadOnlyList<(object Target, object Member)> associations)
    {
        var result = new List<AppearanceResourceEvidence>();
        var byTarget = new Dictionary<object, List<object>>(ReferenceEqualityComparer.Instance);
        foreach (var association in associations.Take(512))
        {
            if (!byTarget.TryGetValue(association.Target, out var members)) byTarget[association.Target] = members = [];
            members.Add(association.Member);
        }
        var watch = Stopwatch.StartNew();
        lock (_gate)
        {
            foreach (var entry in _events)
            {
                if (watch.ElapsedMilliseconds >= 50) return (result, true);
                if (!entry.Target.TryGetTarget(out var target)) continue;
                if (!byTarget.TryGetValue(target, out var members)) continue;
                foreach (var member in members)
                {
                    if (!Matches(entry, member)) continue;
                    result.Add(entry.Evidence);
                    break;
                }
                if (result.Count == 128) return (result, true);
            }
            return (result, _dropped);
        }
    }

    private static bool Matches(Entry entry, object member)
    {
        if (entry.Member?.TryGetTarget(out var original) == true && ReferenceEquals(original, member)) return true;
        // Reflection may return a fresh PropertyInfo. Target identity was already
        // established; compare only its known framework member metadata.
        return member is PropertyInfo property && entry.MemberName == property.Name &&
            entry.MemberOwner == (property.DeclaringType is { } owner ? AppearanceText.TypeName(owner) : null);
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _events.Clear(); }
        ResourceDictionaryDiagnostics.StaticResourceResolved -= Resolved;
    }
}
