using System.IO;
using System.Text.Json;
using System.Windows;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.App.Tests;

public sealed partial class ShellSmokeTests
{
    // Opt-in local reproduction through the real loaded shell. The target workspace
    // is read without editing, saving, building, or starting its application.
    private async Task VerifyExternalPreviewSolutionAsync(string root, MainWindow window, ShellViewModel shell, string solution)
    {
        string view = Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_PREVIEW_DOCUMENT")
            ?? throw new InvalidOperationException("Set WPFSTUDIO_TEST_PREVIEW_DOCUMENT to a XAML view in the solution.");
        string folder = Path.Combine(root, "artifacts", "external-preview");
        Directory.CreateDirectory(folder);
        await shell.LoadWorkspaceAsync(Path.GetFullPath(solution));
        await shell.OpenDocumentAsync(Path.GetFullPath(view));
        await shell.OpenDesignerCommand.ExecuteAsync(null);
        await Idle();
        var designer = shell.Designer;
        var observations = new List<object>();
        bool sourceSucceeded = designer.IsCurrent;
        async Task Record(string mode)
        {
            observations.Add(new { Mode = mode, designer.IsCurrent, designer.Status, designer.AssemblyPath,
                designer.ProjectDirectory, designer.ApplicationResourcePath,
                Diagnostics = designer.Diagnostics.ToArray(),
                Projects = shell.Projects.Select(project => new { project.Name, project.ProjectPath, project.OutputPath, project.ProjectAssetsPath }).ToArray() });
            await File.WriteAllTextAsync(Path.Combine(folder, "observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
            Screenshot((FrameworkElement)window.Content, Path.Combine(folder, mode + "-shell.png"));
            if (designer.Image is { } bytes) await File.WriteAllBytesAsync(Path.Combine(folder, mode + "-preview.png"), bytes);
            output.WriteLine(mode + ": " + designer.Status + "\n" + string.Join("\n", designer.Diagnostics.Select(d => d.Message)));
        }
        await Record("source");
        designer.Mode = PreviewMode.Compiled;
        await designer.RefreshCommand.ExecuteAsync(null);
        await Idle();
        await Record("compiled");
        Assert.True(sourceSucceeded, "Source preview failed; see artifacts/external-preview/observations.json");
        Assert.True(designer.IsCurrent, "Compiled preview failed; see artifacts/external-preview/observations.json");
    }
}
