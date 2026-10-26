using WpfStudio.Core.Git;

namespace WpfStudio.Core.Tests;

public sealed class GitFeatureTests
{
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
    [InlineData("https://token:x-oauth@github.com/owner/repo", GitHost.GitHub, "https://github.com/owner/repo")]
    [InlineData("git@gitlab.com:group/sub/repo.git", GitHost.GitLab, "https://gitlab.com/group/sub/repo")]
    [InlineData("https://bitbucket.org/team/repo.git", GitHost.Bitbucket, "https://bitbucket.org/team/repo")]
    [InlineData("https://git.example.com:8443/scm/repo.git", GitHost.Generic, "https://git.example.com:8443/scm/repo")]
    public void OtherHostsDropCredentialsAndTheGitSuffix(string url, GitHost host, string web)
    {
        var remote = GitRemote.Parse(url)!;
        Assert.Equal((host, web), (remote.Host, remote.WebUrl));
        Assert.DoesNotContain("token", remote.WebUrl);
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
        const string text = """
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
            """;
        var blocks = MergeConflicts.Parse(text);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(("HEAD", "var a = 1;\n", "feature/login", "var a = 2;\n", "var a = 0;\n"),
            (blocks[0].CurrentLabel, blocks[0].Current, blocks[0].IncomingLabel, blocks[0].Incoming, blocks[0].Base));
        Assert.Null(blocks[1].Base);
        var resolved = MergeConflicts.Apply(text, blocks, [ConflictChoice.Incoming, ConflictChoice.CurrentThenIncoming]);
        Assert.Equal("using System;\nvar a = 2;\nmiddle\nLeft();\nRight();\nend", resolved);
        Assert.Throws<InvalidOperationException>(() => MergeConflicts.Apply(text, blocks, [ConflictChoice.Base, ConflictChoice.Base]));
        Assert.Throws<InvalidOperationException>(() => MergeConflicts.Apply(text, blocks, [ConflictChoice.Current, ConflictChoice.Unresolved]));
    }

    [Fact]
    public void IncompleteOrIndentedMarkersAreNotConflicts()
    {
        Assert.Empty(MergeConflicts.Parse("<<<<<<< HEAD\nleft\n"));
        Assert.Empty(MergeConflicts.Parse("  <<<<<<< HEAD\nx\n  =======\ny\n  >>>>>>> b\n"));
        Assert.Empty(MergeConflicts.Parse("<<<<<<<< not a marker\n=======\n>>>>>>> b\n"));
    }
}
