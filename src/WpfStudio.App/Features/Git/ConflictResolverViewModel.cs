using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Core.Git;

namespace WpfStudio.App.Features.Git;

/// <summary>One conflict in the resolver, with the side the user chose for it.</summary>
public sealed partial class ConflictItem(int number, ConflictBlock block, Action changed) : ObservableObject
{
    public int Number { get; } = number;
    public ConflictBlock Block { get; } = block;
    public string Heading => $"Conflict {Number}";
    public string CurrentLabel => "Current" + (Block.CurrentLabel.Length > 0 ? " · " + Block.CurrentLabel : "");
    public string IncomingLabel => "Incoming" + (Block.IncomingLabel.Length > 0 ? " · " + Block.IncomingLabel : "");
    public string Current => Block.Current.Length == 0 ? "(nothing)" : Block.Current.TrimEnd('\r', '\n');
    public string Incoming => Block.Incoming.Length == 0 ? "(nothing)" : Block.Incoming.TrimEnd('\r', '\n');
    public bool HasBase => Block.Base is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ChoiceText), nameof(IsResolved))] public partial ConflictChoice Choice { get; set; }
    public string ChoiceText => Choice switch
    {
        ConflictChoice.Current => "Keeps current", ConflictChoice.Incoming => "Keeps incoming",
        ConflictChoice.CurrentThenIncoming => "Keeps both, current first", ConflictChoice.IncomingThenCurrent => "Keeps both, incoming first",
        ConflictChoice.Base => "Keeps the common ancestor", _ => "Not resolved"
    };
    public bool IsResolved => Choice != ConflictChoice.Unresolved;
    partial void OnChoiceChanged(ConflictChoice value) => changed();
    [RelayCommand] private void Choose(ConflictChoice choice) => Choice = choice;
}

/// <summary>The conflict resolver: one conflicted file's blocks, the side chosen for each, and the result they make.</summary>
public sealed partial class ConflictResolverViewModel(GitService service, IGitOperationHost host) : ObservableObject
{
    public ObservableCollection<ConflictItem> Conflicts { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsResolving), nameof(ResolverHeading))] public partial GitConflictFile? ConflictFile { get; set; }
    [ObservableProperty] public partial string ResolvedPreview { get; set; } = "";
    public bool IsResolving => ConflictFile is not null;
    public string ResolverHeading => ConflictFile is null ? "" : $"{ConflictFile.RelativePath} · {Conflicts.Count(c => c.IsResolved)} of {Conflicts.Count} resolved";
    public bool AllConflictsChosen => Conflicts.Count > 0 && Conflicts.All(c => c.IsResolved);

    private bool CanResolve(GitChange? change) => host.CanOperate && change is { IsConflict: true };
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanResolve))]
    private Task ResolveConflictsAsync(GitChange? change, CancellationToken cancel) => host.RunAsync("Open conflicts", async token =>
    {
        if (!await host.SaveOpenDocumentsAsync(token))
            throw new InvalidOperationException("Save open documents before resolving conflicts.");
        var file = await service.ReadConflictAsync(host.RepositoryRoot!, change!, token);
        if (file.Blocks.Count == 0)
            throw new InvalidOperationException("No conflict markers remain in this file. If it is correct, stage it to mark it resolved.");
        Conflicts.Clear();
        for (var index = 0; index < file.Blocks.Count; index++) Conflicts.Add(new(index + 1, file.Blocks[index], UpdateResolution));
        ConflictFile = file;
        UpdateResolution();
        host.ReportStatus($"{file.Blocks.Count} conflict{(file.Blocks.Count == 1 ? "" : "s")} in {file.RelativePath}");
    }, cancel);
    [RelayCommand] private void ChooseAll(ConflictChoice choice) { foreach (var conflict in Conflicts) conflict.Choice = choice; }
    private bool CanApplyResolution() => host.CanOperate && ConflictFile is not null && AllConflictsChosen;
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanApplyResolution))]
    private Task ApplyResolutionAsync(CancellationToken cancel)
    {
        var file = ConflictFile!;
        var resolved = MergeConflicts.Apply(file.Text, [.. Conflicts.Select(c => c.Block)], [.. Conflicts.Select(c => c.Choice)]);
        return host.MutateAsync($"Resolve {file.RelativePath}", async token =>
        {
            var result = await service.ResolveConflictAsync(file, resolved, token);
            CloseResolver();
            return $"Resolved and staged {file.RelativePath}. " + result;
        }, cancel, changesWorkspaceFiles: true);
    }
    [RelayCommand] private void CloseResolver() { ConflictFile = null; Conflicts.Clear(); ResolvedPreview = ""; }

    /// <summary>Closes the open file once the repository no longer lists it as conflicted.</summary>
    internal void CloseIfResolved(IEnumerable<GitChange> changes)
    {
        if (ConflictFile is { } open && !changes.Any(change => change.IsConflict && change.Path == open.RelativePath)) CloseResolver();
    }

    internal void NotifyCommands()
    {
        ResolveConflictsCommand.NotifyCanExecuteChanged();
        ApplyResolutionCommand.NotifyCanExecuteChanged();
    }

    private void UpdateResolution()
    {
        OnPropertyChanged(nameof(ResolverHeading)); OnPropertyChanged(nameof(AllConflictsChosen));
        ApplyResolutionCommand.NotifyCanExecuteChanged();
        if (ConflictFile is not { } file) { ResolvedPreview = ""; return; }
        // Unresolved blocks keep their markers in the preview so it shows what is left.
        var output = new System.Text.StringBuilder();
        var cursor = 0;
        foreach (var conflict in Conflicts)
        {
            output.Append(file.Text, cursor, conflict.Block.Start - cursor);
            output.Append(conflict.IsResolved ? conflict.Block.Resolve(conflict.Choice) : file.Text[conflict.Block.Start..conflict.Block.End]);
            cursor = conflict.Block.End;
        }
        ResolvedPreview = output.Append(file.Text, cursor, file.Text.Length - cursor).ToString();
    }
}
