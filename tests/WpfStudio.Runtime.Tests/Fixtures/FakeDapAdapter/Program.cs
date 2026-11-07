using System.Text;
using System.Text.Json;

// A scripted debug adapter, so DebugSession's process handling can be tested without netcoredbg.
var scenario = Environment.GetEnvironmentVariable("WPFSTUDIO_FAKE_DAP_SCENARIO");
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var continues = 0;
var applied = 0;
const string BreakpointMovedAway = """{"reason":"changed","breakpoint":{"id":100,"verified":false,"message":"moved away","line":40,"source":{"path":"C:\\src\\Program.cs"}}}""";

void Raw(string text) { output.Write(Encoding.ASCII.GetBytes(text)); output.Flush(); }
void Frame(string json) => Raw($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");
void Reply(int sequence, bool success = true, string message = "") => Frame($$"""{"type":"response","request_seq":{{sequence}},"success":{{(success ? "true" : "false")}},"message":"{{message}}"}""");
void Answer(int sequence, string body) => Frame($$"""{"type":"response","request_seq":{{sequence}},"success":true,"body":{{body}}}""");
void Event(string name) => Frame($$"""{"type":"event","event":"{{name}}"}""");
void EventWith(string name, string body) => Frame($$"""{"type":"event","event":"{{name}}","body":{{body}}}""");

while (ReadFrame(input) is { } request)
{
    var sequence = request.GetProperty("seq").GetInt32();
    var command = request.GetProperty("command").GetString();
    if (Canned(scenario, command, request) is { } canned)
    {
        // The session sets breakpoints once while starting, so the view model's first apply is the second of these.
        if (command == "setBreakpoints") applied++;
        if (scenario == "debug-change-before-answer" && command == "setBreakpoints" && applied == 2) EventWith("breakpoint", BreakpointMovedAway);
        if (scenario == "debug-change-then-terminate" && command == "setBreakpoints" && applied == 3)
        {
            EventWith("breakpoint", BreakpointMovedAway);
            Event("terminated");
            return;
        }
        Answer(sequence, canned);
        if (scenario == "debug-change-after-answer" && command == "setBreakpoints" && applied == 3) EventWith("breakpoint", BreakpointMovedAway);
        // On request, so a test can wait until the view model holds the adapter's id first.
        if (scenario == "debug-events" && command == "evaluate" && request.GetProperty("arguments").GetProperty("expression").GetString() == "send-breakpoint-change")
            EventWith("breakpoint", BreakpointMovedAway);
        continue;
    }
    switch (scenario, command)
    {
        case ("bad-header", "initialize"):
            Reply(sequence);
            Raw("Content-Length: nope\r\n\r\n");
            Thread.Sleep(Timeout.Infinite);
            break;
        case ("missing-fields", "initialize"):
            Reply(sequence);
            foreach (var frame in new[] { "{\"seq\":9}", "[1]", "{\"type\":5}", "{\"type\":\"response\"}", "{\"type\":\"response\",\"request_seq\":\"one\"}", "{\"type\":\"event\"}" }) Frame(frame);
            Event("initialized");
            break;
        case ("reject-attach", "attach"):
            Reply(sequence, success: false, message: "attach rejected");
            break;
        case ("ignore-disconnect", "disconnect"):
            Thread.Sleep(Timeout.Infinite);
            break;
        case ("terminated", "configurationDone"):
            Reply(sequence);
            Event("terminated");
            return;
        case ("crash", "configurationDone"):
            Reply(sequence);
            return;
        case ("debug-step", "configurationDone"):
            Reply(sequence);
            EventWith("stopped", """{"reason":"step","threadId":7}""");
            break;
        // Echoes the launch options, then loads, changes and unloads modules as netcoredbg reports them.
        case ("debug-modules", "launch"):
        {
            var options = request.GetProperty("arguments");
            EventWith("output", $$"""{"category":"console","output":"launch justMyCode={{options.GetProperty("justMyCode").GetBoolean()}} enableStepFiltering={{options.GetProperty("enableStepFiltering").GetBoolean()}} stopAtEntry={{options.GetProperty("stopAtEntry").GetBoolean()}}\n"}""");
            Reply(sequence);
            break;
        }
        case ("debug-modules", "configurationDone"):
            Reply(sequence);
            EventWith("module", """{"reason":"new","module":{"id":"m1","name":"App.dll","path":"C:\\src\\bin\\App.dll","symbolStatus":"Symbols loaded."}}""");
            EventWith("module", """{"reason":"new","module":{"id":"m2","name":"System.Private.CoreLib.dll","path":"C:\\dotnet\\System.Private.CoreLib.dll","symbolStatus":"Skipped loading symbols."}}""");
            EventWith("module", """{"reason":"new","module":{"id":"m3","name":"Plugin.dll","path":"C:\\src\\bin\\Plugin.dll","symbolStatus":"Symbols not found."}}""");
            EventWith("module", """{"reason":"changed","module":{"id":"m3","symbolStatus":"Symbols loaded."}}""");
            EventWith("module", """{"reason":"removed","module":{"id":"m2"}}""");
            EventWith("module", """{"reason":"new","module":{"name":"NoId.dll"}}""");
            break;
        case ("debug-exception", "configurationDone"):
            Reply(sequence);
            EventWith("stopped", """{"reason":"exception","threadId":7}""");
            break;
        case ("debug-breakpoint", "configurationDone"):
            Reply(sequence);
            EventWith("stopped", """{"reason":"breakpoint","threadId":7,"hitBreakpointIds":[100]}""");
            break;
        case ("debug-breakpoint", "continue"):
            Reply(sequence);
            EventWith("stopped", ++continues == 1 ? """{"reason":"breakpoint","threadId":7,"hitBreakpointIds":[100]}""" : """{"reason":"step","threadId":7}""");
            break;
        case ("debug-events", "configurationDone"):
            Reply(sequence);
            EventWith("output", """{"category":"stdout","output":"hello from the debuggee\n"}""");
            EventWith("stopped", """{"reason":"pause","threadId":7}""");
            break;
        // On request: a session that ends while it is still starting races the view model's own start-up, and that is not what these tests check.
        case ("debug-terminate", "pause"):
            Reply(sequence);
            Event("terminated");
            return;
        // The event goes first, so the view model always handles it before attach or launch returns to it.
        case ("debug-terminate-starting", "configurationDone"):
            Event("terminated");
            Reply(sequence);
            return;
        case ("partial", "configurationDone"):
            Reply(sequence);
            EventWith("stopped", """{"threadId":7}""");
            break;
        case ("malformed", "configurationDone"):
            Reply(sequence);
            EventWith("breakpoint", """{"breakpoint":[1]}""");
            EventWith("stopped", """{"reason":"exception","threadId":"seven","hitBreakpointIds":"none"}""");
            break;
        case ("no-body-events", "configurationDone"):
            Reply(sequence);
            Event("breakpoint");
            Event("stopped");
            break;
        case ("refuse-queries", "evaluate"):
            Reply(sequence, success: false, message: "cannot evaluate");
            break;
        default:
            Reply(sequence);
            if (command == "initialize" && scenario != "reject-attach") Event("initialized");
            break;
    }
}

// Answers with a body. The "partial" adapter leaves out every field it can, and "malformed" sends the wrong JSON kinds; neither is a protocol failure the session should end over.
static string? Canned(string? scenario, string? command, JsonElement request) => (scenario, command) switch
{
    ("partial", "threads") => """{"threads":[{"id":7}]}""",
    ("partial", "stackTrace") => """{"stackFrames":[{"id":1}]}""",
    ("partial", "scopes") => """{"scopes":[{}]}""",
    ("partial", "variables") => """{"variables":[{"name":"x"}]}""",
    ("partial", "evaluate") => "{}",
    ("partial", "exceptionInfo") => "{}",
    ("partial", "setBreakpoints") => """{"breakpoints":[{}]}""",
    ("malformed", "threads") => """{"threads":[1,"x",null,{"id":"seven","name":9},{"id":7,"name":"Main"}]}""",
    ("malformed", "stackTrace") => """{"stackFrames":{"id":1}}""",
    ("malformed", "scopes") => """{"scopes":"none"}""",
    ("malformed", "variables") => """{"variables":[null,{"name":{"nested":true},"value":5,"type":false,"variablesReference":"many"}]}""",
    ("malformed", "evaluate") => """{"result":12,"type":null,"variablesReference":1.5}""",
    ("malformed", "exceptionInfo") => """[1,2]""",
    ("malformed", "setBreakpoints") => """{"breakpoints":[7,{"id":"x","verified":"yes","line":"ten"}]}""",
    (_, "threads") when IsDebug(scenario) => """{"threads":[{"id":7,"name":"Main Thread"},{"id":8,"name":"Worker"}]}""",
    (_, "stackTrace") when IsDebug(scenario) => """{"stackFrames":[{"id":1,"name":"Program.Main","line":12,"source":{"path":"C:\\src\\Program.cs"}},{"id":2,"name":"[External Code]","line":0}]}""",
    (_, "scopes") when IsDebug(scenario) => """{"scopes":[{"name":"Locals","variablesReference":1000},{"name":"Empty","variablesReference":0}]}""",
    (_, "variables") when IsDebug(scenario) => Variables(request.GetProperty("arguments")),
    (_, "evaluate") when IsDebug(scenario) => $$"""{"result":"value of {{request.GetProperty("arguments").GetProperty("expression").GetString()}}","type":"int","variablesReference":0}""",
    (_, "exceptionInfo") when IsDebug(scenario) => """{"exceptionId":"System.Exception","description":"boom","details":{"message":"boom"}}""",
    (_, "setBreakpoints") when IsDebug(scenario) => $$"""{"breakpoints":[{{string.Join(',', request.GetProperty("arguments").GetProperty("breakpoints").EnumerateArray().Select((item, index) => $$"""{"id":{{100 + index}},"verified":true,"line":{{item.GetProperty("line").GetInt32() + 1}}}"""))}}]}""",
    _ => null
};

static bool IsDebug(string? scenario) => scenario?.StartsWith("debug-", StringComparison.Ordinal) == true;

static string Variables(JsonElement arguments)
{
    var reference = arguments.GetProperty("variablesReference").GetInt32();
    if (reference == 1000) return """{"variables":[{"name":"answer","value":"42","type":"int","variablesReference":0},{"name":"items","value":"{List}","type":"List<int>","variablesReference":2000}]}""";
    if (reference != 2000) return """{"variables":[]}""";
    var start = arguments.GetProperty("start").GetInt32();
    var count = Math.Min(arguments.GetProperty("count").GetInt32(), 205 - start);
    return $$"""{"variables":[{{string.Join(',', Enumerable.Range(start, Math.Max(0, count)).Select(index => $$"""{"name":"[{{index}}]","value":"{{index * 2}}","type":"int","variablesReference":0}"""))}}]}""";
}

static JsonElement? ReadFrame(Stream stream)
{
    var header = new StringBuilder();
    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
    {
        var value = stream.ReadByte();
        if (value < 0) return null;
        header.Append((char)value);
    }
    var length = int.Parse(header.ToString().Split("\r\n")[0]["Content-Length:".Length..].Trim());
    var body = new byte[length];
    stream.ReadExactly(body);
    return JsonDocument.Parse(body).RootElement.Clone();
}
