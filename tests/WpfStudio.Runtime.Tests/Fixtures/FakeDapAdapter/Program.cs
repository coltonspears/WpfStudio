using System.Text;
using System.Text.Json;

// A scripted debug adapter, so DebugSession's process handling can be tested without netcoredbg.
var scenario = Environment.GetEnvironmentVariable("WPFSTUDIO_FAKE_DAP_SCENARIO");
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();

void Raw(string text) { output.Write(Encoding.ASCII.GetBytes(text)); output.Flush(); }
void Frame(string json) => Raw($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");
void Reply(int sequence, bool success = true, string message = "") => Frame($$"""{"type":"response","request_seq":{{sequence}},"success":{{(success ? "true" : "false")}},"message":"{{message}}"}""");
void Event(string name) => Frame($$"""{"type":"event","event":"{{name}}"}""");

while (ReadFrame(input) is { } request)
{
    var sequence = request.GetProperty("seq").GetInt32();
    var command = request.GetProperty("command").GetString();
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
        default:
            Reply(sequence);
            if (command == "initialize" && scenario != "reject-attach") Event("initialized");
            break;
    }
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
