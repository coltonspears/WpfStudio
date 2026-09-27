namespace WpfStudio.Contracts;

public sealed record LoadWorkspaceRequest(string Path, string Configuration = "Debug", string? TargetFramework = null);
public sealed record WorkspaceSnapshot(string Path, string SdkVersion, IReadOnlyList<WorkspaceProject> Projects, IReadOnlyList<WorkspaceIssue> Issues, IReadOnlyList<string>? Configurations = null);
public sealed record WorkspaceProject(string Id, string Name, string ProjectPath, string? TargetFramework, string? OutputPath, bool IsExecutable, IReadOnlyList<WorkspaceFile> Files, string? AssemblyName = null);
public sealed record WorkspaceFile(string Path, string Name, string Kind, bool IsGenerated = false, string? DocumentId = null, string? LogicalPath = null);
public sealed record WorkspaceIssue(string Message, string Severity = "Warning");
public sealed record UpdateDocumentRequest(string Path, string Text, long Version, bool Analyze = true);
public sealed record DocumentUpdateResult(bool Accepted, long Version, IReadOnlyList<WorkspaceDiagnostic> Diagnostics);
public sealed record DocumentPositionRequest(string Path, int Position, long Version);
public sealed record DocumentRequest(string Path, long Version);
public sealed record WorkspaceDiagnostic(string Id, string Message, string Severity, string? Path, int Line, int Column, int Start, int Length);
public sealed record WorkspaceDiagnosticEvent(string Path, long Version, IReadOnlyList<WorkspaceDiagnostic> Diagnostics);
public sealed record CompletionEntry(string Id, string DisplayText, string InsertText, string? Description, IReadOnlyList<string> Tags);
public sealed record CompletionResult(long Version, int Start, int Length, IReadOnlyList<CompletionEntry> Items);
public sealed record CompletionEditRequest(string Path, long Version, string ItemId);
public sealed record TextEdit(int Start, int Length, string NewText);
public sealed record DocumentEdits(string Path, long Version, IReadOnlyList<TextEdit> Edits, string? ExpectedTextHash = null);
public sealed record SignatureParameter(string Name, string Type, string? Documentation);
public sealed record SignatureEntry(string Label, string? Documentation, IReadOnlyList<SignatureParameter> Parameters);
public sealed record SignatureHelpResult(long Version, int ActiveParameter, IReadOnlyList<SignatureEntry> Signatures);
public sealed record SourceLocation(string Path, int Start, int Length, int Line, int Column, string? DisplayText = null);
public sealed record RenameRequest(string Path, int Position, long Version, string NewName);
public sealed record RefactorRequest(string Path, int Position, long Version, string Action);
public sealed record WorkspaceEditResult(IReadOnlyList<DocumentEdits> Documents, IReadOnlyList<string> Warnings);

/// <summary>All offsets are zero-based UTF-16; source lines and columns are one-based.</summary>
public interface IWorkspaceRpc
{
    Task<WorkspaceSnapshot> LoadAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken);
    Task<DocumentUpdateResult> UpdateDocumentAsync(UpdateDocumentRequest request, CancellationToken cancellationToken);
    Task CloseDocumentAsync(string path, CancellationToken cancellationToken);
    Task<CompletionResult> GetCompletionsAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    Task<TextEdit?> GetCompletionEditAsync(CompletionEditRequest request, CancellationToken cancellationToken);
    Task<SignatureHelpResult> GetSignatureHelpAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceLocation>> GetDefinitionAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceLocation>> FindReferencesAsync(DocumentPositionRequest request, CancellationToken cancellationToken);
    Task<WorkspaceEditResult> FormatDocumentAsync(DocumentRequest request, CancellationToken cancellationToken);
    Task<WorkspaceEditResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken);
    Task<WorkspaceEditResult> RefactorAsync(RefactorRequest request, CancellationToken cancellationToken);
}

public enum BuildOperation { Restore, Build, Rebuild, Clean, Run, Test }
public sealed record BuildRequest(string Path, BuildOperation Operation = BuildOperation.Build, string Configuration = "Debug", string? TargetFramework = null, string? LaunchProfile = null, string? Arguments = null);
public sealed record BuildOutputEvent(string Text, bool IsError = false, WorkspaceDiagnostic? Diagnostic = null);
public sealed record BuildResult(int ExitCode, bool Cancelled, IReadOnlyList<WorkspaceDiagnostic> Diagnostics);
public sealed record LaunchProfile(string Name, string? CommandName, string? CommandLineArgs, string? WorkingDirectory, IReadOnlyDictionary<string, string> EnvironmentVariables, string? ExecutablePath = null);
public sealed record LaunchTarget(string ProjectPath, string Name, IReadOnlyList<string> TargetFrameworks, IReadOnlyList<LaunchProfile> Profiles);
public sealed record ResolvedLaunch(string Program, string WorkingDirectory, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);
