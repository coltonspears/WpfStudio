using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace WpfStudio.App.Features.ColtonGpt;

public sealed record AssistantMessage(string Role, string Content);
public sealed record AssistantModel(string Id, string Name, int ContextLength)
{
    public string Label => $"{Name} · {Id}";
}
public sealed record AssistantChunk(string Text, string? FinishReason = null);
public sealed class AssistantException(string message) : Exception(message);

/// <summary>OpenRouter transport. Never executes tools or writes model output to the workspace.</summary>
public sealed class OpenRouterClient(HttpClient http) : IDisposable
{
    private const int MaximumEventCharacters = 131_072;
    public const int MaximumResponseCharacters = 131_072;
    private static readonly Uri Endpoint = new("https://openrouter.ai/api/v1/");

    public async Task<IReadOnlyList<AssistantModel>> GetModelsAsync(string? apiKey = null, CancellationToken token = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "models", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        ThrowForStatus(response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new AssistantException("OpenRouter returned an invalid model catalog. You can enter a model ID manually.");
        return data.EnumerateArray().Where(IsTextModel).Select(item => new AssistantModel(
            item.GetProperty("id").GetString() ?? "",
            item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : item.GetProperty("id").GetString() ?? "",
            item.TryGetProperty("context_length", out var length) && length.TryGetInt32(out int count) ? count : 0))
            .Where(model => model.Id.Length > 0).OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async IAsyncEnumerable<AssistantChunk> StreamAsync(string apiKey, string model,
        IReadOnlyList<AssistantMessage> messages, int maxOutputTokens, double? temperature = null,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new AssistantException("Add an OpenRouter API key in ColtonGPT settings.");
        if (string.IsNullOrWhiteSpace(model)) throw new AssistantException("Choose an OpenRouter model in ColtonGPT settings.");
        using var request = CreateRequest(HttpMethod.Post, "chat/completions", apiKey);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model, ["stream"] = true, ["max_tokens"] = Math.Clamp(maxOutputTokens, 256, 16_384),
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray()
        };
        if (temperature is not null) payload["temperature"] = Math.Clamp(temperature.Value, 0, 2);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        ThrowForStatus(response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        int received = 0;
        bool finished = false;
        await foreach (string data in ReadEventsAsync(stream, token).ConfigureAwait(false))
        {
            if (data == "[DONE]") yield break;
            using var json = ParseEvent(data);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _))
                throw new AssistantException("The model provider interrupted the response. Partial text is preserved. Try again or choose another model.");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) continue;
            foreach (var choice in choices.EnumerateArray())
            {
                string? finish = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
                if (finish == "error") throw new AssistantException("The model provider could not finish this response. Try another model.");
                if (finish is not null) finished = true;
                string text = "";
                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
                    else if (delta.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String) text = refusal.GetString() ?? "";
                }
                received += text.Length;
                if (received > MaximumResponseCharacters)
                    throw new AssistantException("Response stopped at the 131,072-character limit. Ask for a smaller change or a shorter answer.");
                if (text.Length > 0 || finish is not null) yield return new AssistantChunk(text, finish);
            }
        }
        if (!finished) throw new AssistantException("The response connection closed unexpectedly. Partial text is preserved; you can send a follow-up.");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string? apiKey)
    {
        var request = new HttpRequestMessage(method, new Uri(Endpoint, path));
        request.Headers.TryAddWithoutValidation("X-Title", "ColtonGPT - WpfStudio");
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        return request;
    }

    private static bool IsTextModel(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return false;
        if (!item.TryGetProperty("architecture", out var architecture)) return true;
        return !architecture.TryGetProperty("output_modalities", out var modalities) || modalities.ValueKind != JsonValueKind.Array ||
               modalities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "text");
    }

    private static JsonDocument ParseEvent(string value)
    {
        try { return JsonDocument.Parse(value); }
        catch (JsonException) { throw new AssistantException("OpenRouter returned an invalid streaming response. Try again or choose another model."); }
    }

    public void Dispose() => http.Dispose();

    private static void ThrowForStatus(HttpStatusCode code)
    {
        if ((int)code is >= 200 and < 300) return;
        // Provider error bodies can echo request text or credentials; never place those bodies in UI or logs.
        throw new AssistantException(code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "OpenRouter rejected the API key. Check its validity and permissions in ColtonGPT settings.",
            HttpStatusCode.PaymentRequired => "OpenRouter reports insufficient credits. Add credits or select a model your account can use.",
            HttpStatusCode.TooManyRequests => "OpenRouter is rate limiting requests. Wait briefly, then send again.",
            HttpStatusCode.BadRequest => "OpenRouter rejected this request. Check the model ID, reduce context or output length, or disable custom temperature.",
            HttpStatusCode.NotFound => "This model or endpoint is unavailable. Refresh the model list in ColtonGPT settings.",
            _ => $"OpenRouter request failed (HTTP {(int)code}). Try again later or choose another model."
        });
    }

    private static async IAsyncEnumerable<string> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        char[] buffer = new char[2048];
        var line = new StringBuilder();
        var data = new StringBuilder();
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c != '\n')
                {
                    line.Append(c);
                    if (line.Length + data.Length > MaximumEventCharacters)
                        throw new AssistantException("OpenRouter returned an oversized streaming event. The request was stopped.");
                    continue;
                }
                string value = line.ToString().TrimEnd('\r');
                line.Clear();
                if (value.Length == 0)
                {
                    if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
                }
                else if (value.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value.AsSpan(value.Length > 5 && value[5] == ' ' ? 6 : 5));
                }
            }
        }
        if (line.Length > 0)
        {
            string value = line.ToString().TrimEnd('\r');
            if (value.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(value.AsSpan(value.Length > 5 && value[5] == ' ' ? 6 : 5));
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }
}
