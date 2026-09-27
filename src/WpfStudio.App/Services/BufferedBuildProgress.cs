using WpfStudio.Contracts;

namespace WpfStudio.App.Services;

/// <summary>One dispatcher callback at a time; noisy tools cannot queue unlimited UI work.</summary>
public sealed class BufferedBuildProgress(IUiDispatcher dispatcher, Action<IReadOnlyList<BuildOutputEvent>> consume) : IProgress<BuildOutputEvent>, IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<BuildOutputEvent> _pending = new();
    private int _characters;
    private bool _scheduled;
    private bool _truncated;
    private bool _disposed;
    public void Report(BuildOutputEvent value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (value.Text.Length > 65_536) value = value with { Text = value.Text[^65_536..] };
            _pending.Enqueue(value); _characters += value.Text.Length;
            while (_characters > 131_072 || _pending.Count > 1000) { _characters -= _pending.Dequeue().Text.Length; _truncated = true; }
            if (_scheduled) return;
            _scheduled = true;
        }
        dispatcher.Post(Flush);
    }
    public void Flush()
    {
        List<BuildOutputEvent> batch;
        lock (_gate)
        {
            batch = new(_pending.Count + 1);
            if (_truncated) batch.Add(new("[Output truncated while the tool was producing data faster than it could be displayed.]"));
            batch.AddRange(_pending); _pending.Clear(); _characters = 0; _scheduled = false; _truncated = false;
        }
        if (batch.Count > 0) consume(batch);
    }
    public void Dispose() { lock (_gate) _disposed = true; Flush(); }
}
