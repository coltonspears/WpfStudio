using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Runtime.Design;

namespace WpfStudio.App.Features.Designer;

public sealed partial class DesignerViewModel
{
    private long _interactionTransitionRevision = long.MinValue;

    [ObservableProperty] public partial IPreviewInteractionSession? InteractionSession { get; set; }
    [ObservableProperty] public partial bool IsInteracting { get; set; }
    public double InteractionWidth => _snapshot?.PixelWidth ?? 0;
    public double InteractionHeight => _snapshot?.PixelHeight ?? 0;
    public bool CanInteract => !_disposed && IsCurrent && !IsBusy && _snapshot?.Surface is not null
        && _interactionTransitionRevision != _revision;

    private bool CanEnterInteraction() => CanInteract && !IsInteracting;
    private bool CanExitInteraction() => !_disposed && IsInteracting;

    [RelayCommand(CanExecute = nameof(CanEnterInteraction))]
    private void EnterInteraction()
    {
        if (!CanEnterInteraction() || _snapshot?.Surface is not { } surface) return;
        try
        {
            var session = _client.CreateInteractionSession(surface);
            if (session is not { IsAvailable: true } || session.Surface != surface)
            {
                Status = "Native interaction is unavailable for this preview. Refresh with a compatible preview host.";
                return;
            }
            if (!ReferenceEquals(InteractionSession, session))
            {
                ForgetInteraction();
                InteractionSession = session;
                session.Changed += InteractionChanged;
            }
            IsInteracting = true;
            Status = "Interactive preview at actual size. Press F8 or choose Inspect to update the snapshot; Refresh recreates the view.";
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanExitInteraction))]
    private async Task ExitInteractionAsync()
    {
        if (!CanExitInteraction()) return;
        var revision = _revision;
        var session = InteractionSession;
        _interactionTransitionRevision = revision;
        IsInteracting = false;
        NotifyInteractionState();
        try
        {
            if (session is not null) await session.DeactivateAsync(_lifetime.Token);
            if (!Current(revision) || !ReferenceEquals(session, InteractionSession) || IsInteracting) return;
            await UpdateSnapshotAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (Current(revision) && ReferenceEquals(session, InteractionSession)) Status = exception.Message;
        }
        finally
        {
            if (_interactionTransitionRevision == revision) _interactionTransitionRevision = long.MinValue;
            NotifyInteractionState();
        }
    }

    private void InteractionChanged(object? sender, PreviewInteractionEvent update) => _dispatcher.Post(() =>
    {
        if (_disposed || !ReferenceEquals(sender, InteractionSession)) return;
        if (!update.Available)
        {
            Invalidate();
            Status = update.Status ?? "Interactive preview ended. Refresh to create a new preview.";
        }
        else if (IsInteracting && !string.IsNullOrEmpty(update.Status)) Status = update.Status;
    });

    private void ForgetInteraction()
    {
        var session = InteractionSession;
        if (session is not null) session.Changed -= InteractionChanged;
        IsInteracting = false;
        InteractionSession = null;
        NotifyInteractionState();
        if (session is not null) _ = DeactivateRetiredInteractionAsync(session);
    }

    private static async Task DeactivateRetiredInteractionAsync(IPreviewInteractionSession session)
    {
        try { await session.DeactivateAsync().ConfigureAwait(false); }
        // The client serializes duplicate detaches and rejects a superseded
        // surface. Its watchdog remains armed until detach or process exit;
        // a view destroying its native bridge also confirms exit through Abort.
        catch (Exception) { }
    }

    private void UpdateInteractionSnapshot()
    {
        if (InteractionSession is { } session && session.Surface != _snapshot?.Surface) ForgetInteraction();
        OnPropertyChanged(nameof(InteractionWidth));
        OnPropertyChanged(nameof(InteractionHeight));
        NotifyInteractionState();
    }

    partial void OnIsInteractingChanged(bool value) => NotifyInteractionState();
    private void NotifyInteractionState()
    {
        OnPropertyChanged(nameof(CanInteract));
        EnterInteractionCommand.NotifyCanExecuteChanged();
        ExitInteractionCommand.NotifyCanExecuteChanged();
    }
}
