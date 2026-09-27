using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.App.Services;
using WpfStudio.Workspace;

namespace WpfStudio.App.ViewModels;

public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceClient _workspace;
    private readonly XamlCompletionService _xaml;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _analysis;
    private readonly Action<string> _report;
    public EditorViewModel(DocumentState state, WorkspaceClient workspace, XamlCompletionService xaml, IUiDispatcher dispatcher, Action<string> report)
    {
        State = state; _workspace = workspace; _xaml = xaml; _dispatcher = dispatcher; _report = report;
        state.ContentChanged += ContentChanged;
        state.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(DocumentState.Title)) OnPropertyChanged(nameof(Title)); };
        ContentChanged(this, EventArgs.Empty);
    }
    public DocumentState State { get; }
    public string Title => State.Title;
    public string ContentId => State.Path;
    public bool IsReadOnly => State.Path.Contains(System.IO.Path.Combine("WpfStudio", "GeneratedSources"), StringComparison.OrdinalIgnoreCase);
    public ObservableCollection<WorkspaceDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<int> BreakpointLines { get; } = [];
    public event Action<int>? BreakpointRequested;
    public event Action? NavigationRequested;
    [ObservableProperty] public partial int ExecutionLine { get; set; } = -1;
    [ObservableProperty] public partial string LanguageStatus { get; set; } = "";
    public void Navigate(int offset) { State.CaretOffset = Math.Clamp(offset, 0, State.Content.Length); NavigationRequested?.Invoke(); }
    public void ToggleBreakpoint(int line) => BreakpointRequested?.Invoke(line);
    private void ContentChanged(object? sender, EventArgs e)
    {
        _analysis?.Cancel(); _analysis?.Dispose(); _analysis = new();
        _ = AnalyzeAsync(_analysis.Token);
    }
    private async Task AnalyzeAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(350, token);
            if (State.Extension != ".cs" || !_workspace.IsConnected) return;
            var version = State.Version;
            var result = await _workspace.UpdateDocumentAsync(new(State.Path, State.Content, version), token);
            if (version != State.Version || token.IsCancellationRequested || !result.Accepted) return;
            Diagnostics.Clear(); foreach (var item in result.Diagnostics) Diagnostics.Add(item);
            LanguageStatus = $"{Diagnostics.Count} diagnostic(s)";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LanguageStatus = "Language service unavailable"; _report(ex.Message); }
    }
    public async Task SyncAsync(CancellationToken token = default)
    {
        if (State.Extension == ".cs" && _workspace.IsConnected && !IsReadOnly) await _workspace.UpdateDocumentAsync(new(State.Path, State.Content, State.Version, Analyze: false), token);
    }
    public async Task<CompletionResult> CompleteAsync(int offset, CancellationToken token = default)
    {
        if (State.Extension == ".xaml") return _xaml.Complete(State.Path, State.Content, offset, State.Version);
        if (State.Extension != ".cs" || !_workspace.IsConnected) return new(State.Version, offset, 0, []);
        await SyncAsync(token);
        return await _workspace.GetCompletionsAsync(new(State.Path, offset, State.Version), token);
    }
    public async Task<TextEdit?> CompletionEditAsync(CompletionEntry entry, long version, CancellationToken token = default)
    {
        if (State.Extension != ".cs") return null;
        return await _workspace.GetCompletionEditAsync(new(State.Path, version, entry.Id), token);
    }
    public async Task<string?> SignatureAsync(int offset, CancellationToken token = default)
    {
        if (State.Extension != ".cs" || !_workspace.IsConnected) return null;
        await SyncAsync(token);
        var result = await _workspace.GetSignatureHelpAsync(new(State.Path, offset, State.Version), token);
        return result.Version == State.Version ? string.Join(Environment.NewLine, result.Signatures.Take(6).Select(s => s.Label)) : null;
    }
    public void Dispose() { State.ContentChanged -= ContentChanged; _analysis?.Cancel(); _analysis?.Dispose(); }
}
