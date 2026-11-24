using System.Text.Json.Nodes;
using WpfStudio.Api.CodeGeneration;
using WpfStudio.Api.Import;
using WpfStudio.Api.Models;
using WpfStudio.Api.Requests;
using WpfStudio.Core.Net;

namespace WpfStudio.Api.Tests;

public sealed class ApiClientHardeningTests
{
    [Fact]
    public async Task ClearCookiesWhileRequestIsInFlightDoesNotAbortInFlightRequest()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new TestServer((_, _) =>
        {
            arrived.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return TestServer.Response(200, "OK", "hello");
        });
        using var sender = new ApiSender(() => ProxyConfiguration.Direct);
        var request = new ApiRequest { Method = "GET", Url = server.Address + "/slow" };

        var inFlight = sender.SendAsync(request, null, null, CancellationToken.None);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sender.ClearCookies();
        release.TrySetResult();

        var response = await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("hello", response.Text);
    }

    [Fact]
    public async Task RedirectedCookiesAreQueriedAgainstFinalRequestUri()
    {
        await using var server = new TestServer((req, _) =>
        {
            if (req.Target == "/start")
                return TestServer.Response(302, "Found", "", "Location: /final/page");
            return TestServer.Response(200, "OK", "done", "Set-Cookie: sid=abc123; Path=/final");
        });
        using var sender = new ApiSender(() => ProxyConfiguration.Direct);
        var request = new ApiRequest
        {
            Method = "GET",
            Url = server.Address + "/start",
            Settings = new RequestSettings { FollowRedirects = true, UseCookies = true },
        };

        var response = await sender.SendAsync(request, null, null, CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(server.Address + "/final/page", response.Address?.AbsoluteUri);
        var cookie = Assert.Single(response.Cookies);
        Assert.Equal("sid", cookie.Key);
        Assert.Equal("abc123", cookie.Value);
    }

    [Fact]
    public void RequestUrlDoesNotTreatQueryOrFragmentPlaceholdersAsPathParameters()
    {
        const string address = "https://api.example.com/pets/{id}/:action?filter={tag}&kind=:type#sec/{frag}/:tail";

        Assert.Equal(["id", "action"], RequestUrl.PathParameterNames(address));

        var filled = RequestUrl.FillPathParameters(
            address,
            [
                new KeyValue("id", "42"),
                new KeyValue("action", "feed"),
                new KeyValue("tag", "SHOULD_NOT_REPLACE"),
                new KeyValue("type", "SHOULD_NOT_REPLACE"),
                new KeyValue("frag", "SHOULD_NOT_REPLACE"),
                new KeyValue("tail", "SHOULD_NOT_REPLACE"),
            ],
            text => text);

        Assert.Equal("https://api.example.com/pets/42/feed?filter={tag}&kind=:type#sec/{frag}/:tail", filled);
    }

    [Fact]
    public void ResolveAddressInsertsQueryParametersBeforeFragment()
    {
        var withoutExistingQuery = new ApiRequest
        {
            Url = "https://api.example.com/items#section",
            Query = [new KeyValue("limit", "10")],
            Auth = new ApiAuth { Kind = AuthKind.ApiKey, KeyLocation = ApiKeyLocation.Query, KeyName = "api_key", KeyValue = "secret" },
        };

        var uri1 = ApiRequestBuilder.ResolveAddress(withoutExistingQuery, null, VariableResolver.Empty);
        Assert.Equal("https://api.example.com/items?limit=10&api_key=secret#section", uri1.AbsoluteUri);

        var withExistingQuery = new ApiRequest
        {
            Url = "https://api.example.com/items?page=1#section",
            Query = [new KeyValue("limit", "10")],
        };

        var uri2 = ApiRequestBuilder.ResolveAddress(withExistingQuery, null, VariableResolver.Empty);
        Assert.Equal("https://api.example.com/items?page=1&limit=10#section", uri2.AbsoluteUri);
    }

    [Fact]
    public void MultipartBuildFailureDisposesEarlierOpenedFileStreams()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "WpfStudio-multipart-" + Guid.NewGuid().ToString("N") + ".txt");
        var missingFile = Path.Combine(Path.GetTempPath(), "WpfStudio-missing-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tempFile, "payload");
        try
        {
            var request = new ApiRequest
            {
                Method = "POST",
                Url = "https://api.example.com/upload",
                BodyMode = BodyMode.Multipart,
                Form =
                [
                    new KeyValue("first", tempFile) { IsFile = true },
                    new KeyValue("second", missingFile) { IsFile = true },
                ],
            };

            Assert.Throws<ApiRequestException>(() => ApiRequestBuilder.Build(request, null, VariableResolver.Empty));

            // If the first file's StreamContent was disposed properly, we can open it exclusively and delete it.
            using (var exclusive = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(exclusive.Length > 0);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void EncodeUrlComponentHandlesUnpairedHighSurrogateAndValidSurrogatePair()
    {
        // Unpaired high surrogate followed by a 3-byte UTF-8 character previously overflowed the 4-byte buffer.
        var unpaired = VariableResolver.EncodeUrlComponent("\uD83D\u4E2D");
        Assert.Equal("%EF%BF%BD%E4%B8%AD", unpaired);

        var validPair = VariableResolver.EncodeUrlComponent("\U0001F600");
        Assert.Equal("%F0%9F%98%80", validPair);
    }

    [Fact]
    public void SchemaTypeBuilderHandlesCyclicAllOfWithoutStackOverflow()
    {
        var doc = JsonNode.Parse("""
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "Loop": {
                    "allOf": [
                      { "$ref": "#/components/schemas/Loop" }
                    ]
                  }
                }
              }
            }
            """)!;

        var graph = SchemaTypeBuilder.FromReferences(doc, ["#/components/schemas/Loop"], new CSharpGeneratorOptions());
        Assert.NotNull(graph);
    }

    [Fact]
    public void PostmanImporterToleratesDuplicateAndMissingUrlVariableKeys()
    {
        var doc = JsonNode.Parse("""
            {
              "info": {
                "name": "DupVars",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
              },
              "item": [
                {
                  "name": "GetItem",
                  "request": {
                    "method": "GET",
                    "url": {
                      "raw": "https://api.example.com/items/:id",
                      "variable": [
                        { "value": "missing-key-1" },
                        { "value": "missing-key-2" },
                        { "key": "id", "value": "first" },
                        { "key": "id", "value": "second" }
                      ]
                    }
                  }
                }
              ]
            }
            """)!;

        var (collection, _) = PostmanImporter.ImportCollection(doc, "test.json");
        var request = Assert.Single(collection.Requests);
        var param = Assert.Single(request.PathParameters);
        Assert.Equal("id", param.Key);
        Assert.Equal("second", param.Value);
    }

    [Fact]
    public void SchemaExamplesTypeOfHandlesNonStringEntriesInTypeArray()
    {
        var mixed = JsonNode.Parse("""{ "type": [123, "string"] }""")!;
        Assert.Equal("string", SchemaExamples.TypeOf(mixed));

        var onlyNullAndBool = JsonNode.Parse("""{ "type": [true, "null"] }""")!;
        Assert.Equal("null", SchemaExamples.TypeOf(onlyNullAndBool));

        var onlyNumbers = JsonNode.Parse("""{ "type": [123, 456] }""")!;
        Assert.Null(SchemaExamples.TypeOf(onlyNumbers));
    }

    [Fact]
    public void OpenApiImporterHandlesMalformedRelativeServerUrlWithoutThrowing()
    {
        var doc = JsonNode.Parse("""
            {
              "openapi": "3.0.3",
              "info": { "title": "BadServer", "version": "1.0" },
              "servers": [ { "url": "http://[invalid-host" } ],
              "paths": {
                "/ping": { "get": { "responses": { "200": { "description": "ok" } } } }
              }
            }
            """)!;

        var result = OpenApiImporter.Import(doc, "https://docs.example.com/openapi.json");
        var baseUrl = result.Collection.Variables.Single(v => v.Key == "baseUrl");
        Assert.Equal("https://docs.example.com", baseUrl.Value);
        Assert.Contains(result.Warnings, warning => warning.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StructuredTextRejectsYamlAliasBomb()
    {
        var yaml = """
            a: &a ["x", "x", "x", "x", "x", "x", "x", "x", "x", "x"]
            b: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a, *a]
            c: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b, *b]
            d: &d [*c, *c, *c, *c, *c, *c, *c, *c, *c, *c]
            e: &e [*d, *d, *d, *d, *d, *d, *d, *d, *d, *d]
            f: &f [*e, *e, *e, *e, *e, *e, *e, *e, *e, *e]
            """;

        Assert.Throws<FormatException>(() => StructuredText.Parse(yaml));
    }

    [Fact]
    public void CSharpNamesLiteralEscapesLineAndParagraphSeparators()
    {
        var literal = CSharpNames.Literal("a\u2028b\u2029c");
        Assert.Equal("\"a\\u2028b\\u2029c\"", literal);

        var graph = new TypeGraph();
        var model = new ClassModel { Key = "Doc", Name = "Doc" };
        model.Properties.Add(new PropertyModel { JsonName = "a\u2028b\u2029c", Type = new PrimitiveRef(PrimitiveKind.String) });
        graph.Classes.Add(model);
        graph.Roots.Add(new NamedRef("Doc"));

        var generated = CSharpCodeGenerator.Generate(graph, new CSharpGeneratorOptions { Version = CSharpVersion.CSharp11 });
        _ = GeneratedCodeCompiler.Compile(generated, CSharpVersion.CSharp11);
    }

    [Fact]
    public void CSharpCodeGeneratorDeduplicatesToJsonHelpersWhenNullableIsDisabled()
    {
        var graph = new TypeGraph();
        var pet = new ClassModel { Key = "Pet", Name = "Pet" };
        pet.Properties.Add(new PropertyModel { JsonName = "name", Type = new PrimitiveRef(PrimitiveKind.String) });
        graph.Classes.Add(pet);
        graph.Roots.Add(new ArrayRef(new NamedRef("Pet"), ItemNullable: false));
        graph.Roots.Add(new ArrayRef(new NamedRef("Pet"), ItemNullable: true));

        var options = new CSharpGeneratorOptions
        {
            Features = OutputFeatures.Complete,
            Version = CSharpVersion.CSharp6,
        };
        Assert.False(options.Nullable);
        var generated = CSharpCodeGenerator.Generate(graph, options);
        _ = GeneratedCodeCompiler.Compile(generated, CSharpVersion.CSharp6);
    }
}
