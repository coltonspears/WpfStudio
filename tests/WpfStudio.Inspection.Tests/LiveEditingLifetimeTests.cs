using System.IO.Pipes;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class LiveEditingLifetimeTests
{
    [Fact]
    public async Task OperationLimitKeepsOriginalOutcomesAndDisconnectStillRestoresAnExistingOverride()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var target = await RuntimeEditingApplication.StartAsync("net10.0-windows", timeout.Token);
        var before = (await target.StateAsync(timeout.Token))["EditLiteral"];
        var first = await target.RequestAsync("EditLiteral", "Value", "Retained until disconnect", timeout.Token);
        Assert.Equal("Applied", (await target.Session.SetPropertyAsync(first, timeout.Token)).Outcome);

        // Structurally valid stale requests are accepted into the exactly-once
        // ledger but never reach the application's dispatcher or property.
        var stale = first with { Revision = first.Revision + 1 };
        for (int index = 1; index < 1024; index++)
        {
            var result = await target.Session.SetPropertyAsync(stale with { OperationId = Guid.NewGuid().ToString("N") }, timeout.Token);
            Assert.Equal("Conflict", result.Outcome);
        }
        var reset = await target.RequestAsync("EditLiteral", "Value", null, timeout.Token, reset: true);
        var rejected = await target.Session.SetPropertyAsync(reset, timeout.Token);
        Assert.Equal("Rejected", rejected.Outcome);
        Assert.Contains("operation limit", rejected.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.Session.IsConnected);
        Assert.Equal("Applied", (await target.Session.SetPropertyAsync(first, timeout.Token)).Outcome);
        Assert.Equal("Applied", (await target.Session.GetEditStatusAsync(new(first.OperationId), timeout.Token)).Outcome);
        Assert.Equal("Retained until disconnect", (await target.StateAsync(timeout.Token))["EditLiteral"].Value);

        await target.Session.DisconnectAsync(timeout.Token);
        var restored = (await target.StateAsync(timeout.Token))["EditLiteral"];
        Assert.Equal(before.Value, restored.Value);
        Assert.Equal(before.HasLocalValue, restored.HasLocalValue);
        Assert.Equal("ping", await target.App.SendAsync("ping", timeout.Token));
    }

    [Fact]
    public async Task StartedSlowCallbackReturnsUnknownThenItsOperationReportsCompletionWithoutRepeatingMutation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var target = await RuntimeEditingApplication.StartAsync("net10.0-windows", timeout.Token);
        var request = await target.RequestAsync("EditLiteral", "SlowValue", "slow", timeout.Token);
        var result = await target.Session.SetPropertyAsync(request, timeout.Token);
        Assert.Equal("Unknown", result.Outcome);
        Assert.True(File.Exists(Path.Combine(target.App.DirectoryPath, "edit-slow-started")));
        Assert.True(target.Session.IsConnected);
        InspectionPropertyEditResult status;
        do
        {
            status = await target.Session.GetEditStatusAsync(new(request.OperationId), timeout.Token);
            if (status.Outcome == "Unknown") await Task.Delay(50, timeout.Token);
        } while (status.Outcome == "Unknown");
        Assert.Equal("Applied", status.Outcome);
        Assert.Equal("slow", RuntimeEditingApplication.Property(await target.InspectAsync("EditLiteral", timeout.Token), "SlowValue").EditableValue);
        Assert.Equal("Applied", (await target.Session.SetPropertyAsync(request, timeout.Token)).Outcome);
        Assert.Equal("Reset", (await target.EditAsync("EditLiteral", "SlowValue", null, timeout.Token, reset: true)).Outcome);
        Assert.Equal("steady", (await target.StateAsync(timeout.Token))["EditLiteral"].SlowValue);
    }

    [Fact]
    public async Task CancellingQueuedEditCannotApplyItLaterWhenTheDispatcherResumes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var target = await RuntimeEditingApplication.StartAsync("net10.0-windows", timeout.Token);
        var before = (await target.StateAsync(timeout.Token))["EditLiteral"];
        var request = await target.RequestAsync("EditLiteral", "Value", "Must never run", timeout.Token);
        var blockId = await target.App.SubmitAsync("edit-block-main", timeout.Token);
        await WaitForFileAsync(target.App, "edit-block-started", timeout.Token);
        using var cancellation = new CancellationTokenSource();
        var mutation = target.Session.SetPropertyAsync(request, cancellation.Token);
        await Task.Delay(100, timeout.Token);
        cancellation.Cancel();
        Assert.Equal("Unknown", (await mutation.WaitAsync(timeout.Token)).Outcome);
        Assert.False(target.Session.IsConnected);
        await target.App.WaitForAcknowledgementAsync(blockId, timeout.Token);
        var after = (await target.StateAsync(timeout.Token))["EditLiteral"];
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.ValueChanges, after.ValueChanges); // Even a transient apply followed by reset is forbidden.
        Assert.Equal(before.BindingIdentity, after.BindingIdentity);
    }

    [Fact]
    public async Task CancellingAfterCallbackStartedReturnsUnknownAndRestoresItsOverrideWhenCallbackFinishes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var target = await RuntimeEditingApplication.StartAsync("net10.0-windows", timeout.Token);
        var request = await target.RequestAsync("EditLiteral", "SlowValue", "slow", timeout.Token);
        using var cancellation = new CancellationTokenSource();
        var mutation = target.Session.SetPropertyAsync(request, cancellation.Token);
        await WaitForFileAsync(target.App, "edit-slow-started", timeout.Token);
        cancellation.Cancel();
        Assert.Equal("Unknown", (await mutation.WaitAsync(timeout.Token)).Outcome);
        Assert.False(target.Session.IsConnected);
        while ((await target.StateAsync(timeout.Token))["EditLiteral"].SlowValue != "steady")
            await Task.Delay(25, timeout.Token);
        Assert.Equal("ping", await target.App.SendAsync("ping", timeout.Token));
    }

    [Fact]
    public async Task ThrowingPropertyCallbackCannotBeReportedAsAConfirmedSuccessfulEdit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var target = await RuntimeEditingApplication.StartAsync("net10.0-windows", timeout.Token);
        var request = await target.RequestAsync("EditLiteral", "ThrowingValue", "throw", timeout.Token);
        Assert.True((await target.Session.ValidatePropertyAsync(request, timeout.Token)).Success);
        Assert.Equal("safe", (await target.StateAsync(timeout.Token))["EditLiteral"].ThrowingValue);
        var result = await target.Session.SetPropertyAsync(request, timeout.Token);
        Assert.Equal("Unknown", result.Outcome);
        Assert.Contains("callback failed", result.Error);
        Assert.True(target.Session.IsConnected);
        var property = RuntimeEditingApplication.Property(await target.InspectAsync("EditLiteral", timeout.Token), "ThrowingValue");
        if (property.IsOverridden)
            Assert.Equal("Reset", (await target.EditAsync("EditLiteral", "ThrowingValue", null, timeout.Token, reset: true)).Outcome);
        Assert.Equal("ping", await target.App.SendAsync("ping", timeout.Token));
    }

    [Fact]
    public async Task AbruptPipeEofRestoresOriginalBindingAndOtherDispatcherPropertyWithoutWritingTheModel()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var configuration = new InspectionSession();
        await using var app = new FixtureApplication("net10.0-windows");
        var pipeName = "WpfStudio.Inspection.EditEof." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var environment = new Dictionary<string, string>(InspectionAgentUnderTest.CreateEnvironment(new Dictionary<string, string>(), configuration))
        { [InspectionProtocol.PipeVariable] = pipeName };
        app.Start(environment);
        await pipe.WaitForConnectionAsync(timeout.Token);
        var hello = (await InspectionWire.ReadAsync(pipe, timeout.Token))!.GetPayload<InspectionHello>();
        Assert.Equal(app.ProcessId, hello.ProcessId);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0,
            new InspectionWelcome(InspectionProtocol.Version, "editing EOF")), timeout.Token);
        InspectionAgentUnderTest.AssertLoadedAgent(await app.WaitForReadyAsync(timeout.Token));
        await app.SendAsync("edit-create", timeout.Token);
        var before = await RuntimeEditingApplication.ReadStateAsync(app, timeout.Token);
        long sequence = 0;
        var tree = await RpcAsync<InspectionTree>("tree", new InspectionTreeRequest());
        var main = Assert.Single(tree.Nodes, node => node.Name == "EditTwoWay");
        var mainElement = await RpcAsync<InspectionElement>("inspect", new InspectionNodeRequest(tree.Revision, main.Id));
        var edit = RuntimeEditingApplication.Request(mainElement, "Value", "Temporary before EOF");
        Assert.Equal("Applied", (await RpcAsync<InspectionPropertyEditResult>("set-property", edit)).Outcome);
        var child = Assert.Single(tree.Nodes, node => node.Name == "ChildPickButton");
        var childElement = await RpcAsync<InspectionElement>("inspect", new InspectionNodeRequest(tree.Revision, child.Id));
        Assert.Equal("Applied", (await RpcAsync<InspectionPropertyEditResult>("set-property",
            RuntimeEditingApplication.Request(childElement, "Width", "244.75"))).Outcome);
        var changed = await RuntimeEditingApplication.ReadStateAsync(app, timeout.Token);
        Assert.Equal("Temporary before EOF", changed["EditTwoWay"].Value);
        Assert.Equal(before["EditTwoWay"].SourceWrites, changed["EditTwoWay"].SourceWrites);
        Assert.Equal(244.75, changed.ChildWidth);
        pipe.Dispose();
        EditingState restored;
        do
        {
            restored = await RuntimeEditingApplication.ReadStateAsync(app, timeout.Token);
            if (restored["EditTwoWay"].Value != before["EditTwoWay"].Value || restored.ChildWidth != before.ChildWidth)
                await Task.Delay(25, timeout.Token);
        } while (restored["EditTwoWay"].Value != before["EditTwoWay"].Value || restored.ChildWidth != before.ChildWidth);
        Assert.Equal(before["EditTwoWay"].SourceWrites, restored["EditTwoWay"].SourceWrites);
        Assert.Equal(before["EditTwoWay"].BindingIdentity, restored["EditTwoWay"].BindingIdentity);
        await app.SendAsync("edit-model-update-again", timeout.Token);
        Assert.Equal("edit-model-update-again: EditTwoWay", (await RuntimeEditingApplication.ReadStateAsync(app, timeout.Token))["EditTwoWay"].Value);

        async Task<T> RpcAsync<T>(string kind, object payload)
        {
            var id = ++sequence;
            await InspectionWire.WriteAsync(pipe, InspectionMessage.Create(kind, id, payload), timeout.Token);
            var response = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
            Assert.Equal(id, response.Id);
            Assert.Equal(kind, response.Kind);
            Assert.Null(response.Error);
            return response.GetPayload<T>();
        }
    }

    private static async Task WaitForFileAsync(FixtureApplication app, string name, CancellationToken token)
    {
        while (!File.Exists(Path.Combine(app.DirectoryPath, name)))
        {
            Assert.False(app.HasExited);
            await Task.Delay(10, token);
        }
    }
}
