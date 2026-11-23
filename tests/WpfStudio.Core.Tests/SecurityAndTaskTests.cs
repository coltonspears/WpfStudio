using WpfStudio.Core.IO;
using WpfStudio.Core.Tasks;

namespace WpfStudio.Core.Tests;

public sealed class SecurityAndTaskTests
{
    private static readonly string Tools = MakeTools();
    private static string MakeTools()
    {
        var folder = Path.Combine(Path.GetTempPath(), "WpfStudio-SecurityTaskTools");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "pwsh.exe", "cmd.exe", "sh" }) File.WriteAllText(Path.Combine(folder, name), "");
        return folder;
    }
    private static string Tool(string name) => Path.Combine(Tools, name);
    private static readonly TaskShell PowerShell = new(Tool("pwsh.exe"), []);
    private static string? Find(string name) => name switch
    {
        "pwsh.exe" => Tool("pwsh.exe"),
        "cmd.exe" => Tool("cmd.exe"),
        "sh" => Tool("sh"),
        _ => null
    };
    private static TaskLaunch Launch(StudioTask task) =>
        TaskCommandLine.Create(task, new TaskVariableContext { WorkspaceFolder = Tools }, PowerShell, Find, Tools);

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a;b")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("a>out")]
    [InlineData("a<in")]
    [InlineData("%TEMP%")]
    public void PowerShellTaskQuotesArgumentsContainingShellMetacharacters(string arg)
    {
        var launch = Launch(new StudioTask { Label = "ps", Command = "echo", Args = [arg] });
        Assert.Equal($"echo '{arg}'", launch.DisplayCommand);
    }

    [Theory]
    [InlineData("a&calc")]
    [InlineData("a|calc")]
    [InlineData("a>out.txt")]
    [InlineData("a<in.txt")]
    public void CmdTaskQuotesArgumentsContainingShellMetacharacters(string arg)
    {
        var launch = Launch(new StudioTask { Label = "cmd", Command = "echo", Args = [arg], Shell = new TaskShell("cmd.exe", []) });
        Assert.Equal($"echo \"{arg}\"", launch.DisplayCommand);
    }

    [Fact]
    public void CmdTaskEscapesDoubleQuotesAndEnvironmentExpansionWithoutQuoteBreakout()
    {
        var launch = Launch(new StudioTask
        {
            Label = "cmd-quote",
            Command = "echo",
            Args = ["foo\"&calc&\"bar", "%PATH%", "!VAR!"],
            Shell = new TaskShell("cmd.exe", [])
        });

        Assert.Equal("echo \"foo\"^\"\"&calc&\"^\"\"bar\" \"\"^%\"PATH\"^%\"\" \"\"^!\"VAR\"^!\"\"", launch.DisplayCommand);
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    [InlineData("null\0byte")]
    public void CmdTaskRejectsLineBreaksAndNullCharactersInArguments(string arg)
    {
        Assert.Throws<TaskConfigurationException>(() =>
            Launch(new StudioTask { Label = "cmd-nl", Command = "echo", Args = [arg], Shell = new TaskShell("cmd.exe", []) }));
    }

    [Fact]
    public void PosixShellTaskQuotesMetacharactersAndBackslashes()
    {
        var launch = Launch(new StudioTask
        {
            Label = "sh",
            Command = "echo",
            Args = ["a;b", "it's", @"a\b"],
            Shell = new TaskShell("sh", [])
        });

        Assert.Equal("echo 'a;b' 'it'\\''s' 'a\\b'", launch.DisplayCommand);
    }

    [Fact]
    public void RepositoryPathAllowsRelativePathsInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "WpfStudio-RepoContainment");
        Assert.True(RepositoryPath.TryResolveContained(root, "src/App/Main.cs", out var resolved));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "App", "Main.cs")), resolved);
        Assert.Equal(resolved, RepositoryPath.ResolveContained(root, @"src\App\Main.cs"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("../WpfStudio-RepoContainment-evil/secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\file.txt")]
    public void RepositoryPathRejectsRootedAndEscapingPaths(string? relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "WpfStudio-RepoContainment");
        Assert.False(RepositoryPath.TryResolveContained(root, relativePath, out var resolved));
        Assert.Equal("", resolved);
        if (relativePath is not null)
            Assert.Throws<InvalidOperationException>(() => RepositoryPath.ResolveContained(root, relativePath));
    }
}
