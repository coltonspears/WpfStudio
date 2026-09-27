using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.App.Services;
using WpfStudio.Workspace;

namespace WpfStudio.App.ViewModels;

public enum EditorAction { Definition, References, Rename, Format, OrganizeUsings, UseVar, UseExplicitType, ToggleBreakpoint, BreakpointCondition, DisableBreakpoint, ShowBreakpoints, SwitchRelated, InsertProperty, InsertCommand, AskColtonGpt }
public sealed record BreakpointMarker(int Line, bool Enabled, bool Bound, string Condition, string Status);

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
        ActionCommand = new RelayCommand<EditorAction>(action => ActionRequested?.Invoke(action));
        OpenRelatedCommand = new RelayCommand<RelatedFile>(file => { if (file != null) OpenRequested?.Invoke(file.Path); });
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
    public ObservableCollection<BreakpointMarker> BreakpointMarkers { get; } = [];
    public IRelayCommand<EditorAction> ActionCommand { get; }
    public event Action<EditorAction>? ActionRequested;
    /// <summary>Paired XAML, code-behind and view-model files shown in the editor context bar.</summary>
    public ObservableCollection<RelatedFile> RelatedFiles { get; } = [];
    public IRelayCommand<RelatedFile> OpenRelatedCommand { get; }
    public event Action<string>? OpenRequested;
    /// <summary>Folder breadcrumb relative to the workspace, e.g. "CounterApp › Views".</summary>
    [ObservableProperty] public partial string Location { get; set; } = "";
    public string FileName => State.Name;
    public bool IsViewModel => State.Name.EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase);
    public bool IsCSharp => State.Extension == ".cs";
    public string IconKind => WpfStudio.App.Controls.FileIconKindConverter.KindFor(State.Path);
    /// <summary>Human-readable document kind for the context bar and status bar.</summary>
    public string KindLabel => State.Name switch
    {
        var name when name.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) => "Code-behind",
        var name when name.EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase) => "View model",
        _ => State.Extension switch
        {
            ".cs" => "C#", ".xaml" => "XAML", ".csproj" => "Project file", ".sql" => "SQL", ".json" => "JSON",
            ".xml" or ".props" or ".targets" or ".resx" => "XML", ".md" => "Markdown", _ => "Plain text"
        }
    };
    public string LanguageLabel => State.Extension switch
    {
        ".cs" => "C#", ".xaml" => "XAML", ".sql" => "SQL", ".json" => "JSON",
        ".xml" or ".csproj" or ".props" or ".targets" or ".resx" => "XML", ".md" => "Markdown", _ => "Plain text"
    };
    public string EncodingLabel => State.Encoding.WebName.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ? (State.Encoding.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8") : State.Encoding.WebName.ToUpperInvariant();
    public event Action? ContextChanged;
    public int SelectionStart { get; private set; }
    public int SelectionLength { get; private set; }
    public string SelectedText => State.Content.Substring(Math.Clamp(SelectionStart, 0, State.Content.Length), Math.Clamp(SelectionLength, 0, State.Content.Length - Math.Clamp(SelectionStart, 0, State.Content.Length)));
    public void UpdateSelection(int start, int length) { SelectionStart = start; SelectionLength = length; ContextChanged?.Invoke(); }
    public event Action<int>? BreakpointRequested;
    public event Action? NavigationRequested;
    [ObservableProperty] public partial int ExecutionLine { get; set; } = -1;
    [ObservableProperty] public partial string LanguageStatus { get; set; } = "";
    public void Navigate(int offset) { State.CaretOffset = Math.Clamp(offset, 0, State.Content.Length); NavigationRequested?.Invoke(); }
    public void ToggleBreakpoint(int line) => BreakpointRequested?.Invoke(line);
    private void ContentChanged(object? sender, EventArgs e)
    {
        ContextChanged?.Invoke();
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
