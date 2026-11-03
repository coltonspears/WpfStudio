namespace WpfStudio.Contracts;

public interface IUiDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public interface IFileDialogService
{
    Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null);
    Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null);
    Task<string?> OpenFolderAsync(string title, string? initialDirectory = null);
}

/// <summary>Hands a path or address to the desktop shell.</summary>
public interface IDesktopService
{
    /// <summary>Shows a file or folder in File Explorer.</summary>
    void Reveal(string path);
    /// <summary>Opens an http(s) address in the default browser; other schemes are refused with an exception.</summary>
    void OpenUrl(string url);
}

public interface IClipboardService
{
    /// <summary>False when the clipboard could not be written, usually because another application holds it open.</summary>
    bool TrySetText(string text);
}

public enum SaveDecision { Save, Discard, Cancel }

public interface IUserDialogService
{
    Task<bool> ConfirmAsync(string title, string message);
    Task<SaveDecision> AskSaveAsync(string documentName);
    Task<string?> PromptAsync(string title, string message, string defaultValue = "");
    Task ShowErrorAsync(string title, string message);
}
