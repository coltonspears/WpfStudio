namespace WpfStudio.Contracts;

public sealed record XamlDocumentOverlay(string Path, string Text, long Version);
public sealed record XamlProjectAnalysisRequest(long Generation, IReadOnlyList<XamlDocumentOverlay> Overlays,
    string? ProjectPath = null, int MaximumFiles = 512, int MaximumFileCharacters = 1_000_000,
    int MaximumTotalCharacters = 8_000_000, int MaximumDiagnostics = 2000);
public sealed record XamlFileAnalysisResult(string Path, string ProjectPath, string ProjectName, long? Version,
    string? TextHash, string State, IReadOnlyList<WorkspaceDiagnostic> Diagnostics, string? Status = null);
public sealed record XamlProjectAnalysisResult(long Generation, long SemanticRevision, bool Accepted,
    IReadOnlyList<XamlFileAnalysisResult> Files, int TotalFiles, bool Truncated = false, string? Status = null);
public sealed record RefreshDiskDocumentsRequest(IReadOnlyList<string>? Paths = null, int MaximumFiles = 2048);
public sealed record RefreshDiskDocumentsResult(bool Accepted, bool Changed, int RefreshedFiles,
    bool Truncated = false, string? Status = null, int PendingFiles = 0);
