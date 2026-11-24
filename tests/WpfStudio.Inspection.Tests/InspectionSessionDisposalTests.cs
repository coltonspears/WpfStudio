using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionSessionDisposalTests
{
    [Fact]
    public async Task RepeatedPauseResumeCyclesAndDoubleDisposeCompleteCleanly()
    {
        var session = new InspectionSession(connectionTimeout: TimeSpan.FromSeconds(5));
        for (var i = 0; i < 10; i++)
        {
            session.SetDebuggerPaused(true);
            Assert.True(session.IsDebuggerPaused);
            session.SetDebuggerPaused(false);
            Assert.False(session.IsDebuggerPaused);
        }

        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task DisconnectFollowedByDisposeAsyncCompletesCleanly()
    {
        var session = new InspectionSession(connectionTimeout: TimeSpan.FromSeconds(5));
        await session.DisconnectAsync();
        await session.DisposeAsync();
        Assert.False(session.IsConnected);
    }
}
