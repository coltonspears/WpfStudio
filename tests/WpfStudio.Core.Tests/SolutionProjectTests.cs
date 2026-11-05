using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WpfStudio.Core.Tests;

public sealed class SolutionProjectTests
{
    private static string SolutionFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "WpfStudio.sln");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("WpfStudio.sln was not found above the test output.");
    }

    private static string FullPath(string directory, string relative) => Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar)));

    // Visual Studio restores and builds only what the solution lists, while dotnet follows references,
    // so a project missing from the solution breaks the IDE with NETSDK1004 and the command line stays green.
    [Fact]
    public void EveryReferencedProjectIsListedInTheSolution()
    {
        var solution = SolutionFile();
        var root = Path.GetDirectoryName(solution)!;
        var listed = Regex.Matches(File.ReadAllText(solution), @"^Project\(""[^""]+""\) = ""[^""]+"", ""([^""]+\.csproj)""", RegexOptions.Multiline)
            .Select(match => FullPath(root, match.Groups[1].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(listed.Count > 30);

        var missing = (from project in listed
                       from reference in XDocument.Load(project).Descendants("ProjectReference")
                       let target = FullPath(Path.GetDirectoryName(project)!, reference.Attribute("Include")!.Value)
                       where !listed.Contains(target)
                       select $"{Path.GetRelativePath(root, project)} references {Path.GetRelativePath(root, target)}").ToList();
        Assert.True(missing.Count == 0, "Not in WpfStudio.sln:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }
}
