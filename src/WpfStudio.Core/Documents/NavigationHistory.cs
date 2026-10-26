namespace WpfStudio.Core.Documents;

/// <summary>A caret position in a source file. Line is 1-based and only used to merge nearby positions.</summary>
public sealed record NavigationLocation(string Path, int Offset, int Line);

/// <summary>
/// Back/forward history for editor jumps, like an IDE's Ctrl+- / Ctrl+Shift+-. Only the origins of jumps are
/// recorded, so ordinary typing and caret movement within a screen do not grow the history.
/// </summary>
public sealed class NavigationHistory(int capacity = 100)
{
    /// <summary>Jumps within this many lines of the previous entry in the same file replace it.</summary>
    public const int NearbyLines = 10;
    private readonly List<NavigationLocation> _back = [];
    private readonly Stack<NavigationLocation> _forward = new();

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public event Action? Changed;

    /// <summary>Records where a jump started. A new jump discards the forward history.</summary>
    public void Record(NavigationLocation origin)
    {
        _forward.Clear();
        if (_back.Count > 0 && IsNear(_back[^1], origin)) _back[^1] = origin;
        else _back.Add(origin);
        if (_back.Count > capacity) _back.RemoveAt(0);
        Changed?.Invoke();
    }

    /// <summary>Returns the previous location, remembering <paramref name="current"/> for <see cref="GoForward"/>.</summary>
    public NavigationLocation? GoBack(NavigationLocation? current)
    {
        if (_back.Count == 0) return null;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        // Returning from a location that was never left (for example Back twice) needs no forward entry.
        if (current is not null && !IsNear(current, target)) _forward.Push(current);
        Changed?.Invoke();
        return target;
    }

    public NavigationLocation? GoForward(NavigationLocation? current)
    {
        if (_forward.Count == 0) return null;
        var target = _forward.Pop();
        if (current is not null && (_back.Count == 0 || !IsNear(_back[^1], current))) _back.Add(current);
        Changed?.Invoke();
        return target;
    }

    /// <summary>Drops locations in a file that no longer exists or was replaced by an unrelated workspace.</summary>
    public void Clear()
    {
        _back.Clear(); _forward.Clear();
        Changed?.Invoke();
    }

    private static bool IsNear(NavigationLocation first, NavigationLocation second) =>
        first.Path.Equals(second.Path, StringComparison.OrdinalIgnoreCase) && Math.Abs(first.Line - second.Line) <= NearbyLines;
}
