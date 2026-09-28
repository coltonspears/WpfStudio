namespace WpfStudio.Contracts;

// Replay describes authored rename operations against an exact compiler baseline.
// It carries no generated replacement code and never authorizes disk writes.
public sealed record XamlNameProjectionBaseline(string ProjectPath, string Path, string TextHash);
public sealed record XamlNameProjectionStep(int DeclarationStart, string NewName);
public sealed record XamlNameProjectionExpectedDocument(string Path, string TextHash);
public sealed record XamlNameProjectionPlan(string XamlPath, string BaselineSourceBytes,
    IReadOnlyList<XamlNameProjectionBaseline> Baselines,
    IReadOnlyList<XamlNameProjectionStep> Steps,
    IReadOnlyList<XamlNameProjectionExpectedDocument> ExpectedDocuments);
public sealed record XamlNameProjectionRequest(XamlNameProjectionPlan Plan,
    IReadOnlyList<UpdateDocumentRequest> Documents, bool Replay = false);
public sealed record XamlNameProjectionResult(bool Accepted, string? Status = null, bool BaselineRefreshed = false);
