using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

/// <summary>Records what would have reached the desktop shell and the clipboard.</summary>
internal sealed class FakeDesktop : IDesktopService, IClipboardService
{
    public List<string> Revealed { get; } = [];
    public List<string> Opened { get; } = [];
    public List<string> Copied { get; } = [];
    public bool ClipboardBusy { get; set; }
    public void Reveal(string path) => Revealed.Add(path);
    public void OpenUrl(string url) => Opened.Add(url);
    public bool TrySetText(string text)
    {
        if (ClipboardBusy) return false;
        Copied.Add(text);
        return true;
    }
}
