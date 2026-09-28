using System.Text.Json;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveEditingPayloadTests
{
    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task AggregateEditablePayloadPreservesWholeValuesAndKeepsTheInspectorConnected(string framework)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var target = await RuntimeEditingApplication.StartAsync(framework, timeout.Token);
        await target.App.SendAsync("edit-observe-agent", timeout.Token);
        await target.App.SendAsync("edit-collect-agent", timeout.Token);
        var beforeInspection = await AgentStateAsync(target.App, timeout.Token);
        Assert.True(beforeInspection.Observed);
        Assert.True(beforeInspection.Collectible);
        Assert.Equal(0, beforeInspection.Unloading);
        Assert.True(beforeInspection.DiagnosticsLoaded); // Static-resource diagnostics subscribe before application startup.
        await target.App.SendAsync("edit-large-payload", timeout.Token);
        var element = await target.InspectAsync("EditLiteral", timeout.Token);
        Assert.True(target.Session.IsConnected);
        Assert.True((await AgentStateAsync(target.App, timeout.Token)).DiagnosticsLoaded);
        Assert.InRange(element.Properties.Sum(property => property.EditableValue?.Length ?? 0), 60000, 262144);

        var payload = element.Properties.Where(property => property.OwnerType.EndsWith(".EditPayloadOptions", StringComparison.Ordinal)).ToArray();
        Assert.Equal(160, payload.Length);
        string expected = new('\u6F22', 60000);
        var included = payload.Where(property => property.EditableValue is not null).ToArray();
        Assert.NotEmpty(included);
        Assert.All(included, property =>
        {
            Assert.Equal(expected, property.EditableValue);
            Assert.True(property.CanEdit, property.EditUnavailableReason);
            Assert.False(string.IsNullOrEmpty(property.EditToken));
        });
        var omitted = payload.Where(property => property.EditableValue is null).ToArray();
        Assert.NotEmpty(omitted);
        Assert.All(omitted, property =>
        {
            Assert.False(property.CanEdit);
            Assert.Contains("budget", property.EditUnavailableReason, StringComparison.OrdinalIgnoreCase);
        });

        // The per-property limit must also omit the existing value completely;
        // a shortened display string must never become editable source text.
        var oversized = RuntimeEditingApplication.Property(await target.InspectAsync("EditNullable", timeout.Token), "LongValue");
        Assert.Null(oversized.EditableValue);
        Assert.False(oversized.CanEdit);
        Assert.False(string.IsNullOrWhiteSpace(oversized.EditUnavailableReason));
        Assert.True(target.Session.IsConnected);
        Assert.Equal("ping", await target.App.SendAsync("ping", timeout.Token));
        await target.RefreshAsync(timeout.Token);
        Assert.True(target.Session.IsConnected);
        await target.Session.DisconnectAsync(timeout.Token);
        AgentState ended;
        do
        {
            ended = await AgentStateAsync(target.App, timeout.Token);
            if (ended.Unloading == 0) await Task.Delay(25, timeout.Token);
        } while (ended.Unloading == 0);
        Assert.Equal(1, ended.Unloading);
        Assert.False(target.App.HasExited);
    }

    private static async Task<AgentState> AgentStateAsync(FixtureApplication app, CancellationToken token)
    {
        await app.SendAsync("edit-agent-state", token);
        return JsonSerializer.Deserialize<AgentState>(await File.ReadAllTextAsync(Path.Combine(app.DirectoryPath, "agent-lifetime.json"), token))!;
    }

    private sealed record AgentState(bool Observed, bool Collectible, int Unloading, bool DiagnosticsLoaded);
}
