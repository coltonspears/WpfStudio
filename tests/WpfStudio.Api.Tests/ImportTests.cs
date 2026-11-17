using System.Text.Json.Nodes;
using WpfStudio.Api.Import;
using WpfStudio.Api.Models;
using WpfStudio.Api.Storage;

namespace WpfStudio.Api.Tests;

public sealed class ImportTests
{
    private const string OpenApiYaml = """
        openapi: 3.1.0
        info:
          title: Pet Store
          description: Pets for sale
          version: "1.0"
        servers:
          - url: https://{region}.example.com/v1
            variables:
              region: { default: eu }
        security:
          - bearer: []
        tags:
          - name: pets
            description: Everything about pets
        paths:
          /pets/{petId}:
            parameters:
              - name: petId
                in: path
                required: true
                schema: { type: integer, example: 7 }
            get:
              tags: [pets]
              summary: Find a pet
              operationId: getPet
              parameters:
                - name: verbose
                  in: query
                  schema: { type: boolean, default: false }
                - name: X-Request-Id
                  in: header
                  required: true
                  schema: { type: string }
              responses:
                "200":
                  description: The pet
                  content:
                    application/json:
                      schema: { $ref: '#/components/schemas/Pet' }
            put:
              tags: [pets]
              operationId: updatePet
              security: []
              requestBody:
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/Pet' }
              responses:
                default: { description: done }
          /health:
            get:
              summary: Health
              security:
                - key: []
              responses:
                "200": { description: fine }
        components:
          securitySchemes:
            bearer: { type: http, scheme: bearer }
            key: { type: apiKey, in: header, name: X-Api-Key }
          schemas:
            Pet:
              type: object
              required: [name]
              properties:
                id: { type: integer, readOnly: true }
                name: { type: string, example: Rex }
                tags: { type: array, items: { type: string } }
                status: { type: string, enum: [available, sold] }
                parent: { $ref: '#/components/schemas/Pet' }
        """;

    [Fact]
    public void AnOpenapiYamlDocumentBecomesACollectionWithAFolderPerTag()
    {
        var result = ApiImporter.Import(OpenApiYaml, "petstore.yaml");
        var collection = result.Collection!;
        Assert.Equal("OpenAPI 3.1.0", result.Format);
        Assert.Equal("Pet Store", collection.Name);
        Assert.Equal("https://eu.example.com/v1", collection.Variables.Single(variable => variable.Key == "baseUrl").Value);
        Assert.Equal(AuthKind.Bearer, collection.Auth.Kind);
        Assert.Equal("{{bearerToken}}", collection.Auth.Token);
        Assert.True(collection.Variables.Single(variable => variable.Key == "bearerToken").Secret);

        var folder = Assert.Single(collection.Folders);
        Assert.Equal("pets", folder.Name);
        Assert.Equal("Everything about pets", folder.Description);
        var get = folder.Requests.Single(request => request.Method == "GET");
        Assert.Equal("Find a pet", get.Name);
        Assert.Equal("{{baseUrl}}/pets/{petId}", get.Url);
        Assert.Equal("7", Assert.Single(get.PathParameters).Value);
        var verbose = Assert.Single(get.Query);
        Assert.Equal(("verbose", "false", false), (verbose.Key, verbose.Value, verbose.Enabled));
        Assert.True(Assert.Single(get.Headers).Enabled);
        Assert.Equal("Pet", get.ResponseSchema);

        var put = folder.Requests.Single(request => request.Method == "PUT");
        Assert.Equal("updatePet", put.Name);
        Assert.Equal(AuthKind.None, put.Auth.Kind);
        Assert.Equal(BodyMode.Json, put.BodyMode);
        var body = JsonNode.Parse(put.Body)!.AsObject();
        // Read-only properties are the server's to fill in; the self-reference stops instead of recursing forever.
        Assert.False(body.ContainsKey("id"));
        Assert.Equal("Rex", body["name"]!.GetValue<string>());
        Assert.Equal("available", body["status"]!.GetValue<string>());
        Assert.Equal("Pet", put.RequestSchema);

        var health = Assert.Single(collection.Requests);
        Assert.Equal(AuthKind.ApiKey, health.Auth.Kind);
        Assert.Equal("X-Api-Key", health.Auth.KeyName);
        Assert.Equal("{{apiKey}}", health.Auth.KeyValue);
        Assert.NotNull(StructuredText.Parse(result.Specification!)["components"]);
    }

    [Fact]
    public void ASwagger2DocumentKeepsItsHostBodyAndFormParameters()
    {
        const string swagger = """
            {
              "swagger": "2.0",
              "info": { "title": "Old API", "version": "1" },
              "host": "api.example.com",
              "basePath": "/v2/",
              "schemes": ["http", "https"],
              "securityDefinitions": { "key": { "type": "apiKey", "in": "query", "name": "api_key" } },
              "security": [ { "key": [] } ],
              "paths": {
                "/pets": {
                  "post": {
                    "parameters": [ { "in": "body", "name": "body", "schema": { "$ref": "#/definitions/Pet" } } ],
                    "responses": { "200": { "description": "ok", "schema": { "type": "array", "items": { "$ref": "#/definitions/Pet" } } } }
                  }
                },
                "/pets/{id}/photo": {
                  "post": {
                    "consumes": ["multipart/form-data"],
                    "parameters": [
                      { "in": "path", "name": "id", "type": "integer", "required": true },
                      { "in": "formData", "name": "note", "type": "string", "default": "hi" },
                      { "in": "formData", "name": "file", "type": "file" }
                    ],
                    "responses": {}
                  }
                }
              },
              "definitions": { "Pet": { "type": "object", "properties": { "name": { "type": "string" } } } }
            }
            """;
        var collection = ApiImporter.Import(swagger, "https://example.com/swagger.json").Collection!;
        Assert.Equal("https://api.example.com/v2", collection.Variables.Single(variable => variable.Key == "baseUrl").Value);
        Assert.Equal((AuthKind.ApiKey, ApiKeyLocation.Query, "api_key"), (collection.Auth.Kind, collection.Auth.KeyLocation, collection.Auth.KeyName));
        var create = collection.Requests.Single(request => request.Url.EndsWith("/pets", StringComparison.Ordinal));
        Assert.Equal(BodyMode.Json, create.BodyMode);
        Assert.Equal("string", JsonNode.Parse(create.Body)!["name"]!.GetValue<string>());
        Assert.Equal("Pet[]", create.ResponseSchema);
        var upload = collection.Requests.Single(request => request.Url.EndsWith("/photo", StringComparison.Ordinal));
        Assert.Equal(BodyMode.Multipart, upload.BodyMode);
        Assert.Equal(["note=hi:False", "file=:True"], upload.Form.Select(row => $"{row.Key}={row.Value}:{row.IsFile}"));
    }

    [Fact]
    public void APostmanCollectionAndEnvironmentComeAcross()
    {
        const string postman = """
            {
              "info": { "name": "Shop", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "variable": [ { "key": "baseUrl", "value": "https://shop.example.com" } ],
              "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}", "type": "string" } ] },
              "item": [
                {
                  "name": "Orders",
                  "item": [
                    {
                      "name": "Get order",
                      "event": [ { "listen": "test", "script": { "exec": ["pm.test()"] } } ],
                      "request": {
                        "method": "GET",
                        "header": [ { "key": "Accept", "value": "application/json" }, { "key": "X-Off", "value": "1", "disabled": true } ],
                        "url": {
                          "raw": "{{baseUrl}}/orders/:orderId?expand=items",
                          "host": ["{{baseUrl}}"], "path": ["orders", ":orderId"],
                          "query": [ { "key": "expand", "value": "items" }, { "key": "debug", "value": "1", "disabled": true } ],
                          "variable": [ { "key": "orderId", "value": "42" } ]
                        }
                      }
                    }
                  ]
                },
                {
                  "name": "Create order",
                  "request": {
                    "method": "POST",
                    "auth": { "type": "noauth" },
                    "url": "{{baseUrl}}/orders",
                    "body": { "mode": "raw", "raw": "{\"id\":1}", "options": { "raw": { "language": "json" } } }
                  }
                }
              ]
            }
            """;
        var result = ApiImporter.Import(postman, "shop.postman_collection.json");
        var collection = result.Collection!;
        Assert.Equal("Postman v2.1", result.Format);
        Assert.Equal(AuthKind.Bearer, collection.Auth.Kind);
        var get = Assert.Single(Assert.Single(collection.Folders).Requests);
        Assert.Equal("{{baseUrl}}/orders/:orderId", get.Url);
        Assert.Equal("42", Assert.Single(get.PathParameters).Value);
        Assert.Equal(["expand:True", "debug:False"], get.Query.Select(row => $"{row.Key}:{row.Enabled}"));
        Assert.Equal(AuthKind.Inherit, get.Auth.Kind);
        Assert.False(get.Headers[1].Enabled);
        var post = Assert.Single(collection.Requests);
        Assert.Equal((BodyMode.Json, AuthKind.None), (post.BodyMode, post.Auth.Kind));
        Assert.Contains(result.Warnings, warning => warning.Contains("script", StringComparison.Ordinal));

        var environment = ApiImporter.Import("""{ "name": "Staging", "_postman_variable_scope": "environment", "values": [ { "key": "token", "value": "t", "type": "secret", "enabled": true } ] }""", "staging.json").Environment!;
        Assert.Equal("Staging", environment.Name);
        Assert.True(Assert.Single(environment.Variables).Secret);
    }

    [Fact]
    public void ExportsLeaveOutSecretsButKeepReferencesAndImportAgain()
    {
        var (variable, typed, user) = (TestSecrets.Make("variable"), TestSecrets.Make("typed"), TestSecrets.Make("user"));
        var collection = new ApiCollection
        {
            Name = "Shared",
            Variables = [new("baseUrl", "https://x"), new("token", variable) { Secret = true }],
            Auth = new ApiAuth { Kind = AuthKind.Bearer, Token = "{{token}}" },
            Requests = [new ApiRequest { Name = "Typed", Auth = new ApiAuth { Kind = AuthKind.Basic, UserName = user, Password = typed } }],
        };
        var exported = ApiImporter.Export(collection, """{"openapi":"3.0.0"}""");
        Assert.DoesNotContain(variable, exported, StringComparison.Ordinal);
        Assert.DoesNotContain(typed, exported, StringComparison.Ordinal);
        var imported = ApiImporter.Import(exported, "shared.json");
        Assert.Equal("{{token}}", imported.Collection!.Auth.Token);
        Assert.Equal(user, imported.Collection.Requests[0].Auth.UserName);
        Assert.NotEqual(collection.Id, imported.Collection.Id);
        Assert.True(imported.Collection.HasSpecification);
        Assert.Equal("""{"openapi":"3.0.0"}""", imported.Specification);
    }

    [Fact]
    public void AnImportedExportSharesNoIdsWithItsOriginal()
    {
        var collection = new ApiCollection
        {
            Folders = [new ApiFolder { Folders = [new ApiFolder { Requests = [new ApiRequest()] }], Requests = [new ApiRequest()] }],
            Requests = [new ApiRequest()],
        };
        static IEnumerable<string> Ids(ApiCollection item) => item.AllRequests().Select(entry => entry.Request.Id)
            .Concat(item.AllRequests().SelectMany(entry => entry.Path).Select(folder => folder.Id)).Append(item.Id).Distinct();
        var exported = ApiImporter.Export(collection, null);
        var first = ApiImporter.Import(exported, "a.json").Collection!;
        var second = ApiImporter.Import(exported, "a.json").Collection!;
        Assert.Equal(3, first.AllRequests().Count());
        Assert.Empty(Ids(first).Intersect(Ids(collection)));
        Assert.Empty(Ids(second).Intersect(Ids(first)));
    }

    [Fact]
    public void UnknownDocumentsAreRefusedWithAReason()
    {
        Assert.Throws<FormatException>(() => ApiImporter.Import("""{ "hello": "world" }""", "x.json"));
        var broken = Assert.Throws<FormatException>(() => ApiImporter.Import("openapi: [unclosed", "x.yaml"));
        Assert.Contains("YAML", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void YamlScalarsFollowTheCoreSchema()
    {
        var node = StructuredText.Parse("a: 1\nb: 1.5\nc: true\nd: ~\ne: '1'\nf: \"true\"\ng: 2024-01-01\nh: 0x1F\nbase: &base { x: 1 }\nderived:\n  <<: *base\n  y: 2\n");
        Assert.Equal(1, node["a"]!.GetValue<long>());
        Assert.Equal(1.5, node["b"]!.GetValue<double>());
        Assert.True(node["c"]!.GetValue<bool>());
        Assert.Null(node["d"]);
        Assert.Equal("1", node["e"]!.GetValue<string>());
        Assert.Equal("true", node["f"]!.GetValue<string>());
        Assert.Equal("2024-01-01", node["g"]!.GetValue<string>());
        Assert.Equal("0x1F", node["h"]!.GetValue<string>());
        Assert.Equal(1, node["derived"]!["x"]!.GetValue<long>());
        Assert.Equal(2, node["derived"]!["y"]!.GetValue<long>());
    }
}

public sealed class StoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wpfstudio-api-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Stands in for Windows data protection: visibly different on disk, reversible.</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string value) => new(value.Reverse().ToArray());
        public string Unprotect(string value) => value == "fail" ? throw new InvalidOperationException("wrong user") : new(value.Reverse().ToArray());
    }

    [Fact]
    public async Task SecretsAreProtectedOnDiskAndComeBackWhenLoaded()
    {
        var store = new ApiStore(_directory, new ReversingProtector());
        var (variable, typed, proxy, environment, user) = (TestSecrets.Make("variable"), TestSecrets.Make("typed"), TestSecrets.Make("proxy"), TestSecrets.Make("environment"), TestSecrets.Make("user"));
        var collection = new ApiCollection
        {
            Name = "Mine",
            Variables = [new("token", variable) { Secret = true }, new("plain", "visible")],
            Auth = new ApiAuth { Kind = AuthKind.Bearer, Token = "{{token}}" },
            Requests = [new ApiRequest { Auth = new ApiAuth { Kind = AuthKind.Basic, UserName = user, Password = typed }, Proxy = new ProxyOverride { Mode = ProxyOverrideMode.Custom, Password = proxy } }],
        };
        await store.SaveCollectionAsync(collection);
        await store.SaveSpecificationAsync(collection.Id, "{}");
        await store.SaveHistoryAsync([new HistoryEntry { Request = collection.Requests[0] }]);
        await store.SaveEnvironmentsAsync(new EnvironmentFile { Environments = [new ApiEnvironment { Variables = [new("key", environment) { Secret = true }] }] });

        var onDisk = string.Concat(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        foreach (var secret in new[] { variable, typed, proxy, environment }) Assert.DoesNotContain(secret, onDisk, StringComparison.Ordinal);
        Assert.Contains("visible", onDisk, StringComparison.Ordinal);
        Assert.Contains("{{token}}", onDisk, StringComparison.Ordinal);

        var reloaded = new ApiStore(_directory, new ReversingProtector());
        var loaded = Assert.Single(await reloaded.LoadCollectionsAsync());
        Assert.Equal(collection.Id, loaded.Id);
        Assert.True(loaded.HasSpecification);
        Assert.Equal(variable, loaded.Variables[0].Value);
        Assert.Equal(typed, loaded.Requests[0].Auth.Password);
        Assert.Equal(proxy, loaded.Requests[0].Proxy.Password);
        Assert.Equal(typed, Assert.Single(await reloaded.LoadHistoryAsync()).Request.Auth.Password);
        Assert.Equal(environment, (await reloaded.LoadEnvironmentsAsync()).Environments[0].Variables[0].Value);
        Assert.Empty(reloaded.Problems);

        await reloaded.DeleteCollectionAsync(collection.Id);
        Assert.Empty(await reloaded.LoadCollectionsAsync());
    }

    [Fact]
    public async Task ASecretAnotherUserProtectedIsClearedWithAnExplanation()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "collections"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "collections", "abc.json"), """{ "name": "X", "variables": [ { "key": "k", "value": "protected:fail", "secret": true } ] }""");
        var store = new ApiStore(_directory, new ReversingProtector());
        var loaded = Assert.Single(await store.LoadCollectionsAsync());
        Assert.Equal("", loaded.Variables[0].Value);
        Assert.Contains(store.Problems, problem => problem.Contains("another Windows user", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
