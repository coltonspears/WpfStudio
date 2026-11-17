using System.Net;
using System.Text;

namespace WpfStudio.Core.Tests.DevOps;

/// <summary>A request the provider sent, as the fake service saw it.</summary>
internal sealed record RecordedRequest(string Method, string Url, string? Body, string? Authorization, string? Accept, string? ContentType)
{
    public Uri Uri => new(Url);
    /// <summary>One query parameter, decoded; null when absent.</summary>
    public string? Query(string name) => Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2)).Where(pair => Uri.UnescapeDataString(pair[0]) == name)
        .Select(pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "").FirstOrDefault();
}

/// <summary>
/// A scripted HTTP service for provider tests. Routes match by method and by a fragment of the URL (path and query), first
/// registered first; a request no route matches is answered 404 and recorded like the rest.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<(HttpMethod? Method, string Fragment, Func<RecordedRequest, HttpResponseMessage> Respond)> _routes = [];
    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttp On(HttpMethod? method, string fragment, int status, string body = "", string contentType = "application/json",
        IReadOnlyDictionary<string, string>? headers = null) =>
        On(method, fragment, _ => Response(status, body, contentType, headers));

    public FakeHttp On(HttpMethod? method, string fragment, Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _routes.Add((method, fragment, respond));
        return this;
    }

    public FakeHttp Json(string fragment, string body) => On(HttpMethod.Get, fragment, 200, body);

    public HttpClient Client() => new(this, disposeHandler: false);

    public RecordedRequest Single(HttpMethod method, string fragment) =>
        Assert.Single(Requests, request => request.Method == method.Method && request.Url.Contains(fragment, StringComparison.Ordinal));

    public static HttpResponseMessage Response(int status, string body = "", string contentType = "application/json", IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            if (!response.Headers.TryAddWithoutValidation(name, value)) response.Content.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, body, request.Headers.Authorization?.ToString(),
            request.Headers.Accept.ToString(), request.Content?.Headers.ContentType?.MediaType);
        Requests.Add(recorded);
        var path = request.RequestUri.PathAndQuery;
        foreach (var (method, fragment, respond) in _routes)
            if ((method is null || method == request.Method) && (path.Contains(fragment, StringComparison.Ordinal) || recorded.Url.Contains(fragment, StringComparison.Ordinal)))
                return respond(recorded);
        return Response(404, "{\"message\":\"No route in the fake service.\"}");
    }
}
