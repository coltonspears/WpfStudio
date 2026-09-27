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

public enum SaveDecision { Save, Discard, Cancel }

public interface IUserDialogService
{
    Task<bool> ConfirmAsync(string title, string message);
    Task<SaveDecision> AskSaveAsync(string documentName);
    Task<string?> PromptAsync(string title, string message, string defaultValue = "");
    Task ShowErrorAsync(string title, string message);
}
