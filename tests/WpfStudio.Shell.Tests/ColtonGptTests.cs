using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WpfStudio.App.Features.ColtonGpt;

namespace WpfStudio.Shell.Tests;

public sealed class ColtonGptTests
{
    [Fact]
    public async Task StreamUsesOpenRouterAndHandlesCommentsUnicodeAndMultilineEvents()
    {
        string? body = null;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("ColtonGPT - WpfStudio", Assert.Single(request.Headers.GetValues("X-Title")));
            body = await request.Content!.ReadAsStringAsync(token);
            return Sse(": OPENROUTER PROCESSING\r\n\r\ndata: {\r\ndata: \"choices\":[{\"delta\":{\"content\":\"Hello 世界\"}}]}\r\n\r\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"!\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        }));
        var chunks = new List<AssistantChunk>();
        await foreach (var chunk in new OpenRouterClient(http).StreamAsync("test-key", "test/model", [new("user", "Hi")], 1024)) chunks.Add(chunk);
        Assert.Equal("Hello 世界!", string.Concat(chunks.Select(chunk => chunk.Text)));
        using var json = JsonDocument.Parse(body!);
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("test/model", json.RootElement.GetProperty("model").GetString());
        Assert.False(json.RootElement.TryGetProperty("tools", out _));
        Assert.False(json.RootElement.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.PaymentRequired, "credits")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limiting")]
    [InlineData(HttpStatusCode.BadRequest, "model ID")]
    public async Task ApiErrorsAreUsefulAndNeverEchoRemoteSecrets(HttpStatusCode status, string expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("private-user-code test-key") })));
        var error = await Assert.ThrowsAsync<AssistantException>(async () =>
        {
            await foreach (var _ in new OpenRouterClient(http).StreamAsync("test-key", "test/model", [new("user", "private-user-code")], 1024)) { }
        });
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("private-user-code", error.Message);
        Assert.DoesNotContain("test-key", error.Message);
    }

    [Fact]
    public async Task MidstreamErrorPreservesPriorChunksAndReportsFailure()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"First\"}}]}\n\n" +
            "data: {\"error\":{\"message\":\"secret code\"},\"choices\":[{\"finish_reason\":\"error\"}]}\n\n"))));
        var text = new StringBuilder();
        var error = await Assert.ThrowsAsync<AssistantException>(async () =>
        {
            await foreach (var chunk in new OpenRouterClient(http).StreamAsync("test-key", "test/model", [new("user", "Hi")], 1024)) text.Append(chunk.Text);
        });
        Assert.Equal("First", text.ToString());
        Assert.Contains("interrupted", error.Message);
        Assert.DoesNotContain("secret code", error.Message);
    }

    [Fact]
    public async Task PrematureConnectionEndIsNotReportedAsSuccess()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"))));
        var error = await Assert.ThrowsAsync<AssistantException>(async () =>
        {
            await foreach (var _ in new OpenRouterClient(http).StreamAsync("test-key", "test/model", [], 1024)) { }
        });
        Assert.Contains("closed unexpectedly", error.Message);
    }

    [Fact]
    public async Task StreamCancellationInterruptsResponseRead()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream(waiting)) })));
        using var cancel = new CancellationTokenSource();
        var read = Task.Run(async () =>
        {
            await foreach (var _ in new OpenRouterClient(http).StreamAsync("test-key", "test/model", [], 1024, token: cancel.Token)) { }
        });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task OversizedStreamingEventsAreBounded()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Sse("data: " + new string('x', 140_000)))));
        var error = await Assert.ThrowsAsync<AssistantException>(async () =>
        {
            await foreach (var _ in new OpenRouterClient(http).StreamAsync("test-key", "test/model", [], 1024)) { }
        });
        Assert.Contains("oversized", error.Message);
    }

    [Fact]
    public async Task ModelCatalogFiltersNonTextModels()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/models", request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"data":[{"id":"provider/text","name":"Text","context_length":32000,"architecture":{"output_modalities":["text"]}},
                {"id":"provider/image","name":"Image","architecture":{"output_modalities":["image"]}}]}
                """) });
        }));
        var model = Assert.Single(await new OpenRouterClient(http).GetModelsAsync());
        Assert.Equal("provider/text", model.Id);
        Assert.Equal(32000, model.ContextLength);
    }

    [Fact]
    public async Task SettingsPersistOnlyProtectedKeyAndCanRemoveIt()
    {
        using var fixture = new Fixture();
        var vm = fixture.Settings;
        vm.ModelId = "test/model"; vm.PendingApiKey = "sk-or-private-key";
        await vm.SaveCommand.ExecuteAsync(null);
        string saved = await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "coltongpt.json"));
        Assert.DoesNotContain("sk-or-private-key", saved);
        Assert.Equal("", vm.PendingApiKey);
        var restored = new AssistantSettingsViewModel(fixture.Store, fixture.Client);
        await restored.LoadAsync();
        Assert.Equal("sk-or-private-key", restored.GetApiKey());
        Assert.True(restored.IsReady);
        await restored.ClearKeyCommand.ExecuteAsync(null);
        Assert.Null(restored.GetApiKey());
        Assert.False(restored.IsReady);
        Assert.Null((await fixture.Store.LoadAsync()).ProtectedApiKey);
    }

    [Fact]
    public async Task CatalogUsesExplicitlyEnteredKeyWithoutPersistingIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WpfStudio-UnsavedKey-" + Guid.NewGuid().ToString("N"));
        int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.Equal("unsaved-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"test/model\",\"name\":\"Test\"}]}") });
        }));
        var settings = new AssistantSettingsViewModel(new AssistantSettingsStore(directory), new OpenRouterClient(http));
        await settings.RefreshModelsCommand.ExecuteAsync(null);
        Assert.Equal(0, requests);
        settings.PendingApiKey = "unsaved-key";
        await settings.RefreshModelsCommand.ExecuteAsync(null);
        Assert.Equal(1, requests);
        Assert.Single(settings.Models);
        Assert.False(File.Exists(Path.Combine(directory, "coltongpt.json")));
        Assert.False(settings.HasApiKey);
    }

    [Fact]
    public async Task AssistantMakesNoAutomaticCallsAndOnlySendsPreviewedOptInContext()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAsync();
        using var assistant = new AssistantViewModel(fixture.Client, fixture.Settings);
        await assistant.InitializeAsync();
        assistant.SetEditorContext("C:\\work\\View.xaml", "private file content", "selected text");
        assistant.Prompt = "Explain this.";
        Assert.Empty(fixture.Requests);
        Assert.DoesNotContain("private file content", assistant.RequestPreview);
        Assert.DoesNotContain("selected text", assistant.RequestPreview);
        assistant.ContextMode = "Selected text";
        string preview = assistant.RequestPreview;
        Assert.Contains("selected text", preview);
        Assert.DoesNotContain("private file content", preview);
        await assistant.SendCommand.ExecuteAsync(null);
        string body = Assert.Single(fixture.Requests);
        using var json = JsonDocument.Parse(body);
        string user = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("selected text", user);
        Assert.DoesNotContain("private file content", body);
        Assert.DoesNotContain("C:\\work", body);
        Assert.Equal(preview, assistant.LastRequestPreview);
        Assert.Contains("Hello", assistant.Messages.Last().Text);
        Assert.False(assistant.IsBusy);
    }

    [Fact]
    public async Task NewChatClearsHistoryAndEditorSharingOptIn()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAsync();
        using var assistant = new AssistantViewModel(fixture.Client, fixture.Settings);
        assistant.SetEditorContext("View.xaml", new string('a', 40_000), "selected");
        assistant.ContextMode = "Current document";
        Assert.Contains("truncated", assistant.ContextLabel);
        Assert.True(assistant.ContextPreview.Length < 24_100);
        assistant.Prompt = "Explain";
        await assistant.SendCommand.ExecuteAsync(null);
        assistant.ClearCommand.Execute(null);
        Assert.Empty(assistant.Messages);
        Assert.Equal("No editor context", assistant.ContextMode);
        Assert.Equal("", assistant.ContextPreview);
        Assert.DoesNotContain("Hello", assistant.RequestPreview);
    }

    [Fact]
    public async Task ConversationHistoryIsBoundedAndOversizedPromptsCannotSend()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAsync();
        using var assistant = new AssistantViewModel(fixture.Client, fixture.Settings);
        for (int i = 0; i < 9; i++)
        {
            assistant.Prompt = $"message {i}";
            await assistant.SendCommand.ExecuteAsync(null);
        }
        Assert.Equal(12, assistant.Messages.Count);
        Assert.DoesNotContain("message 0", assistant.RequestPreview);
        Assert.Contains("message 8", assistant.RequestPreview);
        assistant.Prompt = new string('x', AssistantViewModel.MaximumPromptCharacters + 1);
        Assert.False(assistant.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task InvalidSettingsDoNotOverwriteExistingProtectedKey()
    {
        using var fixture = new Fixture();
        await fixture.ConfigureAsync();
        fixture.Settings.PendingApiKey = "key\nwith-newline";
        await fixture.Settings.SaveCommand.ExecuteAsync(null);
        Assert.Contains("invalid characters", fixture.Settings.Status);
        Assert.Equal("test-key", fixture.Settings.GetApiKey());
    }

    private static HttpResponseMessage Sse(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "WpfStudio-ColtonGpt-" + Guid.NewGuid().ToString("N"));
        public List<string> Requests { get; } = [];
        private readonly HttpClient _http;
        public OpenRouterClient Client { get; }
        public AssistantSettingsStore Store { get; }
        public AssistantSettingsViewModel Settings { get; }
        public Fixture()
        {
            _http = new HttpClient(new Handler(async (request, token) =>
            {
                Requests.Add(await request.Content!.ReadAsStringAsync(token));
                return Sse("data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
            }));
            Client = new OpenRouterClient(_http); Store = new AssistantSettingsStore(Directory);
            Settings = new AssistantSettingsViewModel(Store, Client);
        }
        public async Task ConfigureAsync()
        {
            Settings.ModelId = "test/model"; Settings.PendingApiKey = "test-key";
            await Settings.SaveCommand.ExecuteAsync(null);
        }
        public void Dispose()
        {
            _http.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class WaitingStream(TaskCompletionSource waiting) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
