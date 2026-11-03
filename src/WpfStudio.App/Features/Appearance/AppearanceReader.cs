using WpfStudio.Inspection.Protocol;

namespace WpfStudio.App.Features.Appearance;

/// <summary>
/// One appearance read at a time: a new read or a clear supersedes the last, and an answer applies only while the
/// owner's guard holds. What makes a read stale, and which token its transport may safely be cancelled with, is the owner's.
/// </summary>
public sealed class AppearanceReader(Action changed)
{
    private CancellationTokenSource? _read;
    private long _sequence;
    public AppearanceInspectorViewModel Details { get; } = new();

    public void Clear(string status)
    {
        ++_sequence;
        _read?.Cancel(); _read?.Dispose(); _read = null;
        Details.Clear(status);
        changed();
    }

    /// <param name="scope">Ends this read with the owner's session or lifetime; a new read or a clear ends it too.</param>
    /// <param name="current">Whether the selection and session that asked are still the ones on screen.</param>
    /// <param name="read">Asks the host; it gets the token that ends with this read.</param>
    public async Task ReadAsync(AppearanceRequest request, string valueSource, bool overridden, CancellationToken scope, Func<bool> current,
        Func<AppearanceRequest, CancellationToken, Task<AppearanceResponse>> read)
    {
        Clear("Reading appearance…");
        var sequence = _sequence;
        _read = CancellationTokenSource.CreateLinkedTokenSource(scope);
        var token = _read.Token;
        Details.Begin(valueSource, overridden);
        bool Current() => !token.IsCancellationRequested && sequence == _sequence && current();
        try
        {
            var response = await read(request, token);
            if (!Current()) return;
            if (response.Request != request) { Details.Clear("Appearance response did not match the selected property. Refresh to try again."); return; }
            Details.Apply(response.Snapshot);
        }
        catch (OperationCanceledException) { if (Current()) Details.Clear("Appearance request cancelled. Refresh to try again."); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (Current()) Details.Clear("Appearance unavailable: " + exception.Message); }
    }
}
