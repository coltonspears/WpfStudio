using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.App.Services;
using WpfStudio.Workspace;

namespace WpfStudio.App.ViewModels;

public enum EditorAction { Definition, Implementation, References, Rename, Format, OrganizeUsings, UseVar, UseExplicitType, ToggleBreakpoint, BreakpointCondition, BreakpointHitCount, BreakpointLogMessage, AddWatch, DisableBreakpoint, ShowBreakpoints, SwitchRelated, InsertProperty, InsertCommand, InsertSnippet, AskColtonGpt, OpenDesigner }
public sealed record BreakpointMarker(int Line, bool Enabled, bool Bound, string Condition, string Status, string HitCondition = "", string LogMessage = "")
{
    public bool IsTracepoint => LogMessage.Length > 0;
}
public sealed record EditorSelection(int CaretOffset, int Start, int Length);
public sealed record XamlQuickFix(XamlCodeAction Action, IAsyncRelayCommand<XamlCodeAction?> ApplyCommand)
{
    public string Title => Action.Title;
}
/// <summary>A Roslyn code fix or refactoring offered by Ctrl+. in a C# editor.</summary>
public sealed record CSharpQuickAction(CodeActionEntry Action, IAsyncRelayCommand<CodeActionEntry?> ApplyCommand)
{
    public string Title => Action.Title;
    public bool IsFix => Action.Kind == "Fix";
}

public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceClient _workspace;
    private readonly XamlCompletionService _xaml;
    private readonly IUiDispatcher _dispatcher;
    private readonly XamlResourceContext? _resources;
    private CancellationTokenSource? _analysis;
    private CancellationTokenSource? _quickFixRequest;
    private long _quickFixRevision;
    private long _quickFixResourceGeneration;
    private long _quickFixContextRevision;
    private long _selectionRevision;
    private long _semanticRevision;
    private bool _disposed;
    private readonly Action<string> _report;
    public EditorViewModel(DocumentState state, WorkspaceClient workspace, XamlCompletionService xaml, IUiDispatcher dispatcher, Action<string> report,
        XamlResourceContext? resources = null)
    {
        State = state; _workspace = workspace; _xaml = xaml; _dispatcher = dispatcher; _report = report; _resources = resources;
        ActionCommand = new RelayCommand<EditorAction>(action => ActionRequested?.Invoke(action));
        OpenRelatedCommand = new RelayCommand<RelatedFile>(file => { if (file != null) OpenRequested?.Invoke(file.Path); });
        state.ContentChanged += ContentChanged;
        state.PropertyChanged += StatePropertyChanged;
        if (State.Extension == ".xaml")
        {
            _workspace.SemanticStateChanged += SemanticStateChanged;
            if (_resources is not null) _resources.Changed += SemanticStateChanged;
        }
        // XAML synchronization can change generated fields while this C# buffer
        // stays unchanged. Keep this separate from the XAML semantic event:
        // notifying the authoring XAML editor would cancel its own analysis.
        else if (State.Extension == ".cs")
        {
            _workspace.NameProjectionChanged += SemanticStateChanged;
            _workspace.CSharpModelChanged += CSharpModelChanged;
        }
        ContentChanged(this, EventArgs.Empty);
    }
    public DocumentState State { get; }
    public string Title => State.Title;
    public string ContentId => State.Path;
    public bool IsReadOnly => State.Path.Contains(System.IO.Path.Combine("WpfStudio", "GeneratedSources"), StringComparison.OrdinalIgnoreCase);
    public ObservableCollection<WorkspaceDiagnostic> Diagnostics { get; } = [];
    public ObservableCollection<XamlQuickFix> QuickFixes { get; } = [];
    public event Func<XamlCodeAction, Task>? CodeActionRequested;
    public ObservableCollection<CSharpQuickAction> CodeActions { get; } = [];
    /// <summary>The shell computes the action's edits and opens the change review.</summary>
    public event Func<CodeActionEntry, Task>? CSharpCodeActionRequested;
    private long _codeActionsVersion = -1;
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
    public bool IsXaml => State.Extension == ".xaml";
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
    public void UpdateSelection(int start, int length)
    {
        if (SelectionStart != start || SelectionLength != length)
        {
            Interlocked.Increment(ref _selectionRevision);
            ClearQuickFixes();
        }
        SelectionStart = start; SelectionLength = length; ContextChanged?.Invoke();
    }
    internal long QuickFixContextRevision => Volatile.Read(ref _quickFixContextRevision);
    internal long SelectionRevision => Volatile.Read(ref _selectionRevision);
    public event Action<int>? BreakpointRequested;
    public event Action? NavigationRequested;
    public event Action<EditorSelection>? SelectionRequested;
    [ObservableProperty] public partial int ExecutionLine { get; set; } = -1;
    /// <summary>Semantic colouring and inline hints for <see cref="DecorationsVersion"/>; the editor maps them through later edits.</summary>
    public IReadOnlyList<SemanticSpan> SemanticSpans { get; private set; } = [];
    public IReadOnlyList<InlayHint> InlayHints { get; private set; } = [];
    public long DecorationsVersion { get; private set; } = -1;
    public event Action? DecorationsChanged;
    [ObservableProperty] public partial bool ShowInlayHints { get; set; } = true;
    /// <summary>Asks the editor to collapse (true) or expand (false) every folding.</summary>
    public event Action<bool>? FoldingRequested;
    public void SetAllFolded(bool folded) => FoldingRequested?.Invoke(folded);
    [ObservableProperty] public partial string LanguageStatus { get; set; } = "";
    public void Navigate(int offset) { State.CaretOffset = Math.Clamp(offset, 0, State.Content.Length); NavigationRequested?.Invoke(); }
    public void RestoreSelection(int caret, int start, int length)
    {
        start = Math.Clamp(start, 0, State.Content.Length);
        length = Math.Clamp(length, 0, State.Content.Length - start);
        caret = Math.Clamp(caret, 0, State.Content.Length);
        State.CaretOffset = caret;
        UpdateSelection(start, length);
        SelectionRequested?.Invoke(new(caret, start, length));
    }
    public void ToggleBreakpoint(int line) => BreakpointRequested?.Invoke(line);
    /// <summary>Asks the attached editor to expand a snippet at the caret, wrapping any selection the snippet supports.</summary>
    public event Action<WpfStudio.Core.Text.CodeSnippet>? SnippetRequested;
    public void InsertSnippet(WpfStudio.Core.Text.CodeSnippet snippet) => SnippetRequested?.Invoke(snippet);
    private void ContentChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        ContextChanged?.Invoke();
        _ = StartAnalysisAsync(debounce: true);
    }
    private void StatePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DocumentState.Title)) OnPropertyChanged(nameof(Title));
        if (args.PropertyName == nameof(DocumentState.CaretOffset))
        {
            Interlocked.Increment(ref _selectionRevision);
            ClearQuickFixes();
            ContextChanged?.Invoke();
        }
    }
    private void CSharpModelChanged(object? sender, string path)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(System.IO.Path.GetFullPath(State.Path), path))
            SemanticStateChanged(sender, EventArgs.Empty);
    }
    private void SemanticStateChanged(object? sender, EventArgs e)
    {
        // Invalidate in-flight responses before the UI dispatcher processes the notification.
        Interlocked.Increment(ref _semanticRevision);
        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                if (IsCSharp)
                {
                    Diagnostics.Clear();
                    LanguageStatus = _workspace.IsConnected ? "" : "Language service unavailable";
                }
                OnPropertyChanged(nameof(XamlContextRevision));
                _ = StartAnalysisAsync(debounce: true);
            }
        });
    }
    public Task RefreshAnalysisAsync(CancellationToken token = default) => StartAnalysisAsync(debounce: false, token);
    private Task StartAnalysisAsync(bool debounce, CancellationToken token = default)
    {
        if (_disposed) return Task.CompletedTask;
        _analysis?.Cancel();
        _analysis?.Dispose();
        _analysis = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (State.Extension == ".xaml")
        {
            ClearQuickFixes();
            Diagnostics.Clear();
            LanguageStatus = _xamlContextUnavailable ?? (_workspace.IsConnected ? "" : "XAML binding analysis unavailable: open a workspace");
            if (_xamlContextUnavailable is not null) return Task.CompletedTask;
        }
        return AnalyzeAsync(State.Content, State.Version, Volatile.Read(ref _semanticRevision), CaptureResources(), debounce, _analysis.Token);
    }
    private XamlResourceSnapshot CaptureResources() => _resources?.Capture() ?? new(0, []);
    private bool IsCurrentAnalysis(long version, long semanticRevision, long resourceGeneration, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && version == State.Version && semanticRevision == Volatile.Read(ref _semanticRevision)
        && (!IsXaml || _resources?.IsCurrent(resourceGeneration) != false);
    private async Task AnalyzeAsync(string text, long version, long semanticRevision, XamlResourceSnapshot resources, bool debounce, CancellationToken token)
    {
        try
        {
            if (debounce) await Task.Delay(350, token);
            if (!_workspace.IsConnected || !IsCurrentAnalysis(version, semanticRevision, resources.Generation, token)) return;
            IReadOnlyList<WorkspaceDiagnostic> diagnostics;
            EditorDecorationsResult? decorations = null;
            string? status = null;
            if (State.Extension == ".xaml")
            {
                var synchronized = await _workspace.UpdateDocumentAsync(new(State.Path, text, version, Analyze: false), token);
                // Unowned XAML has no persistent worker buffer, but analysis
                // still explains its unavailable/standalone project context.
                if (synchronized.Version != version || !IsCurrentAnalysis(version, semanticRevision, resources.Generation, token)) return;
                var result = await _workspace.AnalyzeXamlAsync(new(State.Path, text, version, XamlProjectPath, resources.Overlays), token);
                if (result.Version != version) return;
                diagnostics = result.Accepted ? result.Diagnostics.Select(diagnostic => diagnostic with
                    { ProjectPath = XamlProjectPath, ProjectName = XamlProject?.Name }).ToArray() : [];
                status = result.Status;
            }
            else if (State.Extension == ".cs")
            {
                var result = await _workspace.UpdateDocumentAsync(new(State.Path, text, version), token);
                if (!result.Accepted || result.Version != version) return;
                diagnostics = result.Diagnostics;
                // Colouring and hints are optional and refresh with the next analysis; diagnostics apply regardless.
                try { decorations = await _workspace.GetEditorDecorationsAsync(new(State.Path, version), token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { decorations = null; }
            }
            else return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentAnalysis(version, semanticRevision, resources.Generation, token) || !_workspace.IsConnected) return;
                Diagnostics.Clear(); foreach (var item in diagnostics) Diagnostics.Add(item);
                LanguageStatus = status ?? ""; // Counts are shown by the status bar's error and warning summary.
                if (decorations is { } current && current.Version == version)
                {
                    SemanticSpans = current.Spans; InlayHints = current.Hints; DecorationsVersion = version;
                    DecorationsChanged?.Invoke();
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentAnalysis(version, semanticRevision, resources.Generation, token)) return;
                if (State.Extension == ".xaml") Diagnostics.Clear();
                LanguageStatus = "Language service unavailable";
                _report(ex.Message);
            });
        }
    }
    public async Task SyncAsync(CancellationToken token = default)
    {
        if ((IsCSharp || IsXaml) && _workspace.IsConnected && !IsReadOnly)
            await _workspace.UpdateDocumentAsync(new(State.Path, State.Content, State.Version, Analyze: false), token);
    }
    public async Task<CompletionResult> CompleteAsync(int offset, CancellationToken token = default)
    {
        if (State.Extension == ".xaml")
        {
            var version = State.Version;
            if (_xamlContextUnavailable is not null) return new(version, Math.Clamp(offset, 0, State.Content.Length), 0, []);
            var text = State.Content;
            var semanticRevision = Volatile.Read(ref _semanticRevision);
            var resources = CaptureResources();
            offset = Math.Clamp(offset, 0, text.Length);
            if (_workspace.IsConnected)
            {
                try
                {
                    var result = await _workspace.GetXamlCompletionsAsync(new(State.Path, text, offset, version, XamlProjectPath, resources.Overlays), token);
                    if (!IsCurrentAnalysis(version, semanticRevision, resources.Generation, token)) return new(version, offset, 0, []);
                    if (result.Available && result.Completion is { } completion && completion.Version == version) return completion;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _report(ex.Message); }
            }
            token.ThrowIfCancellationRequested();
            if (!IsCurrentAnalysis(version, semanticRevision, resources.Generation, token)) return new(version, offset, 0, []);
            return _xaml.Complete(State.Path, text, offset, version);
        }
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
    /// <summary>Evaluates an expression while the debugger is paused; set by the shell. Returns null when unavailable.</summary>
    public Func<string, CancellationToken, Task<string?>>? DataTipEvaluator { get; set; }
    /// <summary>The expression a DataTip or Add watch would use at <paramref name="offset"/>: the selection when it contains the offset, else the hovered member chain.</summary>
    public string? DebugExpressionAt(int offset)
    {
        if (!IsCSharp) return null;
        if (SelectionLength is > 0 and <= 200 && offset >= SelectionStart && offset <= SelectionStart + SelectionLength && !SelectedText.Contains('\n'))
            return SelectedText.Trim() is { Length: > 0 } selected ? selected : null;
        return WpfStudio.Core.Text.DebugExpressionLocator.Find(State.Content, offset)?.Expression;
    }
    public async Task<string?> DataTipAsync(int offset, CancellationToken token = default)
    {
        if (DataTipEvaluator is not { } evaluate || DebugExpressionAt(offset) is not { } expression) return null;
        return await evaluate(expression, token);
    }
    public async Task<XamlHoverInfo?> HoverAsync(int offset, CancellationToken token = default)
    {
        if (!IsXaml || _disposed || !_workspace.IsConnected || _xamlContextUnavailable is not null) return null;
        long version = State.Version, revision = Volatile.Read(ref _semanticRevision);
        var resources = CaptureResources();
        var request = new XamlCompletionRequest(State.Path, State.Content, Math.Clamp(offset, 0, State.Content.Length), version, XamlProjectPath, resources.Overlays);
        var result = await _workspace.GetXamlHoverAsync(request, token);
        return IsCurrentAnalysis(version, revision, resources.Generation, token) && _workspace.IsConnected ? result : null;
    }
    public async Task RefreshQuickFixesAsync(int offset, CancellationToken token = default)
    {
        ClearQuickFixes();
        if (IsCSharp) { await RefreshCSharpActionsAsync(offset, token); return; }
        if (!IsXaml || IsReadOnly || _disposed || !_workspace.IsConnected || _xamlContextUnavailable is not null) return;
        _quickFixRequest = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = _quickFixRequest.Token;
        long version = State.Version, revision = Volatile.Read(ref _semanticRevision);
        var resources = CaptureResources();
        int caret = State.CaretOffset;
        long selectionRevision = QuickFixContextRevision;
        try
        {
            var actions = await _workspace.GetXamlCodeActionsAsync(new(State.Path, State.Content, Math.Clamp(offset, 0, State.Content.Length), version, XamlProjectPath, resources.Overlays), token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentAnalysis(version, revision, resources.Generation, token) || !_workspace.IsConnected || caret != State.CaretOffset || selectionRevision != QuickFixContextRevision) return;
                _quickFixRevision = revision;
                _quickFixResourceGeneration = resources.Generation;
                foreach (var action in actions.Where(action => action.Edit.Version == version && action.Edit.Path.Equals(State.Path, StringComparison.OrdinalIgnoreCase)))
                    QuickFixes.Add(new(action, ApplyCodeActionCommand));
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentAnalysis(version, revision, resources.Generation, token)) LanguageStatus = "Quick fixes unavailable";
            _report(ex.Message);
        }
    }
    /// <summary>Lists Roslyn fixes and refactorings for the selection when it contains <paramref name="offset"/>, else for the caret.</summary>
    private async Task RefreshCSharpActionsAsync(int offset, CancellationToken token)
    {
        if (IsReadOnly || _disposed || !_workspace.IsConnected) return;
        _quickFixRequest = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = _quickFixRequest.Token;
        long contextRevision = QuickFixContextRevision;
        int start = Math.Clamp(offset, 0, State.Content.Length), length = 0;
        if (SelectionLength > 0 && offset >= SelectionStart && offset <= SelectionStart + SelectionLength) { start = SelectionStart; length = SelectionLength; }
        try
        {
            await SyncAsync(token);
            long version = State.Version;
            var result = await _workspace.GetCodeActionsAsync(new(State.Path, start, length, version), token);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || token.IsCancellationRequested || version != State.Version || result.Version != version || contextRevision != QuickFixContextRevision) return;
                _codeActionsVersion = version;
                foreach (var action in result.Actions) CodeActions.Add(new(action, ApplyCSharpCodeActionCommand));
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LanguageStatus = "Quick actions unavailable";
            _report(ex.Message);
        }
    }
    [RelayCommand] private async Task ApplyCSharpCodeActionAsync(CodeActionEntry? action)
    {
        if (action is null || _disposed || IsReadOnly || !_workspace.IsConnected || _codeActionsVersion != State.Version
            || !CodeActions.Any(item => ReferenceEquals(item.Action, action)))
        {
            ClearQuickFixes();
            LanguageStatus = "The document changed. Press Ctrl+. again.";
            return;
        }
        try { if (CSharpCodeActionRequested is { } apply) await apply(action); }
        catch (Exception ex) { LanguageStatus = ex.Message; _report(ex.Message); }
        finally { ClearQuickFixes(); }
    }
    [RelayCommand] private async Task ApplyCodeActionAsync(XamlCodeAction? action)
    {
        if (action is null || _disposed || IsReadOnly || !_workspace.IsConnected ||
            !action.Edit.Path.Equals(State.Path, StringComparison.OrdinalIgnoreCase) ||
            action.Edit.Version != State.Version || _quickFixRevision != Volatile.Read(ref _semanticRevision) ||
            _resources?.IsCurrent(_quickFixResourceGeneration) == false ||
            !QuickFixes.Any(item => ReferenceEquals(item.Action, action)))
        {
            ClearQuickFixes();
            LanguageStatus = "The document or its types changed. Request quick fixes again.";
            return;
        }
        try
        {
            if (CodeActionRequested is { } apply) await apply(action);
        }
        catch (Exception ex) { LanguageStatus = ex.Message; _report(ex.Message); }
        finally { ClearQuickFixes(); }
    }
    private void ClearQuickFixes()
    {
        Interlocked.Increment(ref _quickFixContextRevision);
        _quickFixRequest?.Cancel();
        _quickFixRequest?.Dispose();
        _quickFixRequest = null;
        QuickFixes.Clear();
        CodeActions.Clear();
        _codeActionsVersion = -1;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        State.ContentChanged -= ContentChanged;
        State.PropertyChanged -= StatePropertyChanged;
        _workspace.SemanticStateChanged -= SemanticStateChanged;
        _workspace.NameProjectionChanged -= SemanticStateChanged;
        _workspace.CSharpModelChanged -= CSharpModelChanged;
        if (_resources is not null) _resources.Changed -= SemanticStateChanged;
        ClearQuickFixes();
        _analysis?.Cancel();
        _analysis?.Dispose();
    }
}
