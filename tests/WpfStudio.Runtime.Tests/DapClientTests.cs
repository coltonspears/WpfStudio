using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

/// <summary>Typed requests over the real process and transport. The arguments are checked by the adapter reading them.</summary>
[Collection(AdapterRun.Collection)]
public sealed class DapClientTests
{
    [Fact]
    public async Task ACompleteAdapterAnswersEveryRequest()
    {
        await using var run = new AdapterRun("debug-queries");
        await run.AttachAsync(seconds: 10);
        var client = new DapClient(run.Session);
        Assert.Equal([new DebugThread(7, "Main Thread"), new DebugThread(8, "Worker")], await client.GetThreadsAsync());
        Assert.Equal([new DebugFrame(1, "Program.Main", @"C:\src\Program.cs", 12), new DebugFrame(2, "[External Code]", null, 0)], await client.GetStackTraceAsync(7, levels: 100));
        Assert.Equal([new DapScope("Locals", 1000), new DapScope("Empty", 0)], await client.GetScopesAsync(1));
        Assert.Equal(new DapVariable("answer", "42", "int", 0), (await client.GetVariablesAsync(1000, 0, 200))[0]);
        Assert.Equal(["[3]", "[4]"], (await client.GetVariablesAsync(2000, 3, 2)).Select(variable => variable.Name));
        Assert.Equal(new DapEvaluation("value of a + b", "int", 0), await client.EvaluateAsync("a + b", 1, "watch"));
        Assert.Equal(new DapExceptionInfo("System.Exception", "boom", """{"message":"boom"}"""), await client.GetExceptionInfoAsync(7));
        Assert.Equal([new DapBreakpoint(100, true, null, 13, null), new DapBreakpoint(101, true, null, 21, null)],
            await client.SetBreakpointsAsync("C:/src/Program.cs", [new("C:/src/Program.cs", 12), new("C:/src/Program.cs", 20)]));
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task AnAdapterThatOmitsFieldsReadsAsBlanks()
    {
        await using var run = new AdapterRun("partial");
        await run.AttachAsync(seconds: 10);
        var client = new DapClient(run.Session);
        Assert.Equal(new DebugThread(7, ""), Assert.Single(await client.GetThreadsAsync()));
        Assert.Equal(new DebugFrame(1, "", null, 0), Assert.Single(await client.GetStackTraceAsync(7, levels: 1)));
        Assert.Equal(new DapScope("", 0), Assert.Single(await client.GetScopesAsync(1)));
        Assert.Equal(new DapVariable("x", "", "", 0), Assert.Single(await client.GetVariablesAsync(0, 0, 200)));
        Assert.Equal(new DapEvaluation("", "", 0), await client.EvaluateAsync("x", 1, "hover"));
        Assert.Equal(new DapExceptionInfo("", "", null), await client.GetExceptionInfoAsync(7));
        Assert.Equal(new DapBreakpoint(null, false, null, 0, null), Assert.Single(await client.SetBreakpointsAsync("C:/a.cs", [new("C:/a.cs", 1)])));
        Assert.True(run.Session.IsActive);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task AnAdapterThatSendsTheWrongKindsDoesNotThrow()
    {
        await using var run = new AdapterRun("malformed");
        await run.AttachAsync(seconds: 10);
        var client = new DapClient(run.Session);
        Assert.Equal([new DebugThread(0, "9"), new DebugThread(7, "Main")], await client.GetThreadsAsync());
        Assert.Empty(await client.GetStackTraceAsync(7, levels: 1));
        Assert.Empty(await client.GetScopesAsync(1));
        Assert.Equal(new DapVariable("""{"nested":true}""", "5", "False", 0), Assert.Single(await client.GetVariablesAsync(0, 0, 200)));
        Assert.Equal(new DapEvaluation("12", "", 0), await client.EvaluateAsync("x", 1, "watch"));
        Assert.Equal(new DapExceptionInfo("", "", null), await client.GetExceptionInfoAsync(7));
        Assert.Equal(2, (await client.SetBreakpointsAsync("C:/a.cs", [new("C:/a.cs", 1)])).Count);
        Assert.True(run.Session.IsActive);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task AResponseWithoutABodyReadsAsEmpty()
    {
        await using var run = new AdapterRun("no-body");
        await run.AttachAsync(seconds: 10);
        var client = new DapClient(run.Session);
        Assert.Empty(await client.GetThreadsAsync());
        Assert.Empty(await client.GetStackTraceAsync(7, levels: 1));
        Assert.Empty(await client.GetScopesAsync(1));
        Assert.Empty(await client.GetVariablesAsync(1, 0, 200));
        Assert.Equal(new DapEvaluation("", "", 0), await client.EvaluateAsync("x", 1, "watch"));
        Assert.Equal(new DapExceptionInfo("", "", null), await client.GetExceptionInfoAsync(7));
        Assert.Empty(await client.SetBreakpointsAsync("C:/a.cs", [new("C:/a.cs", 1)]));
    }

    [Fact]
    public async Task ARefusedRequestStillFails()
    {
        await using var run = new AdapterRun("refuse-queries");
        await run.AttachAsync(seconds: 10);
        var client = new DapClient(run.Session);
        Assert.Equal("cannot evaluate", (await Assert.ThrowsAsync<InvalidOperationException>(() => client.EvaluateAsync("x", 1, "watch"))).Message);
        Assert.True(run.Session.IsActive);
    }
}
