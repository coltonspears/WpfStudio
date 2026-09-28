namespace WpfStudio.Contracts;

// A reviewed rename synchronizes authoritative source buffers, never replacement
// generated code or disk writes. CurrentSource plans have no compiler history.
// Baseline fields remain readable so older plans can be rejected explicitly.
public sealed record XamlNameProjectionBaseline(string ProjectPath, string Path, string TextHash);
public sealed record XamlNameProjectionStep(int DeclarationStart, string NewName);
public sealed record XamlNameProjectionExpectedDocument(string Path, string TextHash);
public sealed record XamlNameProjectionPlan(string XamlPath, string BaselineSourceBytes,
    IReadOnlyList<XamlNameProjectionBaseline> Baselines,
    IReadOnlyList<XamlNameProjectionStep> Steps,
    IReadOnlyList<XamlNameProjectionExpectedDocument> ExpectedDocuments,
    bool CurrentSource = false);
public sealed record XamlNameProjectionRequest(XamlNameProjectionPlan Plan,
    IReadOnlyList<UpdateDocumentRequest> Documents, bool Replay = false);
public sealed record XamlNameProjectionResult(bool Accepted, string? Status = null, bool BaselineRefreshed = false);
