using System.Text;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Documents;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Shell.Tests;

/// <summary>The steps the verified edits share: a stale edit is refused at each one, and nothing reaches a buffer.</summary>
public sealed class VerifiedEditPipelineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("WpfStudio-VerifiedEdits-").FullName;
    private readonly DocumentStore _store;
    private readonly FakeHost _host = new();
    private readonly VerifiedEditPipeline _pipeline;

    public VerifiedEditPipelineTests()
    {
        _store = new(Path.Combine(_root, "state"));
        _pipeline = new(_host, _store, new(_store));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private async Task<DocumentState> OpenAsync(string name, string text)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, text);
        return await _store.OpenAsync(path);
    }

    private static FileChange Change(DocumentState document, string after) => new(document.Path, document.Content, after, "test");

    [Fact]
    public async Task AStaleEditIsRefusedWithoutAReview()
    {
        var document = await OpenAsync("A.xaml", "before");

        var review = await _pipeline.ReviewAsync("Edit", [Change(document, "after")], "", () => false);

        Assert.Equal(VerifiedEditReview.StaleBeforeReview, review);
        Assert.Empty(_host.Reviews);
    }

    [Fact]
    public async Task AnEditThatChangesNothingIsNotReviewed()
    {
        var document = await OpenAsync("A.xaml", "same");

        var review = await _pipeline.ReviewAsync("Edit", [Change(document, "same")], "", () => true);

        Assert.Equal(VerifiedEditReview.Unchanged, review);
        Assert.Empty(_host.Reviews);
    }

    [Fact]
    public async Task TheReviewShowsOnlyRealChanges()
    {
        var edited = await OpenAsync("A.xaml", "before");
        var prerequisite = await OpenAsync("B.xaml", "same");

        var review = await _pipeline.ReviewAsync("Edit", [Change(edited, "after"), Change(prerequisite, "same")], "why", () => true);

        Assert.Equal(VerifiedEditReview.Accepted, review);
        var shown = Assert.Single(_host.Reviews);
        Assert.Equal(edited.Path, Assert.Single(shown).Path);
    }

    [Fact]
    public async Task DecliningIsNotStaleEvenWhenTheEditAlsoWentStale()
    {
        var document = await OpenAsync("A.xaml", "before");
        var current = true;
        _host.Answer = false;
        _host.WhileReviewing = () => current = false;

        var review = await _pipeline.ReviewAsync("Edit", [Change(document, "after")], "", () => current);

        Assert.Equal(VerifiedEditReview.Declined, review);
    }

    [Fact]
    public async Task AnEditThatGoesStaleDuringItsReviewIsRefused()
    {
        var document = await OpenAsync("A.xaml", "before");
        var current = true;
        _host.WhileReviewing = () => current = false;

        var review = await _pipeline.ReviewAsync("Edit", [Change(document, "after")], "", () => current);

        Assert.Equal(VerifiedEditReview.StaleAfterReview, review);
        Assert.Equal("before", document.Content);
    }

    [Fact]
    public async Task ApplyingUnderAFalseGuardChangesNothing()
    {
        var document = await OpenAsync("A.xaml", "before");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.ApplyAsync([Change(document, "after")], () => false, default));

        Assert.Equal("before", document.Content);
        Assert.Empty(_host.Shown);
    }

    [Fact]
    public async Task ApplyingShowsWhatChangedAndStillValidatesUnchangedPrerequisites()
    {
        var edited = await OpenAsync("A.xaml", "before");
        var prerequisite = await OpenAsync("B.xaml", "same");
        var changes = new[] { Change(edited, "after"), Change(prerequisite, "same") };

        prerequisite.Content = "typed since it was prepared";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.ApplyAsync(changes, () => true, default));
        Assert.Equal("before", edited.Content);
        Assert.Empty(_host.Shown);

        prerequisite.Content = "same";
        await _pipeline.ApplyAsync(changes, () => true, default);
        Assert.Equal("after", edited.Content);
        Assert.Same(edited, Assert.Single(_host.Shown));
    }

    [Fact]
    public async Task ARememberedBufferNoticesEditsRestoredEditsAndAReopenedTab()
    {
        var edited = await OpenAsync("A.xaml", "before");
        var unchanged = _pipeline.Remember(edited);
        Assert.True(unchanged());
        edited.Content = "after";
        Assert.False(unchanged());

        var restored = await OpenAsync("B.xaml", "before");
        unchanged = _pipeline.Remember(restored);
        restored.Content = "after";
        restored.Content = "before";
        Assert.False(unchanged());

        var reopened = await OpenAsync("C.xaml", "before");
        unchanged = _pipeline.Remember(reopened);
        _store.Close(reopened);
        await _store.OpenAsync(reopened.Path);
        Assert.False(unchanged());
    }

    [Fact]
    public void AWorkerEditNeedsAVerifiedSnapshotOfEachDistinctTarget()
    {
        var verified = new DocumentEdits("A.xaml", 1, [], "hash");
        VerifiedEditPipeline.RequireVerifiedTargets([verified, new("B.xaml", 1, [], "hash")], "Rename", "Request rename again.");

        var missing = Assert.Throws<InvalidOperationException>(() => VerifiedEditPipeline.RequireVerifiedTargets([verified, new("B.xaml", 1, [])], "Rename", "Request rename again."));
        Assert.Equal("Rename has no verified source snapshot. Request rename again.", missing.Message);
        var duplicate = Assert.Throws<InvalidOperationException>(() => VerifiedEditPipeline.RequireVerifiedTargets([verified, new("a.XAML", 1, [], "hash")], "The quick fix", "Request it again."));
        Assert.Equal("The quick fix contains duplicate document targets. Request it again.", duplicate.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyCleanInvisibleTargetsThatTheEditOpenedAreReleased(bool editFails)
    {
        var existing = await OpenAsync("Existing.xaml", "text");
        string Closed(string name) { var path = Path.Combine(_root, name); File.WriteAllText(path, "text"); return path; }
        string opened = Closed("Opened.xaml"), dirty = Closed("Dirty.xaml"), shown = Closed("Shown.xaml"), other = Closed("Other.xaml");

        async Task EditAsync()
        {
            await _store.OpenAsync(opened);
            (await _store.OpenAsync(dirty)).Content = "edited";
            _host.Open.Add(await _store.OpenAsync(shown));
            await _store.OpenAsync(other);
            if (editFails) throw new InvalidOperationException("refused");
        }
        var run = _pipeline.ReleasingOpenedTargetsAsync([existing.Path, opened, dirty, shown], EditAsync);
        if (editFails) await Assert.ThrowsAsync<InvalidOperationException>(() => run); else await run;

        Assert.Null(_store.Find(opened));
        Assert.NotNull(_store.Find(existing.Path));
        Assert.NotNull(_store.Find(dirty));
        Assert.NotNull(_store.Find(shown));
        Assert.NotNull(_store.Find(other));
    }

    [Fact]
    public async Task ADesignerEditThatGoesStaleBeforeOrDuringItsReviewIsRefused()
    {
        await using var test = new ShellTestContext();
        var path = await test.CreateFileAsync("View.xaml", "<Grid />");
        await test.Shell.OpenDocumentAsync(path);
        var document = test.Shell.ActiveDocument!.State;
        var proposal = new XamlPropertyEditResult(new DocumentEdits(path, document.Version, [new(0, 0, "<!-- edit -->")], DocumentStore.Hash(Encoding.UTF8.GetBytes(document.Content))), "why");
        var host = (IDesignerSourceHost)test.Shell;
        var current = false;
        var validations = 0;
        DesignerLayoutSourceEdit Edit() => new(proposal, () => current, _ => { validations++; return Task.FromResult(new PreviewLayoutValidationResult(null!, true)); });

        var before = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReviewLayoutEditAsync(Edit()));
        Assert.Contains("preparing", before.Message);
        Assert.False(test.Shell.IsPreviewOpen);

        current = true;
        var reviewed = host.ReviewLayoutEditAsync(Edit());
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        while (!test.Shell.IsPreviewOpen) await Task.Delay(10, patience.Token);
        current = false;
        test.Shell.ChangeReview.AcceptCommand.Execute(null);
        var during = await Assert.ThrowsAsync<InvalidOperationException>(() => reviewed);
        Assert.Contains("reviewed", during.Message);
        Assert.Equal(0, validations);
        Assert.Equal("<Grid />", document.Content);

        current = true;
        var applied = host.ReviewLayoutEditAsync(Edit());
        while (!test.Shell.IsPreviewOpen) await Task.Delay(10, patience.Token);
        test.Shell.ChangeReview.AcceptCommand.Execute(null);
        Assert.True(await applied);
        Assert.Equal(1, validations);
        Assert.Equal("<!-- edit --><Grid />", document.Content);
    }

    private sealed class FakeHost : IVerifiedEditHost
    {
        public List<IReadOnlyList<FileChange>> Reviews { get; } = [];
        public List<DocumentState> Shown { get; } = [];
        public HashSet<DocumentState> Open { get; } = [];
        public bool Answer { get; set; } = true;
        public Action? WhileReviewing { get; set; }
        public Task<bool> ReviewChangesAsync(string title, IReadOnlyList<FileChange> changes, string warnings)
        {
            Reviews.Add(changes);
            WhileReviewing?.Invoke();
            return Task.FromResult(Answer);
        }
        public void ShowDocument(DocumentState document) => Shown.Add(document);
        public bool IsDocumentOpen(DocumentState document) => Open.Contains(document);
    }
}
