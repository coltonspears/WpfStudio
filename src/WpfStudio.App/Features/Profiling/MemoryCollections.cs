using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Runs a refresh at once, then at most once per interval while requests keep arriving (typing in a filter box),
/// always finishing with a refresh for the latest request. Continues on the caller's synchronization context.</summary>
internal sealed class RefreshThrottle(Action refresh, int intervalMilliseconds = 200)
{
    private long _last = long.MinValue / 2;
    private bool _pending;

    public void Request()
    {
        if (_pending) return;
        var wait = _last + intervalMilliseconds - Environment.TickCount64;
        if (wait <= 0) { Run(); return; }
        _pending = true;
        RunLaterAsync((int)wait).Forget();
    }

    private async Task RunLaterAsync(int wait)
    {
        await Task.Delay(wait);
        _pending = false;
        Run();
    }

    private void Run() { _last = Environment.TickCount64; refresh(); }
}
