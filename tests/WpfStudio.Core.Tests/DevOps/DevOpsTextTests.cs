using WpfStudio.Core.DevOps;

namespace WpfStudio.Core.Tests.DevOps;

public sealed class DevOpsTextTests
{
    [Fact]
    public void WiqlStringsDoubleTheirQuotes() => Assert.Equal("'it''s'", DevOpsText.WiqlString("it's"));

    [Theory]
    [InlineData("<div>Hello&nbsp;<b>world</b></div>", "Hello world")]
    [InlineData("Line one<br>Line two<br/>Line three", "Line one\nLine two\nLine three")]
    [InlineData("<p>One</p><p>Two</p>", "One\n\nTwo")]
    [InlineData("<ol><li>First</li><li>Second</li></ol>", "• First\n• Second")]
    [InlineData("<script>steal()</script><style>p{}</style>Safe &lt;tag&gt; &amp; text", "Safe <tag> & text")]
    [InlineData("Plain text stays", "Plain text stays")]
    [InlineData(null, "")]
    public void HtmlBecomesPlainTextWithoutMarkupOrScript(string? html, string text) => Assert.Equal(text, DevOpsText.HtmlToText(html));

    [Fact]
    public void TextForHtmlFieldsIsEncodedWithItsLineBreaks() =>
        Assert.Equal("a &lt;b&gt;<br/>c &amp; d", DevOpsText.TextToHtml("a <b>\r\nc & d"));

    [Theory]
    [InlineData("feature/{id}-{title}", 123, "Fix: Login fails — on Ünïcode!!", "Bug", "feature/123-fix-login-fails-on-unicode")]
    [InlineData("{type}/{id}", 7, "x", "User Story", "user-story/7")]
    [InlineData("", 9, "Crash on save", "Bug", "feature/9-crash-on-save")]
    [InlineData("users/ada/{id}_{title}", 5, "...", "Task", "users/ada/5_")]
    [InlineData("hotfix/{title}.lock", 1, "Release", "Bug", "hotfix/release")]
    public void BranchNamesFollowTheTemplateWithASafeSlug(string template, int id, string title, string type, string expected) =>
        Assert.Equal(expected, DevOpsText.BranchName(template, id, title, type));

    [Fact]
    public void LongTitlesAreCutToFortyCharactersOfSlug()
    {
        var name = DevOpsText.BranchName("feature/{id}-{title}", 1, new string('a', 30) + " " + new string('b', 30), "Bug");
        Assert.Equal("feature/1-" + new string('a', 30) + "-" + new string('b', 9), name);
    }

    [Fact]
    public void WorkItemReferencesAreFoundInMessagesAndBranchNames()
    {
        Assert.Equal([12, 34, 56], DevOpsText.WorkItemReferences("AB#12 fixes #34", "feature/56-thing"));
        Assert.Equal([7], DevOpsText.WorkItemReferences("users/ada/7_login", "Merge #7 again"));
        Assert.Empty(DevOpsText.WorkItemReferences("it&#39;s C#9 and issue#3", "release/2026.11", null));
        Assert.Equal([88], DevOpsText.WorkItemReferences("88"));
    }

    [Theory]
    [InlineData("feature/123-fix-login", "Fix login")]
    [InlineData("users/ada/add_dark_theme", "Add dark theme")]
    [InlineData("main", "Main")]
    [InlineData("feature/123", "feature/123")]
    public void TitlesAreSuggestedFromBranchNames(string branch, string title) => Assert.Equal(title, DevOpsText.TitleFromBranch(branch));

    [Fact]
    public void LogsLoseTheirTimestampsAndShowTheirMarkers()
    {
        const string log = "2026-11-17T10:00:00.1234567Z ##[section]Starting: Build\r\n" +
            "2026-11-17T10:00:01.0000000Z ##[group]Restore\n" +
            "2026-11-17T10:00:02.0000000Z   Restored 3 projects\n" +
            "2026-11-17T10:00:03.0000000Z ##[endgroup]\n" +
            "2026-11-17T10:00:04.0000000Z ##[warning]Obsolete API\n" +
            "2026-11-17T10:00:05.0000000Z \u001b[31mred\u001b[0m text\n" +
            "##[command]dotnet build";
        Assert.Equal("── Starting: Build\n▸ Restore\n  Restored 3 projects\nWARNING: Obsolete API\nred text\n> dotnet build", DevOpsText.FormatLog(log, keepTimestamps: false));
        Assert.StartsWith("2026-11-17T10:00:00.1234567Z ── Starting: Build\n", DevOpsText.FormatLog(log, keepTimestamps: true), StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkItemCopiesAsMarkdown()
    {
        var details = new DevOpsWorkItemDetails(new(7, "Login fails", "Bug", "Active", "Ada", "https://x/7", "Web\\Sprint 1", Tags: "ui; auth", Priority: 1),
            "Steps below", [new("1", new("u", "Grace"), "Seen on Edge", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))], [], ["Active"],
            Fields: [new("Repro steps", "Click sign in"), new("Effort", "")]);
        var markdown = DevOpsText.WorkItemMarkdown(details);
        Assert.StartsWith("## Bug 7: Login fails\n\n- State: Active\n- Assigned to: Ada\n- Iteration: Web\\Sprint 1\n- Priority: 1\n- Tags: ui, auth\n- Link: https://x/7\n", markdown, StringComparison.Ordinal);
        Assert.Contains("### Description\n\nSteps below\n", markdown, StringComparison.Ordinal);
        Assert.Contains("### Repro steps\n\nClick sign in\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Effort", markdown, StringComparison.Ordinal);
        Assert.Contains("**Grace** (2026-11-01):\nSeen on Edge", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TimelinesNestUnderTheirParentsInOrderAndShowOrphansAtTheTop()
    {
        DevOpsTimelineRecord Record(string id, string? parent, string type, int order, RunStatus state = RunStatus.Completed, RunResult result = RunResult.Succeeded, string? log = null) =>
            new(id, parent, type, id, order, state, result, LogId: log);
        var records = new[]
        {
            Record("stage2", null, "Stage", 2), Record("stage1", null, "Stage", 1),
            Record("job", "stage1", "Job", 1, RunStatus.Running, RunResult.None, "1"),
            Record("task", "job", "Task", 1, RunStatus.Queued, RunResult.None),
            Record("orphan", "missing", "Task", 0, result: RunResult.Failed, log: "9"),
            Record("check", "stage2", "Checkpoint.Approval", 1),
        };
        var tree = PipelineTimeline.Build(records);
        Assert.Equal(["orphan", "stage1", "stage2"], tree.Select(node => node.Record.Name));
        Assert.Equal("task", tree[1].Children.Single().Children.Single().Record.Name);
        Assert.Empty(tree[2].Children);
        Assert.True(PipelineTimeline.IsActive(records));
        Assert.Equal("orphan", PipelineTimeline.FirstFailure(tree)!.Record.Name);
        Assert.False(PipelineTimeline.IsActive([Record("done", null, "Job", 1)]));
    }

    [Fact]
    public void SummariesReadNaturally()
    {
        var pr = new DevOpsPullRequest(3, "T", "", PullRequestStatus.Abandoned, false, new("a", "Ada"), DateTimeOffset.Now, null, "f", "main",
            [new(new("b", "Bob"), ReviewVote.Approved), new(new("c", "Cy"), ReviewVote.ApprovedWithSuggestions)], "https://x", RepositoryId: "r");
        Assert.Equal(("Abandoned", "2 approvals", "f → main"), (pr.StatusText, pr.ReviewText, pr.BranchText));
        Assert.Equal("Closed", (pr with { RepositoryId = null }).StatusText);
        Assert.Equal("Merged", (pr with { Status = PullRequestStatus.Completed, IsMerged = true }).StatusText);
        Assert.Equal("Draft", (pr with { Status = PullRequestStatus.Active, IsDraft = true }).StatusText);
        Assert.Equal("1m 5s", DevOpsText.Duration(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(65)));
        Assert.Equal("2h 3m", DevOpsText.Duration(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(123)));
        Assert.Equal("", DevOpsText.Duration(null, null));
        Assert.Equal("Partially succeeded", DevOpsText.RunStatusText(RunStatus.Completed, RunResult.PartiallySucceeded));
        Assert.True(new DevOpsIdentity("x", "Ada", "ada@x").IsSame(new("", "Someone", "ADA@x")));
        Assert.False(new DevOpsIdentity("", "Ada").IsSame(new("", "Ada")));
    }
}
