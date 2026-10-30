using WpfStudio.Core.Text;

namespace WpfStudio.Core.Tests;

public sealed class TaskCommentTests
{
    [Fact]
    public void FindsTokensAfterCommentOpenersWithOwnerAndText()
    {
        const string code = "class C\r\n{\r\n    // TODO: wire up save\r\n    /* HACK work around focus bug */\r\n    void M() { i--; // FIXME(colton) off by one\r\n    }\r\n}";
        var found = TaskComments.Find(@"C:\App\C.cs", code).ToArray();

        Assert.Equal(["TODO", "HACK", "FIXME"], found.Select(c => c.Token));
        Assert.Equal("wire up save", found[0].Text);
        Assert.Equal(3, found[0].Line);
        Assert.Equal(8, found[0].Column);
        Assert.Equal("work around focus bug", found[1].Text);
        Assert.Equal("colton", found[2].Owner);
        Assert.Equal("off by one", found[2].Text);
        Assert.True(found[2].IsHighPriority);
        Assert.False(found[0].IsHighPriority);
    }

    [Fact]
    public void XamlAndSqlCommentsCount()
    {
        Assert.Equal("replace with a style", TaskComments.Find("View.xaml", "<Grid>\n  <!-- TODO replace with a style -->\n</Grid>").Single().Text);
        Assert.Equal("add an index", TaskComments.Find("Schema.sql", "-- TODO add an index").Single().Text);
    }

    [Theory]
    [InlineData("var label = \"TODO\";")]
    [InlineData("// todo lower case is prose")]
    [InlineData("// BUGFIX is not a token")]
    [InlineData("-- TODO in C# is not a comment")]
    [InlineData("#region TODO")]
    public void IgnoresTextThatIsNotATaskComment(string line) => Assert.Empty(TaskComments.Find("C.cs", line));

    [Fact]
    public async Task UserTasksAreKeptPerWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "WpfStudio-TaskTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStateStore<UserTaskItem>("tasks", root);
            await store.SaveAsync(@"C:\Work\Shop.sln", [new("Ship the invoice view"), new("Review", true, @"C:\Work\A.cs", 12)]);

            var loaded = await store.LoadAsync(@"C:\Work\Shop.sln");
            Assert.Equal(["Ship the invoice view", "Review"], loaded.Select(t => t.Text));
            Assert.True(loaded[1].IsDone);
            Assert.Equal(12, loaded[1].Line);
            Assert.Empty(await store.LoadAsync(@"C:\Work\Other.sln"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
