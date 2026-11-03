using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Git;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class EditorGitCardsTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RelayCommand Nothing = new(() => { });

    private static BlameLine Commit(int index, string summary = "A change", string author = "Ada") =>
        new($"{index:x8}aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", author, author + "@example.invalid", Now.AddDays(-3), summary);

    [Fact]
    public void HistoryCardDescribesTheScopeAndEachCommit()
    {
        var commit = Commit(1);
        var card = BlameHistoryCard.Create("Parse", 4, 9, new([new(commit, 2)], 0), Now, Nothing, Nothing);

        Assert.Equal("Parse", card.Name);
        Assert.Equal("Lines 4–9 · 1 commit by 1 author", card.Scope);
        Assert.Null(card.OlderCommits);
        var row = Assert.Single(card.Rows);
        Assert.Equal("A change", row.Title);
        Assert.Same(commit, row.Commit);
        Assert.Same(Nothing, row.ShowChangeCommand);
        Assert.Contains("Ada · ", row.Detail);
        Assert.EndsWith("(3 days ago) · 00000001 · 2 lines", row.Detail);
    }

    [Fact]
    public void HistoryCardListsUncommittedLinesFirstWithNothingToOpen()
    {
        var card = BlameHistoryCard.Create("Parse", 1, 1, new([new(Commit(1, summary: ""), 1)], 1), Now, Nothing, Nothing);

        Assert.Equal(["Uncommitted changes", "00000001"], card.Rows.Select(row => row.Title));
        Assert.Null(card.Rows[0].Commit);
        Assert.Null(card.Rows[0].ShowChangeCommand);
        Assert.StartsWith("1 line not committed yet", card.Rows[0].Detail);
        Assert.EndsWith("· 1 line", card.Rows[1].Detail);
    }

    [Fact]
    public void HistoryCardLimitsItsRowsAndCountsTheRest()
    {
        var changes = Enumerable.Range(1, 15).Select(index => new BlameChange(Commit(index, author: "Author" + index % 2), 1)).ToArray();
        var card = BlameHistoryCard.Create("Parse", 1, 15, new(changes, 0), Now, Nothing, Nothing);

        Assert.Equal(12, card.Rows.Count);
        Assert.Equal("3 older commits are in Git history.", card.OlderCommits);
        Assert.Equal("Lines 1–15 · 15 commits by 2 authors", card.Scope);
    }

    [Theory]
    [InlineData(LineChangeKind.Added, "", "Added since the last commit", null, "Remove added lines")]
    [InlineData(LineChangeKind.Added, "old", "Added since the last commit", "old", "Remove added lines")]
    [InlineData(LineChangeKind.Modified, "old", "Changed since the last commit", "old", "Revert change")]
    [InlineData(LineChangeKind.Modified, "", "Changed since the last commit", "(empty line)", "Revert change")]
    [InlineData(LineChangeKind.Deleted, "old", "Deleted since the last commit", "old", "Revert change")]
    public void ChangeCardWordsTheChange(LineChangeKind kind, string original, string heading, string? committed, string revert)
    {
        var card = LineChangeCard.Create(new(kind, 1, 1, original), canRevert: true, Nothing, Nothing);

        Assert.Equal((heading, committed, revert), (card.Heading, card.CommittedText, card.RevertLabel));
    }

    [Fact]
    public Task HistoryTemplateShowsEachRowAndOnlyCommitsHaveActions() => OnDispatcherAsync(() =>
    {
        var copied = Commit(1);
        var card = BlameHistoryCard.Create("Parse", 1, 2, new([new(copied, 2)], 1), Now, Nothing, Nothing);

        var view = Render(card);

        Assert.Equal(["Parse", "Uncommitted changes", "A change"], Texts(view).Where(text => text is "Parse" or "Uncommitted changes" or "A change"));
        var showButtons = Descendants<Button>(view).Where(button => (string)button.Content == "Show change").ToArray();
        Assert.Equal(2, showButtons.Length);
        Assert.False(IsShown(showButtons[0]));
        Assert.True(IsShown(showButtons[1]));
        Assert.True(showButtons[1].IsEnabled);
        Assert.Same(copied, showButtons[1].CommandParameter);
        var copy = Descendants<Button>(view).Where(button => (string)button.Content == "Copy ID").ElementAt(1);
        Assert.True(copy.IsEnabled);
        Assert.Equal(copied.Commit, copy.CommandParameter);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ChangeTemplateHidesTheComparisonWithNothingToCompareAndHonoursReadOnly() => OnDispatcherAsync(() =>
    {
        var revert = new RelayCommand(() => { });
        var view = Render(LineChangeCard.Create(new(LineChangeKind.Modified, 1, 1, "committed text"), canRevert: false, revert, Nothing));
        Assert.Equal("committed text", Descendants<TextBox>(view).Single().Text);
        Assert.True(IsShown(Descendants<TextBox>(view).Single()));
        Assert.False(Descendants<Button>(view).Single(button => (string)button.Content == "Revert change").IsEnabled);
        Assert.True(Descendants<Button>(view).Single(button => (string)button.Content == "Close").IsEnabled);

        view = Render(LineChangeCard.Create(new(LineChangeKind.Added, 1, 1, ""), canRevert: true, revert, Nothing));
        Assert.False(IsShown(Descendants<TextBox>(view).Single()));
        Assert.True(Descendants<Button>(view).Single(button => (string)button.Content == "Remove added lines").IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheEditorsHistoryCardAsksTheViewModelToShowACommit() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        using var model = new EditorViewModel(new DocumentState(Path.Combine(Path.GetTempPath(), "History.cs"), "class C { }"), workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        var commit = Commit(1);
        BlameLine? requested = null;
        model.BlameCommitRequested += line => requested = line;

        var card = editor.CreateHistoryCard("C", 1, 1, new([new(commit, 1)], 0))!;
        card.Rows[0].ShowChangeCommand!.Execute(commit);

        Assert.Same(commit, requested);
        editor.ViewModel = null;
        Assert.Null(editor.CreateHistoryCard("C", 1, 1, new([new(commit, 1)], 0)));
    });

    [Fact]
    public Task TheEditorsChangeCardFollowsTheEditorAndIgnoresAStaleChange() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        const string text = "first\nsecond\n";
        var state = new DocumentState(Path.Combine(Path.GetTempPath(), "Stale.cs"), text);
        using var model = new EditorViewModel(state, workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        using var generated = new EditorViewModel(new DocumentState(Path.Combine(Path.GetTempPath(), "WpfStudio", "GeneratedSources", "G.cs"), text), workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });
        var editor = new EditorSurface { ViewModel = model };
        var change = new LineChange(LineChangeKind.Modified, 1, 1, "committed");

        var card = editor.CreateChangeCard(change);
        Assert.True(card.CanRevert);
        card.RevertCommand.Execute(null); // The view model no longer lists this change.
        Assert.Equal(text, state.Content);

        editor.ViewModel = generated;
        Assert.False(editor.CreateChangeCard(change).CanRevert);
    });

    private static FrameworkElement Render(object card)
    {
        var dictionary = (ResourceDictionary)Application.LoadComponent(new Uri("/WpfStudio;component/Resources/EditorPopups.xaml", UriKind.Relative));
        var view = new ContentPresenter { Content = card, Resources = dictionary };
        view.Measure(new Size(800, 800));
        view.Arrange(new Rect(0, 0, 800, 800));
        view.UpdateLayout();
        return view;
    }

    private static bool IsShown(UIElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static IEnumerable<string> Texts(DependencyObject root) => Descendants<TextBlock>(root).Select(block => block.Text);

    private static IEnumerable<T> Descendants<T>(DependencyObject owner) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(owner); index++)
        {
            var child = VisualTreeHelper.GetChild(owner, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
