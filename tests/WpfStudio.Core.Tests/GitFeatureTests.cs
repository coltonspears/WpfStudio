using WpfStudio.Core.Git;

namespace WpfStudio.Core.Tests;

public sealed class GitFeatureTests
{
    [Fact]
    public void PorcelainParserPreservesSpacesUnicodeAndRenameOrder()
    {
        var changes = GitOutput.ParseStatus(" M folder/space λ.cs\0R  new name.cs\0old name.cs\0?? note[1].txt\0UU conflict.cs\0");
        Assert.Equal(4, changes.Count);
        Assert.Equal("folder/space λ.cs", changes[0].Path);
        Assert.True(changes[0].IsUnstaged);
        Assert.False(changes[0].IsStaged);
        Assert.Equal("new name.cs", changes[1].Path);
        Assert.Equal("old name.cs", changes[1].OriginalPath);
        Assert.True(changes[1].IsStaged);
        Assert.True(changes[2].IsUnstaged);
        Assert.True(changes[3].IsConflict);
    }

    [Fact]
    public void CommitFileRecordsJoinStatusesWithLineCounts()
    {
        var files = GitOutput.ParseCommitFiles("M\0src/a b.cs\0R087\0old.cs\0new.cs\0A\0logo.png\0",
            "3\t1\tsrc/a b.cs\0" + "2\t2\t\0old.cs\0new.cs\0" + "-\t-\tlogo.png\0");
        Assert.Equal(3, files.Count);
        Assert.Equal<(string, char, int?, int?)>(("src/a b.cs", 'M', 3, 1), (files[0].Path, files[0].Status, files[0].Added, files[0].Deleted));
        Assert.Equal<(string, string?, char, int?)>(("new.cs", "old.cs", 'R', 2), (files[1].Path, files[1].OriginalPath, files[1].Status, files[1].Added));
        Assert.True(files[2].IsBinary);
        Assert.Equal("binary", files[2].AddedText);
    }

    [Fact]
    public void AStatusRecordThatIsNotPorcelainIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => GitOutput.ParseStatus("M\0"));
        Assert.Throws<InvalidOperationException>(() => GitOutput.ParseStatus("MM_a.cs\0"));
        Assert.Throws<InvalidOperationException>(() => GitOutput.ParseStatus("R  new.cs\0"));
        Assert.Empty(GitOutput.ParseStatus(""));
    }

    [Fact]
    public void HistoryRecordsCountParentsAndSurviveGitsLineBreakBetweenRecords()
    {
        var history = GitOutput.ParseHistory("aaa\0a\0Ada\02026-01-02T03:04:05+00:00\0First\0HEAD -> main, tag: v1\0ppp qqq\0\nbbb\0b\0Linus\02026-01-01T00:00:00+00:00\0Second\0\0\0\n");
        Assert.Equal(2, history.Count);
        Assert.Equal(("aaa", "a", "Ada", "First"), (history[0].Id, history[0].ShortId, history[0].Author, history[0].Subject));
        Assert.True(history[0].IsMerge);
        Assert.Equal(["main", "v1"], history[0].RefList.Select(reference => reference.Name));
        Assert.True(history[0].RefList[0].IsHead); Assert.True(history[0].RefList[1].IsTag);
        Assert.Equal("bbb", history[1].Id);
        Assert.Equal(0, history[1].ParentCount);
        Assert.Empty(GitOutput.ParseHistory(""));
    }

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://example.invalid/r.git/'")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    [InlineData("git@github.com: PERMISSION DENIED (PUBLICKEY).")]
    [InlineData("remote: TF401019: The Git repository does not exist or you do not have permissions")]
    [InlineData("The requested URL returned error: 403")]
    public void CredentialFailuresAreRecognizedWhateverTheirCase(string message) => Assert.True(GitOutput.IsAuthenticationFailure(message));

    [Theory]
    [InlineData("")]
    [InlineData("fatal: Not possible to fast-forward, aborting.")]
    [InlineData("fatal: unable to access 'https://example.invalid/': Could not resolve host: example.invalid")]
    public void OtherFailuresAreNotCredentialFailures(string message) => Assert.False(GitOutput.IsAuthenticationFailure(message));

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Shop/_git/Web", "https://dev.azure.com/contoso/Shop/_git/Web", "Web")]
    [InlineData("https://contoso@dev.azure.com/contoso/Shop%20Team/_git/Web", "https://dev.azure.com/contoso/Shop%20Team/_git/Web", "Web")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection/Shop/_git/Web", "https://contoso.visualstudio.com/DefaultCollection/Shop/_git/Web", "Web")]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Shop/Web", "https://dev.azure.com/contoso/Shop/_git/Web", "Web")]
    [InlineData("ssh://git@ssh.dev.azure.com:22/v3/contoso/Shop/Web", "https://dev.azure.com/contoso/Shop/_git/Web", "Web")]
    [InlineData("contoso@vs-ssh.visualstudio.com:v3/contoso/Shop/Web", "https://contoso.visualstudio.com/Shop/_git/Web", "Web")]
    public void AzureDevOpsRemotesResolveToTheirWebRepository(string url, string web, string name)
    {
        var remote = GitRemote.Parse(url)!;
        Assert.Equal((GitHost.AzureDevOps, web, name), (remote.Host, remote.WebUrl, remote.Name));
        Assert.Equal("Azure DevOps", remote.HostName);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", GitHost.GitHub, "https://github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo.git", GitHost.GitHub, "https://github.com/owner/repo")]
    [InlineData("https://build-agent@github.com/owner/repo", GitHost.GitHub, "https://github.com/owner/repo")]
    [InlineData("git@gitlab.com:group/sub/repo.git", GitHost.GitLab, "https://gitlab.com/group/sub/repo")]
    [InlineData("https://bitbucket.org/team/repo.git", GitHost.Bitbucket, "https://bitbucket.org/team/repo")]
    [InlineData("https://git.example.com:8443/scm/repo.git", GitHost.Generic, "https://git.example.com:8443/scm/repo")]
    public void OtherHostsDropCredentialsAndTheGitSuffix(string url, GitHost host, string web)
    {
        var remote = GitRemote.Parse(url)!;
        Assert.Equal((host, web), (remote.Host, remote.WebUrl));
        Assert.DoesNotContain("@", remote.WebUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\repos\\local")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("https://dev.azure.com/contoso/Shop")]
    public void LocalAndIncompleteRemotesHaveNoWebLinks(string url) => Assert.Null(GitRemote.Parse(url));

    [Fact]
    public void LinksFollowEachHostsConventions()
    {
        var azure = GitRemote.Parse("https://dev.azure.com/contoso/Shop/_git/Web")!;
        Assert.Equal("https://dev.azure.com/contoso/Shop/_git/Web/commit/abc123", azure.CommitUrl("abc123"));
        Assert.Equal("https://dev.azure.com/contoso/Shop/_git/Web?path=/src/My%20File.cs&version=GCabc123&line=12&lineEnd=13&lineStartColumn=1&lineEndColumn=1&lineStyle=plain&_a=contents",
            azure.FileUrl("abc123", "src\\My File.cs", 12));
        Assert.Equal("https://dev.azure.com/contoso/Shop/_git/Web/pullrequestcreate?sourceRef=feature%2Flogin", azure.NewPullRequestUrl("feature/login"));
        Assert.Equal("https://dev.azure.com/contoso/Shop/_git/Web?version=GBmain", azure.BranchUrl("main"));
        var github = GitRemote.Parse("git@github.com:owner/repo.git")!;
        Assert.Equal("https://github.com/owner/repo/blob/abc123/src/A.cs#L4", github.FileUrl("abc123", "src/A.cs", 4));
        Assert.Equal("https://github.com/owner/repo/compare/feature%2Flogin?expand=1", github.NewPullRequestUrl("feature/login"));
        Assert.Equal("https://gitlab.com/g/r/-/commit/abc", GitRemote.Parse("https://gitlab.com/g/r")!.CommitUrl("abc"));
    }

    [Fact]
    public void BlamePorcelainReusesCommitDetailsForLaterLines()
    {
        var a = new string('a', 40); var zero = new string('0', 40);
        var porcelain = $"""
            {a} 1 1 2
            author Ada Lovelace
            author-mail <ada@example.com>
            author-time 1700000000
            author-tz +0000
            summary Add the parser
            filename Parser.cs
            	class Parser
            {a} 2 2
            	{"{"}
            {zero} 3 3 1
            author Not Committed Yet
            author-mail <not.committed.yet>
            author-time 1700000500
            summary Version of Parser.cs from Parser.cs
            filename Parser.cs
            	// new
            """;
        var lines = GitBlame.Parse(porcelain);
        Assert.Equal(3, lines.Count);
        Assert.Equal(("Ada Lovelace", "ada@example.com", "Add the parser"), (lines[1].Author, lines[1].AuthorEmail, lines[1].Summary));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), lines[0].Time);
        Assert.True(lines[2].IsUncommitted);
        Assert.Equal("Ada Lovelace, 3 days ago · Add the parser", lines[0].Describe(lines[0].Time.AddDays(3).AddHours(2)));
        Assert.Equal("You · Uncommitted change", lines[2].Describe(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MemberHistorySummarizesTheBlameOfItsLines()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var old = new BlameLine(new string('a', 40), "Ada Lovelace", "ada@example.com", now.AddDays(-40), "Add the parser");
        var recent = new BlameLine(new string('b', 40), "Grace Hopper", "grace@example.com", now.AddDays(-2), "Handle comments");
        var again = new BlameLine(new string('c', 40), "Ada L.", "ADA@example.com", now.AddDays(-10), "Rename");
        var pending = new BlameLine(new string('0', 40), "Not Committed Yet", "not.committed.yet", now, "");
        BlameLine[] blame = [old, old, recent, again, pending, old];

        var history = GitBlame.Summarize(blame, 2, 5)!;
        Assert.Equal([(recent, 1), (again, 1), (old, 1)], history.Changes.Select(change => (change.Commit, change.Lines)));
        Assert.Equal(1, history.UncommittedLines);
        Assert.Equal(2, history.Authors); // The same address with a different name is one author.
        Assert.Equal("Uncommitted changes · Grace Hopper, 2 days ago · 2 authors, 3 changes", history.Describe(now));
        Assert.Equal("Ada Lovelace, 1 month ago", GitBlame.Summarize(blame, 1, 2)!.Describe(now));
        Assert.Null(GitBlame.Summarize(blame, 7, 9));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(6)] // Stale changes against a text that has since become shorter: trailing deletions gather on the last line.
    public void LineChangesAtFindsWhatAScanOfEveryChangeFinds(int lineCount)
    {
        LineChange[] changes =
        [
            new(LineChangeKind.Deleted, 1, 0, "gone"), new(LineChangeKind.Added, 2, 2, ""), new(LineChangeKind.Modified, 5, 1, "old"),
            new(LineChangeKind.Deleted, 7, 0, "gone"), new(LineChangeKind.Deleted, 9, 0, "gone"), new(LineChangeKind.Deleted, 10, 0, "gone")
        ];
        for (var line = 1; line <= lineCount; line++)
        {
            var number = line;
            var scanned = changes.Where(change => change.Kind == LineChangeKind.Deleted
                ? Math.Min(change.StartLine, lineCount) == number
                : number >= change.StartLine && number < change.StartLine + change.LineCount);
            Assert.Equal(scanned, LineChanges.At(changes, number, lineCount));
        }
        Assert.Empty(LineChanges.At([], 1, 1));
    }

    [Fact]
    public void LineChangesMarkAddedModifiedAndDeletedBlocks()
    {
        const string original = "one\ntwo\nthree\nfour\nfive\n";
        const string current = "one\nTWO\nthree\nfive\nsix\nseven\n";
        var changes = LineChanges.Compute(original, current);
        Assert.Equal(
        [
            new LineChange(LineChangeKind.Modified, 2, 1, "two"),
            new LineChange(LineChangeKind.Deleted, 4, 0, "four"),
            new LineChange(LineChangeKind.Added, 5, 2, ""),
        ], changes);
        Assert.Empty(LineChanges.Compute(current, current));
        Assert.Equal([new LineChange(LineChangeKind.Added, 1, 2, "")], LineChanges.Compute(null, "a\nb"));
        Assert.Equal([new LineChange(LineChangeKind.Deleted, 1, 0, "zero")], LineChanges.Compute("zero\none\n", "one\n"));
    }

    [Fact]
    public void ConflictsParseWithAndWithoutBaseAndResolve()
    {
        // The literal's line endings are whatever this file was checked out with, so they are set here.
        var text = """
            using System;
            <<<<<<< HEAD
            var a = 1;
            ||||||| merged common ancestors
            var a = 0;
            =======
            var a = 2;
            >>>>>>> feature/login
            middle
            <<<<<<< Updated upstream
            Left();
            =======
            Right();
            >>>>>>> Stashed changes
            end
            """.ReplaceLineEndings("\n");
        var blocks = MergeConflicts.Parse(text);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(("HEAD", "var a = 1;\n", "feature/login", "var a = 2;\n", "var a = 0;\n"),
            (blocks[0].CurrentLabel, blocks[0].Current, blocks[0].IncomingLabel, blocks[0].Incoming, blocks[0].Base));
        Assert.Null(blocks[1].Base);
        var resolved = MergeConflicts.Apply(text, blocks, [ConflictChoice.Incoming, ConflictChoice.CurrentThenIncoming]);
        Assert.Equal("using System;\nvar a = 2;\nmiddle\nLeft();\nRight();\nend", resolved);
        Assert.Throws<InvalidOperationException>(() => MergeConflicts.Apply(text, blocks, [ConflictChoice.Base, ConflictChoice.Base]));
        Assert.Throws<InvalidOperationException>(() => MergeConflicts.Apply(text, blocks, [ConflictChoice.Current, ConflictChoice.Unresolved]));

        // A file with Windows line endings keeps them, in each side and in what is left after resolving.
        var windows = text.ReplaceLineEndings("\r\n");
        var windowsBlocks = MergeConflicts.Parse(windows);
        Assert.Equal(2, windowsBlocks.Count);
        Assert.Equal(("HEAD", "var a = 1;\r\n", "feature/login", "var a = 2;\r\n", "var a = 0;\r\n"),
            (windowsBlocks[0].CurrentLabel, windowsBlocks[0].Current, windowsBlocks[0].IncomingLabel, windowsBlocks[0].Incoming, windowsBlocks[0].Base));
        Assert.Equal("using System;\r\nvar a = 2;\r\nmiddle\r\nLeft();\r\nRight();\r\nend",
            MergeConflicts.Apply(windows, windowsBlocks, [ConflictChoice.Incoming, ConflictChoice.CurrentThenIncoming]));
    }

    [Fact]
    public void IncompleteOrIndentedMarkersAreNotConflicts()
    {
        Assert.Empty(MergeConflicts.Parse("<<<<<<< HEAD\nleft\n"));
        Assert.Empty(MergeConflicts.Parse("  <<<<<<< HEAD\nx\n  =======\ny\n  >>>>>>> b\n"));
        Assert.Empty(MergeConflicts.Parse("<<<<<<<< not a marker\n=======\n>>>>>>> b\n"));
    }
}
