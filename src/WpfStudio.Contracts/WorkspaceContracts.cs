namespace WpfStudio.Contracts;

public sealed record LoadWorkspaceRequest(string Path, string Configuration = "Debug", string? TargetFramework = null);
public sealed record WorkspaceSnapshot(string Path, string SdkVersion, IReadOnlyList<WorkspaceProject> Projects, IReadOnlyList<WorkspaceIssue> Issues, IReadOnlyList<string>? Configurations = null);
public sealed record WorkspaceProject(string Id, string Name, string ProjectPath, string? TargetFramework, string? OutputPath, bool IsExecutable, IReadOnlyList<WorkspaceFile> Files, string? AssemblyName = null, string? ProjectAssetsPath = null);
public sealed record WorkspaceFile(string Path, string Name, string Kind, bool IsGenerated = false, string? DocumentId = null, string? LogicalPath = null);
public sealed record WorkspaceIssue(string Message, string Severity = "Warning");
public sealed record UpdateDocumentRequest(string Path, string Text, long Version, bool Analyze = true);
public sealed record DocumentUpdateResult(bool Accepted, long Version, IReadOnlyList<WorkspaceDiagnostic> Diagnostics);
// XAML remains a source buffer, not a C# Roslyn document. These requests keep the
// language engine independent of its editor and transport.
public sealed record XamlDocumentRequest(string Path, string Text, long Version, string? ProjectPath = null,
    IReadOnlyList<XamlDocumentOverlay>? XamlOverlays = null);
public sealed record XamlCompletionRequest(string Path, string Text, int Position, long Version, string? ProjectPath = null,
    IReadOnlyList<XamlDocumentOverlay>? XamlOverlays = null);
public sealed record XamlAnalysisResult(long Version, bool Accepted, IReadOnlyList<WorkspaceDiagnostic> Diagnostics, string? Status = null);
public sealed record XamlCompletionResult(bool Available, CompletionResult? Completion, string? Status = null);
public sealed record XamlHoverInfo(int Start, int Length, string Text);
public sealed record XamlCodeAction(string Title, DocumentEdits Edit, IReadOnlyList<DocumentEdits>? AdditionalEdits = null);
public sealed record DocumentPositionRequest(string Path, int Position, long Version);
public sealed record DocumentRequest(string Path, long Version);
public sealed record WorkspaceDiagnostic(string Id, string Message, string Severity, string? Path, int Line, int Column, int Start, int Length,
    string? ProjectPath = null, string? ProjectName = null);
public sealed record WorkspaceDiagnosticEvent(string Path, long Version, IReadOnlyList<WorkspaceDiagnostic> Diagnostics);
public sealed record CompletionEntry(string Id, string DisplayText, string InsertText, string? Description, IReadOnlyList<string> Tags);
public sealed record CompletionResult(long Version, int Start, int Length, IReadOnlyList<CompletionEntry> Items);
public sealed record CompletionEditRequest(string Path, long Version, string ItemId);
public sealed record TextEdit(int Start, int Length, string NewText);
/// <summary>Edits to one file. <paramref name="Create"/> marks a new file whose edits insert its whole text.</summary>
public sealed record DocumentEdits(string Path, long Version, IReadOnlyList<TextEdit> Edits, string? ExpectedTextHash = null, bool Create = false);
public sealed record SignatureParameter(string Name, string Type, string? Documentation);
public sealed record SignatureEntry(string Label, string? Documentation, IReadOnlyList<SignatureParameter> Parameters);
public sealed record SignatureHelpResult(long Version, int ActiveParameter, IReadOnlyList<SignatureEntry> Signatures);
public sealed record SourceLocation(string Path, int Start, int Length, int Line, int Column, string? DisplayText = null,
    string? ExpectedTextHash = null, string? ProjectPath = null, string? ProjectName = null);
public sealed record SymbolReferenceRequest(string Path, int Position, long Version, string? Text = null,
    string? ProjectPath = null, IReadOnlyList<XamlDocumentOverlay>? XamlOverlays = null);
public sealed record SymbolReferenceResult(IReadOnlyList<SourceLocation> Locations, IReadOnlyList<string> Warnings, bool SymbolFound = true);
public sealed record RenameRequest(string Path, int Position, long Version, string NewName, string? Text = null,
    string? ProjectPath = null, IReadOnlyList<XamlDocumentOverlay>? XamlOverlays = null);
public sealed record RefactorRequest(string Path, int Position, long Version, string Action);
/// <summary>A solution-wide type and member search. The query may be qualified, for example <c>Customer.Save</c>.</summary>
public sealed record SymbolSearchRequest(string Query, int Limit = 100);
public sealed record SymbolSearchResult(string Name, string Kind, string? Container, SourceLocation Location);
/// <summary>Code fixes and refactorings for a caret (<paramref name="Length"/> 0) or selection.</summary>
public sealed record CodeActionsRequest(string Path, int Start, int Length, long Version);
/// <summary>An offered action. <paramref name="Kind"/> is "Fix" or "Refactor"; the identifier is valid until the document changes.</summary>
public sealed record CodeActionEntry(string Id, string Title, string Kind);
public sealed record CodeActionsResult(long Version, IReadOnlyList<CodeActionEntry> Actions);
public sealed record ApplyCodeActionRequest(string Path, long Version, string Id);
/// <summary>A semantically classified range. Kind is Type, Interface, Enum, TypeParameter, Method, Property, Field, Event, Constant, EnumMember, Parameter, Local or Namespace.</summary>
public sealed record SemanticSpan(int Start, int Length, string Kind);
/// <summary>Text shown inline before <paramref name="Position"/> without being part of the document. Kind is Parameter or Type.</summary>
public sealed record InlayHint(int Position, string Text, string Kind);
/// <summary>
/// Reference counts above a declaration. <paramref name="XamlReferences"/> counts verified XAML uses (bindings,
/// event handlers); <paramref name="XamlName"/> names the generated member XAML uses, such as SaveCommand.
/// </summary>
public sealed record CodeLensEntry(int Line, int Position, string Name, int References, int XamlReferences, string? XamlName = null);
public sealed record CodeLensResult(long Version, IReadOnlyList<CodeLensEntry> Entries, bool Complete, string? Status = null);
public sealed record EditorDecorationsResult(long Version, IReadOnlyList<SemanticSpan> Spans, IReadOnlyList<InlayHint> Hints);
public sealed record WorkspaceEditResult(IReadOnlyList<DocumentEdits> Documents, IReadOnlyList<string> Warnings,
    XamlNameProjectionPlan? NameProjection = null);

/// <summary>All offsets are zero-based UTF-16; source lines and columns are one-based.</summary>
public interface IWorkspaceRpc
{
    /// <summary>An operation refreshed closed source files internally; cached semantic results are stale.</summary>
    event EventHandler? SemanticStateChanged;
    Task<WorkspaceSnapshot> LoadAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken);
    Task<DocumentUpdateResult> UpdateDocumentAsync(UpdateDocumentRequest request, CancellationToken cancellationToken);
    Task<XamlNameProjectionResult> ApplyXamlNameProjectionAsync(XamlNameProjectionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new XamlNameProjectionResult(false, "This language worker does not support live XAML name projection."));
    Task<XamlAnalysisResult> AnalyzeXamlAsync(XamlDocumentRequest request, CancellationToken cancellationToken);
    Task<XamlProjectAnalysisResult> AnalyzeXamlProjectAsync(XamlProjectAnalysisRequest request, CancellationToken cancellationToken);
    Task<RefreshDiskDocumentsResult> RefreshDiskDocumentsAsync(RefreshDiskDocumentsRequest request, CancellationToken cancellationToken);
    Task<XamlCompletionResult> GetXamlCompletionsAsync(XamlCompletionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceLocation>> GetXamlDefinitionAsync(XamlCompletionRequest request, CancellationToken cancellationToken);
    Task<XamlHoverInfo?> GetXamlHoverAsync(XamlCompletionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<XamlCodeAction>> GetXamlCodeActionsAsync(XamlCompletionRequest request, CancellationToken cancellationToken);
    Task CloseDocumentAsync(string path, CancellationToken cancellationToken);
    Task<CompletionResult> GetCompletionsAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    Task<TextEdit?> GetCompletionEditAsync(CompletionEditRequest request, CancellationToken cancellationToken);
    Task<SignatureHelpResult> GetSignatureHelpAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceLocation>> GetDefinitionAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceLocation>> FindReferencesAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    /// <summary>Implementations of an interface or abstract member, overrides of a virtual member, or types derived from the type at the position.</summary>
    Task<IReadOnlyList<SourceLocation>> FindImplementationsAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(SymbolSearchRequest request, CancellationToken cancellationToken);
    Task<SymbolReferenceResult> FindSymbolReferencesAsync(SymbolReferenceRequest request, CancellationToken cancellationToken);
    Task<WorkspaceEditResult> FormatDocumentAsync(DocumentRequest request, CancellationToken cancellationToken);
    Task<WorkspaceEditResult> FormatXamlAsync(XamlFormattingRequest request, CancellationToken cancellationToken);
    Task<WorkspaceEditResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken);
    Task<WorkspaceEditResult> RefactorAsync(RefactorRequest request, CancellationToken cancellationToken);
    /// <summary>Semantic colouring and inline parameter-name and type hints for a synchronized C# document.</summary>
    Task<EditorDecorationsResult> GetEditorDecorationsAsync(DocumentRequest request, CancellationToken cancellationToken);
    /// <summary>C# and XAML reference counts for the declarations in a synchronized C# document.</summary>
    Task<CodeLensResult> GetCodeLensAsync(DocumentRequest request, CancellationToken cancellationToken);
    /// <summary>Roslyn's C# code fixes for diagnostics at the span and refactorings available there.</summary>
    Task<CodeActionsResult> GetCodeActionsAsync(CodeActionsRequest request, CancellationToken cancellationToken);
    /// <summary>Computes, but does not apply, the edits of an action returned by <see cref="GetCodeActionsAsync"/>.</summary>
    Task<WorkspaceEditResult> ApplyCodeActionAsync(ApplyCodeActionRequest request, CancellationToken cancellationToken);
}

public enum BuildOperation { Restore, Build, Rebuild, Clean, Run, Test }
public sealed record BuildRequest(string Path, BuildOperation Operation = BuildOperation.Build, string Configuration = "Debug", string? TargetFramework = null, string? LaunchProfile = null, string? Arguments = null, bool XamlDebuggingInformation = false);
public sealed record BuildOutputEvent(string Text, bool IsError = false, WorkspaceDiagnostic? Diagnostic = null);
public sealed record BuildResult(int ExitCode, bool Cancelled, IReadOnlyList<WorkspaceDiagnostic> Diagnostics);
public sealed record LaunchProfile(string Name, string? CommandName, string? CommandLineArgs, string? WorkingDirectory, IReadOnlyDictionary<string, string> EnvironmentVariables, string? ExecutablePath = null);
public sealed record LaunchTarget(string ProjectPath, string Name, IReadOnlyList<string> TargetFrameworks, IReadOnlyList<LaunchProfile> Profiles);
public sealed record ResolvedLaunch(string Program, string WorkingDirectory, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);
