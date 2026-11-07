using System.Text.Json;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

/// <summary>Real adapters omit optional fields, and a response may have no body at all. None of that may throw.</summary>
public sealed class DapMessagesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    /// <summary>What the transport hands over for a response without a body, plus bodies of every wrong shape.</summary>
    public static TheoryData<string?> Unusable => new() { null, "null", "5", "\"text\"", "[]", "[{}]", "{}" };
    private static JsonElement Body(string? text) => text is null ? default : Json(text);

    [Theory]
    [MemberData(nameof(Unusable))]
    public void ABodyWithoutTheListReadsAsEmpty(string? text)
    {
        var body = Body(text);
        Assert.Empty(DapMessages.Threads(body));
        Assert.Empty(DapMessages.StackFrames(body));
        Assert.Empty(DapMessages.Scopes(body));
        Assert.Empty(DapMessages.Variables(body));
        Assert.Empty(DapMessages.Breakpoints(body));
    }

    [Theory]
    [MemberData(nameof(Unusable))]
    public void ABodyWithoutTheFieldsReadsAsBlank(string? text)
    {
        var body = Body(text);
        Assert.Equal(new DapEvaluation("", "", 0), DapMessages.Evaluation(body));
        Assert.Equal(new DapExceptionInfo("", "", null), DapMessages.ExceptionInfo(body));
        Assert.Null(DapMessages.ChangedBreakpoint(body));
        Assert.Null(DapMessages.Module(body));
        var stopped = DapMessages.Stopped(body);
        Assert.Null(stopped.Reason);
        Assert.Equal(0, stopped.ThreadId);
        Assert.Empty(stopped.HitBreakpointIds);
    }

    [Theory]
    [InlineData("""{"threads":5}""")]
    [InlineData("""{"threads":"none"}""")]
    [InlineData("""{"threads":{"id":1}}""")]
    [InlineData("""{"threads":null}""")]
    [InlineData("""{"threads":[1,"x",null,true,[]]}""")]
    public void AListOfTheWrongKindOrEntriesThatAreNotObjectsReadAsEmpty(string text) => Assert.Empty(DapMessages.Threads(Json(text)));

    [Fact]
    public void ModuleEventsReadStringAndNumberIdsAndSkipModulesWithoutOne()
    {
        var loaded = DapMessages.Module(Json("""{"reason":"new","module":{"id":"a1","name":"App.dll","path":"C:\\src\\App.dll","symbolStatus":"Symbols loaded."}}"""));
        Assert.Equal(new DapModuleEvent("new", new DebugModule("a1", "App.dll", @"C:\src\App.dll", "Symbols loaded.")), loaded);
        Assert.True(loaded!.Module.HasSymbols);
        Assert.Equal("Symbols loaded", loaded.Module.Symbols);
        Assert.False(DapMessages.Module(Json("""{"reason":"new","module":{"id":"b","symbolStatus":"Symbols not found."}}"""))!.Module.HasSymbols);
        Assert.Equal(new DapModuleEvent("removed", new DebugModule("7", "", "", "")), DapMessages.Module(Json("""{"reason":"removed","module":{"id":7}}""")));
        Assert.Null(DapMessages.Module(Json("""{"reason":"new","module":{"name":"x"}}""")));
        Assert.Null(DapMessages.Module(Json("""{"reason":"new","module":{"id":null}}""")));
        Assert.Null(DapMessages.Module(Json("""{"reason":"new","module":[1]}""")));
    }

    [Fact]
    public void ThreadsReadCompletely() =>
        Assert.Equal([new DebugThread(7, "Main"), new DebugThread(8, "")], DapMessages.Threads(Json("""{"threads":[{"id":7,"name":"Main"},{"id":8}]}""")));

    [Fact]
    public void AThreadWithAMistypedIdKeepsItsNameAndReadsAsZero() =>
        Assert.Equal(new DebugThread(0, "9"), Assert.Single(DapMessages.Threads(Json("""{"threads":[{"id":"seven","name":9}]}"""))));

    [Fact]
    public void FrameSourceIsOptionalAtEveryLevel()
    {
        var frames = DapMessages.StackFrames(Json("""
            {"stackFrames":[
              {"id":1,"name":"Main","line":12,"column":3,"source":{"path":"C:/a.cs","name":"a.cs"}},
              {"id":2,"name":"External","line":0},
              {"id":3,"name":"NoPath","line":4,"source":{"sourceReference":9}},
              {"id":4,"source":null},
              {}
            ],"totalFrames":5}
            """));
        Assert.Equal(
        [
            new DebugFrame(1, "Main", "C:/a.cs", 12),
            new DebugFrame(2, "External", null, 0),
            new DebugFrame(3, "NoPath", "", 4),
            new DebugFrame(4, "", "", 0),
            new DebugFrame(0, "", null, 0)
        ], frames);
    }

    [Fact]
    public void ScopesAndVariablesReadWithOptionalFieldsMissing()
    {
        Assert.Equal([new DapScope("Locals", 10), new DapScope("", 0)], DapMessages.Scopes(Json("""{"scopes":[{"name":"Locals","variablesReference":10,"expensive":false},{}]}""")));
        Assert.Equal(
        [
            new DapVariable("a", "1", "int", 0),
            new DapVariable("b", "{B}", "", 20),
            new DapVariable("", "", "", 0)
        ], DapMessages.Variables(Json("""{"variables":[{"name":"a","value":"1","type":"int","variablesReference":0},{"name":"b","value":"{B}","variablesReference":20},{}]}""")));
    }

    [Theory]
    [InlineData("""{"variablesReference":"many"}""")]
    [InlineData("""{"variablesReference":1.5}""")]
    [InlineData("""{"variablesReference":99999999999}""")]
    [InlineData("""{"variablesReference":null}""")]
    public void AReferenceThatIsNotAnIntReadsAsNotExpandable(string text) => Assert.Equal(0, DapMessages.Evaluation(Json(text)).VariablesReference);

    [Fact]
    public void AValueSentAsAnotherKindIsStillShown()
    {
        Assert.Equal(new DapEvaluation("12", "", 0), DapMessages.Evaluation(Json("""{"result":12,"type":null}""")));
        Assert.Equal("""{"nested":true}""", DapMessages.Evaluation(Json("""{"result":{"nested":true}}""")).Result);
    }

    [Fact]
    public void ExceptionDetailsAreTextOfWhateverTheAdapterSent()
    {
        Assert.Equal(new DapExceptionInfo("E", "boom", """{"message":"boom"}"""), DapMessages.ExceptionInfo(Json("""{"exceptionId":"E","description":"boom","details":{"message":"boom"}}""")));
        Assert.Equal("", DapMessages.ExceptionInfo(Json("""{"details":null}""")).Details);
        Assert.Null(DapMessages.ExceptionInfo(Json("""{"exceptionId":"E"}""")).Details);
    }

    [Fact]
    public void BreakpointAnswersKeepTheirPositionsEvenWhenUnreadable()
    {
        var answers = DapMessages.Breakpoints(Json("""
            {"breakpoints":[
              {"id":3,"verified":true,"line":13},
              7,
              {"verified":false,"message":"no symbols"},
              {"id":"x","verified":"yes","line":"ten"}
            ]}
            """));
        Assert.Equal(
        [
            new DapBreakpoint(3, true, null, 13, null),
            new DapBreakpoint(null, false, null, 0, null),
            new DapBreakpoint(null, false, "no symbols", 0, null),
            new DapBreakpoint(null, false, null, 0, null)
        ], answers);
    }

    [Fact]
    public void AChangedBreakpointNeedsAnObject()
    {
        Assert.Null(DapMessages.ChangedBreakpoint(Json("""{"breakpoint":[1]}""")));
        Assert.Null(DapMessages.ChangedBreakpoint(Json("""{"breakpoint":null}""")));
        var changed = DapMessages.ChangedBreakpoint(Json("""{"reason":"changed","breakpoint":{"id":1,"verified":true,"line":9,"source":{"path":"C:/a.cs"}}}"""));
        Assert.Equal(new DapBreakpoint(1, true, null, 9, "C:/a.cs"), changed);
        Assert.Null(DapMessages.ChangedBreakpoint(Json("""{"breakpoint":{"id":1}}"""))!.SourcePath);
    }

    [Fact]
    public void StoppedKeepsOnlyTheIdsItCanRead()
    {
        var stopped = DapMessages.Stopped(Json("""{"reason":"breakpoint","threadId":7,"allThreadsStopped":true,"hitBreakpointIds":[1,"two",3.5,null,4]}"""));
        Assert.Equal("breakpoint", stopped.Reason);
        Assert.Equal(7, stopped.ThreadId);
        Assert.Equal([1, 4], stopped.HitBreakpointIds.Order());
        Assert.Empty(DapMessages.Stopped(Json("""{"reason":"step","threadId":"seven","hitBreakpointIds":"none"}""")).HitBreakpointIds);
        Assert.Equal(0, DapMessages.Stopped(Json("""{"threadId":"seven"}""")).ThreadId);
    }
}
