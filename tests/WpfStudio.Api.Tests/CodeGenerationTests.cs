using System.Text.Json.Nodes;
using WpfStudio.Api.CodeGeneration;
using WpfStudio.Api.Import;

namespace WpfStudio.Api.Tests;

public sealed class CodeGenerationTests
{
    private const string Sample = """
        {
          "id": 42,
          "name": "Rex",
          "price": 12.5,
          "tags": [ { "id": 1, "name": "good" }, { "id": 2, "name": "dog" } ],
          "createdAt": "2024-05-01T10:00:00+00:00",
          "birthday": "2020-02-29",
          "uuid": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
          "count": "17",
          "vaccinated": "true",
          "billingAddress": { "street": "1 Main", "city": "Springfield", "zip": "01234" },
          "shippingAddress": { "street": "2 Elm", "city": "Shelbyville", "zip": "05678" },
          "scores": { "123": 1.5, "456": 2.5 },
          "items": [ { "kind": "small" }, { "kind": "large" }, { "kind": "small" }, { "kind": "large" } ]
        }
        """;

    private const string PetStore = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Pets", "version": "1" },
          "paths": {},
          "components": {
            "schemas": {
              "Pet": {
                "type": "object",
                "description": "A pet <for sale> & more",
                "required": ["id", "name"],
                "properties": {
                  "id": { "type": "integer", "format": "int64" },
                  "name": { "type": "string" },
                  "status": { "$ref": "#/components/schemas/Status" },
                  "category": { "$ref": "#/components/schemas/Category" },
                  "photoUrls": { "type": "array", "items": { "type": "string", "format": "uri" } },
                  "born": { "type": "string", "format": "date" },
                  "weight": { "type": "number", "nullable": true },
                  "attributes": { "type": "object", "additionalProperties": { "type": "string" } },
                  "owner": { "oneOf": [ { "$ref": "#/components/schemas/Person" }, { "type": "null" } ] },
                  "extra": {},
                  "class": { "type": "string" },
                  "Pet": { "type": "string" }
                }
              },
              "Dog": { "allOf": [ { "$ref": "#/components/schemas/Pet" }, { "type": "object", "properties": { "barks": { "type": "boolean" } } } ] },
              "Category": { "type": "object", "properties": { "id": { "type": "integer", "format": "int32" }, "parent": { "$ref": "#/components/schemas/Category" } } },
              "Person": { "type": "object", "required": ["email"], "properties": { "email": { "type": "string" }, "id": { "type": "string", "format": "uuid" } } },
              "Status": { "type": "string", "enum": ["available", "on-hold", "sold", "2nd-hand"] },
              "Type": { "type": "object", "properties": { "value": { "type": "string" } } }
            }
          }
        }
        """;

    public static TheoryData<string> OptionSets() =>
    [
        "default", "newtonsoft", "csharp6", "csharp8", "csharp10", "records", "records-init-required", "dense", "attributes-only", "just-types", "just-types-namespace",
        "array", "readonly", "decimal", "dynamic", "json-dom", "json-dom-newtonsoft", "datetime", "no-dateonly", "keep-names", "virtual", "check-required",
        "check-required-newtonsoft", "initialize-collections", "file-per-type", "no-enums", "no-docs", "newtonsoft-csharp6",
    ];

    private static CSharpGeneratorOptions Options(string name)
    {
        var options = new CSharpGeneratorOptions { Namespace = "Generated.Models", FileName = "PetModels.cs" };
        switch (name)
        {
            case "newtonsoft": options.Framework = JsonFramework.NewtonsoftJson; break;
            case "csharp6": options.Version = CSharpVersion.CSharp6; break;
            case "csharp8": options.Version = CSharpVersion.CSharp8; break;
            case "csharp10": options.Version = CSharpVersion.CSharp10; break;
            case "records": options.UseRecords = true; break;
            case "records-init-required": options.UseRecords = true; options.InitOnlyProperties = true; options.RequiredMembers = true; break;
            case "dense": options.Density = PropertyDensity.Dense; break;
            case "attributes-only": options.Features = OutputFeatures.AttributesOnly; break;
            case "just-types": options.Features = OutputFeatures.JustTypes; break;
            case "just-types-namespace": options.Features = OutputFeatures.JustTypesAndNamespace; break;
            case "array": options.Arrays = ArrayStyle.Array; break;
            case "readonly": options.Arrays = ArrayStyle.ReadOnlyList; break;
            case "decimal": options.Numbers = NumberStyle.Decimal; break;
            case "dynamic": options.Any = AnyStyle.Dynamic; break;
            case "json-dom": options.Any = AnyStyle.JsonDom; break;
            case "json-dom-newtonsoft": options.Any = AnyStyle.JsonDom; options.Framework = JsonFramework.NewtonsoftJson; break;
            case "datetime": options.Dates = DateStyle.DateTime; break;
            case "no-dateonly": options.UseDateOnlyTimeOnly = false; break;
            case "keep-names": options.KeepPropertyNames = true; break;
            case "virtual": options.VirtualProperties = true; break;
            case "check-required": options.CheckRequired = true; break;
            case "check-required-newtonsoft": options.CheckRequired = true; options.Framework = JsonFramework.NewtonsoftJson; break;
            case "initialize-collections": options.InitializeCollections = true; break;
            case "file-per-type": options.Layout = FileLayout.FilePerType; break;
            case "no-enums": options.GenerateEnums = false; break;
            case "no-docs": options.DocumentationComments = false; break;
            case "newtonsoft-csharp6": options.Framework = JsonFramework.NewtonsoftJson; options.Version = CSharpVersion.CSharp6; break;
        }
        return options;
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void SchemasCompileCleanlyWithEveryOption(string optionSet)
    {
        var options = Options(optionSet);
        var document = StructuredText.Parse(PetStore);
        var references = SchemaTypeBuilder.List(document).Select(entry => entry.Reference).ToList();
        var code = CSharpCodeGenerator.Generate(SchemaTypeBuilder.FromReferences(document, references, options), options);
        GeneratedCodeCompiler.Compile(code, options.Version);
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void SamplesCompileCleanlyWithEveryOption(string optionSet)
    {
        var options = Options(optionSet);
        var code = CSharpCodeGenerator.Generate(JsonSampleInferrer.Infer([JsonNode.Parse(Sample)], options), options);
        GeneratedCodeCompiler.Compile(code, options.Version);
    }

    [Theory]
    [InlineData(JsonFramework.SystemTextJson)]
    [InlineData(JsonFramework.NewtonsoftJson)]
    public void ASampleSurvivesARoundTripThroughItsGeneratedTypes(JsonFramework framework)
    {
        var options = new CSharpGeneratorOptions { Framework = framework, Namespace = "RoundTrip", TopLevelName = "Pet", FileName = "Pet.cs" };
        var code = CSharpCodeGenerator.Generate(JsonSampleInferrer.Infer([JsonNode.Parse(Sample)], options), options);
        var assembly = GeneratedCodeCompiler.Compile(code, options.Version);
        var pet = GeneratedCodeCompiler.Type(assembly, "Pet");
        var value = GeneratedCodeCompiler.Call(pet, "FromJson", Sample);
        Assert.NotNull(value);
        var json = (string)GeneratedCodeCompiler.Call(GeneratedCodeCompiler.Type(assembly, "PetJson"), "ToJson", value)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Sample), JsonNode.Parse(json)), "Round trip changed the JSON:\n" + json + "\n\n" + code.Preview);
    }

    [Theory]
    [InlineData(JsonFramework.SystemTextJson)]
    [InlineData(JsonFramework.NewtonsoftJson)]
    public void NumbersAndBooleansWrittenAsStringsInsideListsAndDictionariesStillRoundTrip(JsonFramework framework)
    {
        // Their converters attach to a property, so a list's items and a dictionary's values stay strings rather than fail to read.
        const string sample = """{ "flags": ["true", "false", "true"], "counts": ["1", "22"], "byId": { "1": "10", "2": "20" }, "single": "5", "on": "false" }""";
        var options = new CSharpGeneratorOptions { Framework = framework, Namespace = "Encoded", TopLevelName = "Settings", FileName = "Settings.cs" };
        var graph = JsonSampleInferrer.Infer([JsonNode.Parse(sample)], options);
        var code = CSharpCodeGenerator.Generate(graph, options);
        var assembly = GeneratedCodeCompiler.Compile(code, options.Version);
        var value = GeneratedCodeCompiler.Call(GeneratedCodeCompiler.Type(assembly, "Settings"), "FromJson", sample);
        var json = (string)GeneratedCodeCompiler.Call(GeneratedCodeCompiler.Type(assembly, "SettingsJson"), "ToJson", value)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sample), JsonNode.Parse(json)), "Round trip changed the JSON:\n" + json + "\n\n" + code.Preview);
        Assert.Contains("List<string> Flags", code.Preview, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, string> ById", code.Preview, StringComparison.Ordinal);
        Assert.Contains("public long Single", code.Preview, StringComparison.Ordinal);
        Assert.Contains("public bool On", code.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredListInOnePartOfAnAllOfAppliesToPropertiesTheOtherPartsDeclare()
    {
        const string document = """
            {
              "openapi": "3.0.3", "info": { "title": "t", "version": "1" }, "paths": {},
              "components": { "schemas": {
                "Base": { "type": "object", "properties": { "id": { "type": "integer" }, "name": { "type": "string" }, "note": { "type": "string" } } },
                "Named": { "required": ["id"], "allOf": [ { "required": ["name"] }, { "$ref": "#/components/schemas/Base" } ] }
              } }
            }
            """;
        var options = new CSharpGeneratorOptions();
        var graph = SchemaTypeBuilder.FromReferences(StructuredText.Parse(document), ["#/components/schemas/Named", "#/components/schemas/Base"], options);
        var named = graph.Classes.Single(model => model.Name == "Named");
        Assert.True(named.Properties.Single(property => property.JsonName == "id").Required);
        Assert.True(named.Properties.Single(property => property.JsonName == "name").Required);
        Assert.False(named.Properties.Single(property => property.JsonName == "note").Required);
        // Base, generated as a class of its own, requires nothing: what Named requires stays with Named.
        Assert.All(graph.Classes.Single(model => model.Name == "Base").Properties, property => Assert.False(property.Required));
    }

    [Fact]
    public void InferenceFindsTheTypesAPersonWouldChoose()
    {
        var options = new CSharpGeneratorOptions { TopLevelName = "Pet" };
        var graph = JsonSampleInferrer.Infer([JsonNode.Parse(Sample)], options);
        var pet = graph.Classes.Single(model => model.Name == "Pet");
        TypeRef Type(string name) => pet.Properties.Single(property => property.JsonName == name).Type;
        Assert.Equal(new PrimitiveRef(PrimitiveKind.Long), Type("id"));
        Assert.Equal(new PrimitiveRef(PrimitiveKind.Number), Type("price"));
        Assert.Equal(new PrimitiveRef(PrimitiveKind.DateTime), Type("createdAt"));
        Assert.Equal(new PrimitiveRef(PrimitiveKind.Date), Type("birthday"));
        Assert.Equal(new PrimitiveRef(PrimitiveKind.Guid), Type("uuid"));
        Assert.Equal(new PrimitiveRef(PrimitiveKind.IntegerString), Type("count"));
        Assert.Equal(new PrimitiveRef(PrimitiveKind.BooleanString), Type("vaccinated"));
        // Numeric keys: a dictionary, not a class with properties named 123 and 456.
        Assert.Equal(new MapRef(new PrimitiveRef(PrimitiveKind.Number)), Type("scores"));
        // Billing and shipping addresses have the same properties, so they share one class, and a zip with a leading zero stays text.
        Assert.Equal(Type("billingAddress"), Type("shippingAddress"));
        var address = graph.Class(((NamedRef)Type("billingAddress")).Key)!;
        Assert.Equal("Address", address.Name);
        Assert.Equal(new PrimitiveRef(PrimitiveKind.String), address.Properties.Single(property => property.JsonName == "zip").Type);
        // Two words, each seen twice: an enum.
        var item = graph.Class(((NamedRef)((ArrayRef)Type("items")).Item).Key)!;
        var kind = graph.Enum(((NamedRef)item.Properties.Single().Type).Key)!;
        Assert.Equal(["small", "large"], kind.Members.Select(member => member.Value));
    }

    [Fact]
    public void APropertyMissingFromSomeSamplesIsOptionalAndOneSometimesNullIsNullable()
    {
        var options = new CSharpGeneratorOptions();
        var graph = JsonSampleInferrer.Infer([JsonNode.Parse("""[ { "a": 1, "b": "x" }, { "a": 2, "b": null }, { "a": 3 } ]""")], options);
        var root = graph.Classes.Single();
        Assert.True(root.Properties.Single(property => property.JsonName == "a").Required);
        var b = root.Properties.Single(property => property.JsonName == "b");
        Assert.False(b.Required);
        Assert.True(b.Nullable);
        Assert.Equal(new ArrayRef(new NamedRef(root.Key)), graph.Roots.Single());
    }

    [Fact]
    public void SchemaNamesAndEnumValuesBecomeValidIdentifiers()
    {
        var options = new CSharpGeneratorOptions();
        var document = StructuredText.Parse(PetStore);
        var code = CSharpCodeGenerator.Generate(SchemaTypeBuilder.FromReferences(document, SchemaTypeBuilder.List(document).Select(entry => entry.Reference), options), options).Preview;
        Assert.Contains("public enum Status", code, StringComparison.Ordinal);
        Assert.Contains("OnHold,", code, StringComparison.Ordinal);
        Assert.Contains("The2ndHand", code, StringComparison.Ordinal);
        Assert.Contains("case \"on-hold\": return Status.OnHold;", code, StringComparison.Ordinal);
        // A schema called Type would hide System.Type in the converters.
        Assert.Contains("public partial class TypeModel", code, StringComparison.Ordinal);
        // A property may not share its class's name.
        Assert.Contains("public string? PetValue { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("public string? Class { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("/// A pet &lt;for sale&gt; &amp; more", code, StringComparison.Ordinal);
        // allOf is flattened: a dog has the pet's properties and its own.
        Assert.Matches(@"class Dog\s*\{[\s\S]*Barks[\s\S]*\}", code);
        Assert.Contains("public Category? Parent { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("public string Name { get; set; } = null!;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void FilePerTypePutsEachTypeInItsOwnFileAndSharedCodeInTheNamedOne()
    {
        var options = new CSharpGeneratorOptions { Layout = FileLayout.FilePerType, FileName = "Pets.cs" };
        var document = StructuredText.Parse(PetStore);
        var code = CSharpCodeGenerator.Generate(SchemaTypeBuilder.FromReferences(document, ["#/components/schemas/Pet"], options), options);
        var names = code.Files.Select(file => file.FileName).ToList();
        Assert.Contains("Pet.cs", names);
        Assert.Contains("Status.cs", names);
        Assert.Contains("Category.cs", names);
        Assert.Contains("Pets.cs", names);
        Assert.Contains("public static class PetsJson", code.Files.Single(file => file.FileName == "Pets.cs").Content, StringComparison.Ordinal);
        Assert.DoesNotContain(names, name => name == "Dog.cs");
    }
}
