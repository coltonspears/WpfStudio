using System.Reflection;
using System.Text.Json;
using WpfStudio.Runtime.Inspection;
using Xunit.Abstractions;

namespace WpfStudio.Inspection.Tests;

public sealed class BindingSourceProbeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task PublicBindingSourceHintsAreRecordedWithoutAssumingParserPositions(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var session = new InspectionSession();
        await using var app = new FixtureApplication(framework);
        app.Start(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), session));
        session.ExpectProcess(app.ProcessId);
        await app.WaitForReadyAsync(timeout.Token);
        await session.WaitForConnectionAsync(timeout.Token);
        await app.SendAsync("binding-source-probe-create", timeout.Token);
        // Separate dispatcher turns allow normal WPF binding and layout work to
        // finish before the fixture records the observation-only getter count.
        await app.SendAsync("ping", timeout.Token);
        var first = await ReadAsync("initial");
        var stable = await ReadAsync("unchanged");
        var initialRows = first.GetProperty("Rows").EnumerateArray().ToArray();
        Assert.Equal(18, initialRows.Length);
        Assert.All(initialRows, row => Assert.False(row.TryGetProperty("Error", out _), row.GetRawText()));
        Assert.Equal(first.GetProperty("Rows").GetRawText(), stable.GetProperty("Rows").GetRawText());
        var style = initialRows.Single(row => row.GetProperty("Form").GetString() == "StyleSetter");
        var sharedStyle = initialRows.Single(row => row.GetProperty("Form").GetString() == "SharedStyleSetter");
        Assert.NotEqual(style.GetProperty("ExpressionId").GetInt32(), sharedStyle.GetProperty("ExpressionId").GetInt32());
        Assert.Equal(style.GetProperty("BindingObjectId").GetInt32(), sharedStyle.GetProperty("BindingObjectId").GetInt32());
        Assert.Equal(JsonValueKind.Null, initialRows.Single(row => row.GetProperty("Form").GetString() == "Code").GetProperty("BindingSource").ValueKind);
        await app.SendAsync("binding-source-probe-replace", timeout.Token);
        var replaced = await ReadAsync("replaced");
        var oldInline = initialRows.Single(row => row.GetProperty("Form").GetString() == "Inline");
        var newInline = replaced.GetProperty("Rows").EnumerateArray().Single(row => row.GetProperty("Form").GetString() == "Inline");
        Assert.NotEqual(oldInline.GetProperty("ExpressionId").GetInt32(), newInline.GetProperty("ExpressionId").GetInt32());
        Assert.NotEqual(oldInline.GetProperty("BindingObjectId").GetInt32(), newInline.GetProperty("BindingObjectId").GetInt32());
        Assert.Equal(oldInline.GetProperty("Path").GetString(), newInline.GetProperty("Path").GetString());
        Assert.Equal(JsonValueKind.Null, newInline.GetProperty("BindingSource").ValueKind);

        async Task<JsonElement> ReadAsync(string label)
        {
            await app.SendAsync("binding-source-probe-dump", timeout.Token);
            string json = await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "binding-source-probe.json"), timeout.Token);
            output.WriteLine(framework + " / " + label + Environment.NewLine + json);
            using var document = JsonDocument.Parse(json);
            var result = document.RootElement.Clone();
            Assert.Equal(result.GetProperty("GetterCallsBefore").GetInt32(), result.GetProperty("GetterCallsAfter").GetInt32());
            if (label == "initial")
            {
                var fixturePath = typeof(BindingSourceProbeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Single(attribute => attribute.Key == "InspectionFixtureSourcePath").Value!;
                string source = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(fixturePath)!, "BindingSourceProbeView.xaml"), timeout.Token);
                output.WriteLine("Authored XAML with one-based line numbers:");
                int line = 0;
                foreach (string text in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) output.WriteLine($"{++line}: {text}");
            }
            return result;
        }
    }
}
