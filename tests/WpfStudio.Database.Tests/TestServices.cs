using WpfStudio.Contracts;
using WpfStudio.Database.Services;

namespace WpfStudio.Database.Tests;

/// <summary>A recovery store with nothing to restore, for tests that are not about recovery.</summary>
internal sealed class NoRecovery : IQueryRecoveryStore
{
    public Task<IReadOnlyList<QueryRecoveryDocument>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<QueryRecoveryDocument>>([]);
    public Task SaveAsync(IReadOnlyList<QueryRecoveryDocument> documents, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Answers every question with no, for tests that are not about dialogs.</summary>
internal sealed class NoAnswers : IUserDialogService
{
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
    public Task<SaveDecision> AskSaveAsync(string documentName) => Task.FromResult(SaveDecision.Cancel);
    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    public Task<string?> PromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(null);
}
