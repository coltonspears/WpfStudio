using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using StreamJsonRpc;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Design;

namespace WpfStudio.Preview.Tests;

public sealed class PreviewProcessTests
{
    [Fact]
    public async Task IsolatedHostServesRpcRecoversAfterLoadErrorAndExitsOnDisconnect()
    {
        string pipeName = "WpfStudio.Preview.Tests." + Guid.NewGuid().ToString("N");
        string executable = PreviewHostUnderTest.ExecutablePath;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch preview host.");
        _ = process.Handle;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(timeout.Token);
                Assert.Equal(executable, process.MainModule!.FileName, ignoreCase: true);
                using var rpc = new JsonRpc(pipe);
                var preview = rpc.Attach<IPreviewRpc>();
                rpc.StartListening();
                await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => rpc.InvokeAsync("Dispose"));
                var invalid = await preview.RenderAsync(new("C:/preview/Invalid.xaml", "<Invalid/>", 1, 400, 300), timeout.Token);
                Assert.False(invalid.Success);
                Assert.False(process.HasExited);
                var valid = await preview.RenderAsync(new("C:/preview/Valid.xaml", "<UserControl xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Button Name='Subject' Width='100' Height='50'>Test</Button></UserControl>", 2, 400, 300), timeout.Token);
                Assert.True(valid.Success, string.Join("\n", valid.Diagnostics.Select(d => d.Message)));
                Assert.NotEmpty(valid.PngBytes!);
                var button = Assert.Single(valid.Nodes, n => n.Name == "Subject");
                var inspection = await preview.InspectAsync(new(2, button.Id), timeout.Token);
                Assert.Contains(inspection.Properties, p => p.Name == "Width" && p.Value == "100");
                var edited = await preview.SetPropertyAsync(new(2, button.Id, "Width", "140"), timeout.Token);
                Assert.True(edited.Success, edited.Error);
                Assert.Equal(140, Assert.Single(edited.Snapshot.Nodes, n => n.Name == "Subject").Bounds!.Width);
            }
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            await Task.WhenAll(stderr, stdout);
        }
    }

    [Fact]
    public async Task ExplicitHostRendersPicksReportsBindingFailuresAndResetsProperties()
    {
        string executable = PreviewHostUnderTest.ExecutablePath;
        await using var preview = new PreviewClient(executable);
        string xaml = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel.Resources><Style TargetType="Button"><Setter Property="Width" Value="100"/><Setter Property="Height" Value="50"/></Style></StackPanel.Resources>
              <TextBox x:Name="Source" Text="Original"/>
              <Button x:Name="Subject" Content="{Binding Text, ElementName=Source}"/>
              <TextBlock x:Name="Broken" Text="{Binding Mistyped, ElementName=Source}"/>
            </StackPanel>
            """;
        var snapshot = await preview.RenderAsync(new("C:/preview/PackagedHost.xaml", xaml, 1, 400, 300));
        Assert.True(snapshot.Success, string.Join("\n", snapshot.Diagnostics.Select(d => d.Message)));
        using var process = Process.GetProcessById(preview.ProcessId!.Value);
        Assert.Equal(executable, process.MainModule!.FileName, ignoreCase: true);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, snapshot.PngBytes![..8]);
        Assert.Contains(snapshot.Diagnostics, d => d.Message.Contains("Mistyped", StringComparison.Ordinal));
        var button = Assert.Single(snapshot.Nodes, n => n.Name == "Subject");
        Assert.Equal(xaml.IndexOf("<Button", StringComparison.Ordinal), button.Source!.Start);
        Assert.Equal(100, button.Bounds!.Width);
        var picked = await preview.PickAsync(new(snapshot.Version, button.Bounds.X + 3, button.Bounds.Y + 3));
        Assert.NotNull(picked.Node);
        var inspection = await preview.InspectAsync(new(snapshot.Version, button.Id));
        Assert.Contains(inspection.Properties, p => p.Name == "Width" && p.ValueSource == "Style");
        Assert.Contains(inspection.Properties, p => p.Name == "Content" && p.Value == "Original" && p.BindingPath == "Text");
        var width = Assert.Single(inspection.Properties, p => p.Name == "Width");
        Assert.True((await preview.ValidatePropertyAsync(new(snapshot.Version, button.Id, "Width", "180", OwnerType: width.OwnerType, OwnerAssembly: width.OwnerAssembly))).Success);
        Assert.False((await preview.ValidatePropertyAsync(new(snapshot.Version, button.Id, "Width", "-180", OwnerType: width.OwnerType, OwnerAssembly: width.OwnerAssembly))).Success);
        var overrideWidth = await preview.SetPropertyAsync(new(snapshot.Version, button.Id, "Width", "140"));
        Assert.True(overrideWidth.Success, overrideWidth.Error);
        Assert.Equal(140, Assert.Single(overrideWidth.Snapshot.Nodes, n => n.Name == "Subject").Bounds!.Width);
        var resetWidth = await preview.SetPropertyAsync(new(snapshot.Version, button.Id, "Width", null, Reset: true));
        Assert.True(resetWidth.Success, resetWidth.Error);
        Assert.Contains(resetWidth.Inspection.Properties, p => p.Name == "Width" && p.Value == "100" && p.ValueSource == "Style" && !p.IsOverridden);

        var broken = Assert.Single(snapshot.Nodes, n => n.Name == "Broken");
        var replacedBinding = await preview.SetPropertyAsync(new(snapshot.Version, broken.Id, "Text", "Preview override"));
        Assert.True(replacedBinding.Success, replacedBinding.Error);
        Assert.DoesNotContain(replacedBinding.Snapshot.Diagnostics, d => d.Message.Contains("Mistyped", StringComparison.Ordinal));
        var restoredBinding = await preview.SetPropertyAsync(new(snapshot.Version, broken.Id, "Text", null, Reset: true));
        Assert.True(restoredBinding.Success, restoredBinding.Error);
        Assert.Contains(restoredBinding.Inspection.Properties, p => p.Name == "Text" && p.BindingPath == "Mistyped");
        Assert.Contains(restoredBinding.Snapshot.Diagnostics, d => d.Message.Contains("Mistyped", StringComparison.Ordinal));
        await preview.DisposeAsync();
        Assert.True(process.HasExited);
    }
}
