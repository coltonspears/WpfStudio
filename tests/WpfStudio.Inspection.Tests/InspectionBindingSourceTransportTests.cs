using System.IO.Pipes;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionBindingSourceTransportTests
{
    private static BindingSourceRequest Selection => new(7, "node", "Text", "root-expression", "child-expression",
        "declaration", "System.Windows.Controls.TextBlock", "PresentationFramework", "observed-property");
    private static BindingSourceDeclaration Declaration => new("child-expression", "declaration", "root-expression", 1,
        "Binding", "Name", null, "Active", new("pack://application:,,,/App;component/View.xaml", 9, 12));

    [Fact]
    public async Task ExactChildIdentityAndSourceSurviveTheReadRoundTrip()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, true, timeout.Token);
        var pending = session.GetBindingSourceAsync(Selection, timeout.Token);
        var message = Assert.IsType<InspectionMessage>(await InspectionWire.ReadAsync(pipe, timeout.Token));
        Assert.Equal("binding-source", message.Kind);
        Assert.Equal(Selection, message.GetPayload<BindingSourceRequest>());
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("binding-source", message.Id,
            new BindingSourceResponse(Selection, true, Declaration)), timeout.Token);
        var response = await pending;
        Assert.Equal(Declaration, response.Declaration);
        Assert.True(response.Available);
        Assert.True(session.IsConnected);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("node")]
    [InlineData("property")]
    [InlineData("property identity")]
    [InlineData("root")]
    [InlineData("child")]
    [InlineData("declaration")]
    [InlineData("owner")]
    [InlineData("assembly")]
    [InlineData("wrong expression")]
    [InlineData("wrong declaration")]
    [InlineData("missing source")]
    [InlineData("missing declaration")]
    public async Task UnrelatedOrIncompleteAcknowledgementCannotAuthorizeNavigation(string mismatch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, true, timeout.Token);
        var pending = session.GetBindingSourceAsync(Selection, timeout.Token);
        var message = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        var request = mismatch switch
        {
            "revision" => Selection with { Revision = 8 },
            "node" => Selection with { NodeId = "other" },
            "property" => Selection with { Property = "Tag" },
            "property identity" => Selection with { PropertyId = "other" },
            "root" => Selection with { BindingId = "other" },
            "child" => Selection with { ExpressionId = "other" },
            "declaration" => Selection with { DeclarationId = "other" },
            "owner" => Selection with { OwnerType = "other" },
            "assembly" => Selection with { OwnerAssembly = "other" },
            _ => Selection
        };
        var declaration = mismatch switch
        {
            "wrong expression" => Declaration with { ExpressionId = "other" },
            "wrong declaration" => Declaration with { DeclarationId = "other" },
            "missing source" => Declaration with { Source = null },
            "missing declaration" => null,
            _ => Declaration
        };
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("binding-source", message.Id,
            new BindingSourceResponse(request, true, declaration)), timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => pending);
        Assert.True(session.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedOrCancelledReadDoesNotReachAgent(bool cancelled)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = new InspectionSession();
        using var pipe = await ConnectAsync(session, cancelled, timeout.Token);
        if (cancelled)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetBindingSourceAsync(Selection, cancellation.Token));
        }
        else Assert.False((await session.GetBindingSourceAsync(Selection, timeout.Token)).Available);
        var pending = session.SnapshotAsync(cancellationToken: timeout.Token);
        var message = (await InspectionWire.ReadAsync(pipe, timeout.Token))!;
        Assert.Equal("tree", message.Kind);
        await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("tree", message.Id, new InspectionTree(8, [], [])), timeout.Token);
        await pending;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(InspectionSession session, bool supported, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", session.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(token);
            session.ExpectProcess(Environment.ProcessId);
            await InspectionWire.WriteAsync(pipe, InspectionMessage.Create("hello", 0,
                new InspectionHello(InspectionProtocol.Version, session.Token, Environment.ProcessId,
                    Environment.Version.ToString(), "binding-source-test", supported ? ["tree", "binding-source"] : ["tree"])), token);
            Assert.Equal("hello", (await InspectionWire.ReadAsync(pipe, token))!.Kind);
            await session.WaitForConnectionAsync(token);
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
}
