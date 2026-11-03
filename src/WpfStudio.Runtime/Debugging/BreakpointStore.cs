using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WpfStudio.Runtime.Debugging;

/// <summary>A workspace's breakpoints and watch expressions, and the file that keeps them between sessions.</summary>
/// <param name="directory">Where the workspace files go; the user's profile unless a test says otherwise.</param>
public sealed class BreakpointStore(string? directory = null)
{
    private readonly string directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfStudio", "debugging");
    private string? statePath;
    public ObservableCollection<BreakpointViewModel> Breakpoints { get; } = [];
    public List<string> WatchExpressions { get; } = [];
    public IEnumerable<string> Files => Breakpoints.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase);
    public SourceBreakpoint[] ToModels() => Breakpoints.Select(x => x.ToModel()).ToArray();
    public BreakpointViewModel[] EnabledIn(string path) => Breakpoints.Where(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase) && x.Enabled).ToArray();

    public void Toggle(string path, int line)
    {
        var existing = Breakpoints.FirstOrDefault(x => x.Line == line && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) Breakpoints.Add(new BreakpointViewModel { Path = path, Line = line }); else Breakpoints.Remove(existing);
    }

    /// <summary>The breakpoint a stop belongs to: the adapter's ids when it reports them, or else the source line it bound to.</summary>
    public BreakpointViewModel? FindHit(IReadOnlySet<int> ids, string path, int line) =>
        Breakpoints.FirstOrDefault(b => b.Enabled && b.AdapterId is { } id && ids.Contains(id))
        ?? Breakpoints.FirstOrDefault(b => b.Enabled && (b.BoundLine ?? b.Line) == line && string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<BreakpointViewModel> Matching(DapBreakpoint changed) =>
        Breakpoints.Where(x => changed.Id is not null && x.AdapterId == changed.Id || x.Line == changed.Line && string.Equals(x.Path, changed.SourcePath, StringComparison.OrdinalIgnoreCase));

    public void ResetForSession() { foreach (var breakpoint in Breakpoints) { breakpoint.HitCount = 0; breakpoint.AdapterId = null; breakpoint.BoundLine = null; } }
    public void MarkPending() { foreach (var breakpoint in Breakpoints) breakpoint.Status = "Pending"; }

    /// <summary>Replaces the contents with the workspace's saved state. Throws <see cref="IOException"/> or <see cref="JsonException"/> when that cannot be read, leaving them empty.</summary>
    public async Task LoadAsync(string workspacePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspacePath).ToUpperInvariant())))[..20];
        statePath = Path.Combine(directory, hash + ".json");
        Breakpoints.Clear(); WatchExpressions.Clear();
        if (!File.Exists(statePath)) return;
        var state = JsonSerializer.Deserialize<DebugWorkspaceState>(await File.ReadAllTextAsync(statePath));
        if (state is null) return;
        foreach (var item in state.Breakpoints) Breakpoints.Add(new BreakpointViewModel { Path = item.Path, Line = item.Line, Condition = item.Condition ?? "", Enabled = item.Enabled, HitCondition = item.HitCondition ?? "", LogMessage = item.LogMessage ?? "" });
        WatchExpressions.AddRange(state.Watches);
    }

    public async Task SaveAsync()
    {
        if (statePath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        // A unique temporary name lets overlapping saves (rapid breakpoint toggles) each complete; the last move wins.
        var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new DebugWorkspaceState(ToModels(), WatchExpressions.ToArray())));
            File.Move(temporary, statePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record DebugWorkspaceState(SourceBreakpoint[] Breakpoints, string[] Watches);
}
