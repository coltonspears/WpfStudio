using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Features.Documents;

/// <summary>What the external-changes review needs from the shell: the open editors and a few shell services.</summary>
public interface IExternalChangesHost
{
    IReadOnlyList<EditorViewModel> Documents { get; }
    /// <summary>Raised when a document is opened or closed.</summary>
    event Action? DocumentsChanged;
    /// <summary>Cancelled when the shell shuts down.</summary>
    CancellationToken Lifetime { get; }
    /// <summary>Runs an operation, reporting a failure in the status bar and Output instead of faulting the caller.</summary>
    Task GuardAsync(Func<Task> action);
    void ReportStatus(string status);
    void AppendOutput(string line);
    /// <summary>Closes an editor; with <paramref name="discardChanges"/> no save prompt is shown. False when the user cancelled.</summary>
    Task<bool> CloseDocumentAsync(EditorViewModel document, bool discardChanges);
    /// <summary>A check has read or reloaded open files, so analysis and Git margins that compare with disk must follow.</summary>
    void DiskVersionsChecked();
    /// <summary>A user preference of the review changed and should be persisted.</summary>
    void SettingChanged();
}
