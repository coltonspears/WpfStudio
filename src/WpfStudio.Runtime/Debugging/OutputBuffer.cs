using System.Text;
using WpfStudio.Contracts;

namespace WpfStudio.Runtime.Debugging;

/// <summary>Takes debug output from any thread and publishes the text on the UI thread. One bounded batch per interval keeps high-volume output off the dispatcher queue.</summary>
public sealed class OutputBuffer(IUiDispatcher dispatcher, Action<string> publish)
{
    private const int Limit = 250_000;
    private const int KeptAfterTrim = 200_000;
    private readonly object gate = new();
    private readonly StringBuilder pending = new();
    private string text = "";
    private bool scheduled;
    private bool closed;

    public void Append(string value)
    {
        lock (gate)
        {
            if (closed) return;
            if (value.Length >= Limit) { pending.Clear(); pending.Append(value.AsSpan(value.Length - Limit)); }
            else
            {
                int excess = pending.Length + value.Length - Limit;
                if (excess > 0) pending.Remove(0, excess);
                pending.Append(value);
            }
            if (scheduled) return;
            scheduled = true;
        }
        ScheduleAsync().Forget();
    }

    /// <summary>Call on the UI thread.</summary>
    public void Clear()
    {
        lock (gate) pending.Clear();
        text = "";
        publish(text);
    }

    public void Close() { lock (gate) { closed = true; pending.Clear(); } }

    private async Task ScheduleAsync()
    {
        await Task.Delay(50).ConfigureAwait(false);
        lock (gate) { if (closed) return; }
        dispatcher.Post(() =>
        {
            string batch;
            lock (gate)
            {
                scheduled = false;
                if (closed) return;
                batch = pending.ToString(); pending.Clear();
            }
            if (batch.Length == 0) return;
            var next = text + batch;
            text = next.Length > Limit ? next[^KeptAfterTrim..] : next;
            publish(text);
        });
    }
}
