using System.Text.Json.Nodes;
using WpfStudio.Api.Import;

namespace WpfStudio.Api.CodeGeneration;

/// <summary>A schema an OpenAPI document or JSON Schema defines by name, offered for model generation.</summary>
public sealed record SchemaEntry(string Name, string Reference, string Kind, string? Description);

/// <summary>
/// Reads OpenAPI component schemas (or Swagger definitions, or a JSON Schema and its <c>$defs</c>) into a
/// <see cref="TypeGraph"/>: objects become classes, string enumerations become enums, <c>allOf</c> is flattened into
/// one class, and <c>oneOf</c>/<c>anyOf</c> collapse to their single real choice, a merged class, or "any".
/// </summary>
public sealed class SchemaTypeBuilder
{
    private readonly JsonNode _root;
    private readonly CSharpGeneratorOptions _options;
    private readonly TypeGraph _graph = new();
    private readonly Dictionary<string, (TypeRef Type, bool Nullable)> _byReference = new(StringComparer.Ordinal);
    private readonly NameScope _keys = new();
    // What each class's schemas, and the parts of its allOf, list as required, wherever the property itself is declared.
    private readonly Dictionary<ClassModel, HashSet<string>> _required = [];

    private SchemaTypeBuilder(JsonNode root, CSharpGeneratorOptions options) { _root = root; _options = options; }

    /// <summary>The named schemas of an OpenAPI 3 or Swagger 2 document, or the definitions of a JSON Schema.</summary>
    public static IReadOnlyList<SchemaEntry> List(JsonNode document)
    {
        var (container, prefix) = Container(document);
        if (container is null) return [];
        return container.Select(entry => new SchemaEntry(entry.Key, prefix + Escape(entry.Key), Describe(entry.Value), entry.Value.Text("description") ?? entry.Value.Text("title")))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Types for the named schemas, plus every schema they reach.</summary>
    public static TypeGraph FromReferences(JsonNode document, IEnumerable<string> references, CSharpGeneratorOptions options)
    {
        var builder = new SchemaTypeBuilder(document, options);
        foreach (var reference in references)
        {
            var (type, _) = builder.FromReference(reference);
            if (!builder._graph.Roots.Contains(type)) builder._graph.Roots.Add(type);
        }
        return builder._graph;
    }

    /// <summary>Types for a standalone JSON Schema, whose outermost type takes <see cref="CSharpGeneratorOptions.TopLevelName"/>.</summary>
    public static TypeGraph FromSchema(JsonNode schema, CSharpGeneratorOptions options)
    {
        var builder = new SchemaTypeBuilder(schema, options);
        var (type, _) = builder.Convert(schema, options.TopLevelName.Trim().Length > 0 ? options.TopLevelName.Trim() : "Root", 0);
        builder._graph.Roots.Add(type);
        return builder._graph;
    }

    private static (JsonObject? Container, string Prefix) Container(JsonNode document)
    {
        if (document.Object("components").Object("schemas") is { } components) return (components, "#/components/schemas/");
        if (document.Object("definitions") is { } definitions) return (definitions, "#/definitions/");
        if (document.Object("$defs") is { } defs) return (defs, "#/$defs/");
        return (null, "");
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Describe(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return "any";
        if (obj.ContainsKey("enum")) return "enum";
        if (obj.ContainsKey("allOf")) return "object (allOf)";
        if (obj.ContainsKey("oneOf") || obj.ContainsKey("anyOf")) return "union";
        return SchemaExamples.TypeOf(obj) ?? "any";
    }

    private (TypeRef Type, bool Nullable) FromReference(string reference)
    {
        if (_byReference.TryGetValue(reference, out var known)) return known;
        var target = JsonPointer.Resolve(_root, reference);
        if (target is null) return (AnyRef.Instance, true);
        var name = JsonPointer.Name(reference);
        if (IsClassLike(target))
        {
            // Registered before its properties are read, so a schema that refers to itself ends at its own name.
            var model = new ClassModel { Key = _keys.Claim(name), Name = name, Description = target.Text("description") ?? target.Text("title") };
            _graph.Classes.Add(model);
            var result = ((TypeRef)new NamedRef(model.Key), IsNullable(target));
            _byReference[reference] = result;
            FillClass(model, target, 0);
            return result;
        }
        if (IsStringEnum(target) && _options.GenerateEnums)
        {
            var result = ((TypeRef)Enumeration(target.AsObject(), name), IsNullable(target));
            _byReference[reference] = result;
            return result;
        }
        // An alias for an array, a dictionary or a primitive: used in place.
        _byReference[reference] = (AnyRef.Instance, true);
        var converted = Convert(target, name, 1);
        _byReference[reference] = converted;
        return converted;
    }

    private (TypeRef Type, bool Nullable) Convert(JsonNode? schema, string nameHint, int depth)
    {
        if (schema is not JsonObject obj || depth > 40) return (AnyRef.Instance, true);
        if (obj.Text("$ref") is { } reference)
        {
            var (type, nullable) = reference.StartsWith('#') ? FromReference(reference) : (AnyRef.Instance, true);
            // OpenAPI 3.1 allows nullable: true or a type list beside a $ref.
            return (type, nullable || IsNullable(obj));
        }
        var ownNullable = IsNullable(obj);
        if (obj.Array("allOf") is { Count: > 0 } allOf)
        {
            // allOf: [ { $ref: X } ] with nothing else is X itself, nullable or not.
            if (allOf.Count == 1 && !obj.ContainsKey("properties")) { var single = Convert(allOf[0], nameHint, depth + 1); return (single.Type, single.Nullable || ownNullable); }
            var model = NewClass(nameHint, obj);
            FillClass(model, obj, depth);
            return (new NamedRef(model.Key), ownNullable);
        }
        foreach (var keyword in new[] { "oneOf", "anyOf" })
            if (obj.Array(keyword) is { Count: > 0 } choices) return Union(obj, choices, nameHint, depth, ownNullable);
        if (IsStringEnum(obj)) return (_options.GenerateEnums ? Enumeration(obj, nameHint) : new PrimitiveRef(PrimitiveKind.String), ownNullable);
        switch (SchemaExamples.TypeOf(obj))
        {
            case "object":
                if (obj.Object("properties") is { Count: > 0 })
                {
                    var model = NewClass(nameHint, obj);
                    FillClass(model, obj, depth);
                    return (new NamedRef(model.Key), ownNullable);
                }
                var additional = obj["additionalProperties"];
                if (additional is JsonObject valueSchema)
                {
                    var (value, valueNullable) = Convert(valueSchema, CSharpNames.Singular(nameHint) + "Value", depth + 1);
                    return (new MapRef(value, valueNullable), ownNullable);
                }
                return (new MapRef(AnyRef.Instance, true), ownNullable);
            case "array":
                var (item, itemNullable) = Convert(obj["items"], CSharpNames.Singular(nameHint), depth + 1);
                return (new ArrayRef(item, itemNullable), ownNullable);
            case "string": return (new PrimitiveRef(StringKind(obj.Text("format"))), ownNullable);
            case "integer": return (new PrimitiveRef(obj.Text("format") == "int32" ? PrimitiveKind.Integer : PrimitiveKind.Long), ownNullable);
            case "number": return (new PrimitiveRef(PrimitiveKind.Number), ownNullable);
            case "boolean": return (new PrimitiveRef(PrimitiveKind.Boolean), ownNullable);
            case "null": return (AnyRef.Instance, true);
            default: return (AnyRef.Instance, true);
        }
    }

    private (TypeRef Type, bool Nullable) Union(JsonObject owner, JsonArray choices, string nameHint, int depth, bool ownNullable)
    {
        var real = choices.Where(choice => SchemaExamples.TypeOf(Resolve(choice)) != "null").ToList();
        var nullable = ownNullable || real.Count < choices.Count;
        if (real.Count == 0) return (AnyRef.Instance, true);
        if (real.Count == 1) { var only = Convert(real[0], nameHint, depth + 1); return (only.Type, only.Nullable || nullable); }
        var resolved = real.Select(Resolve).ToList();
        // Several shapes of object (a discriminated union): one class holding every property, none of them required.
        if (resolved.All(IsClassLike))
        {
            var model = NewClass(nameHint, owner);
            foreach (var choice in real) FillClass(model, choice!, depth + 1, optional: true);
            return (new NamedRef(model.Key), nullable);
        }
        if (resolved.All(choice => choice is not null && IsStringEnum(choice)) && _options.GenerateEnums)
        {
            var merged = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(resolved.SelectMany(choice => choice.Array("enum")!.Select(value => value?.DeepClone())).ToArray()) };
            return (Enumeration(merged, nameHint), nullable);
        }
        var converted = real.Select(choice => Convert(choice, nameHint, depth + 1)).ToList();
        var types = converted.Select(entry => entry.Type).Distinct().ToList();
        if (types.Count == 1) return (types[0], nullable || converted.Any(entry => entry.Nullable));
        // integer or number: a number.
        if (types.All(type => type is PrimitiveRef { Kind: PrimitiveKind.Integer or PrimitiveKind.Long or PrimitiveKind.Number })) return (new PrimitiveRef(PrimitiveKind.Number), nullable);
        return (AnyRef.Instance, true);
    }

    private ClassModel NewClass(string nameHint, JsonObject schema)
    {
        var model = new ClassModel { Key = _keys.Claim(nameHint), Name = nameHint, Description = schema.Text("description") ?? schema.Text("title") };
        _graph.Classes.Add(model);
        return model;
    }

    private void FillClass(ClassModel model, JsonNode schema, int depth, bool optional = false)
    {
        if (depth > 40 || Resolve(schema) is not { } obj) return;
        foreach (var part in obj.Array("allOf") ?? []) FillClass(model, part!, depth + 1, optional);
        if (!_required.TryGetValue(model, out var required)) _required[model] = required = new HashSet<string>(StringComparer.Ordinal);
        // Choices of a oneOf or anyOf, and "make every property optional", require nothing.
        if (!optional && !_options.AllPropertiesOptional)
            foreach (var name in obj.Array("required") ?? []) if (name?.ToString() is { } text) required.Add(text);
        foreach (var (name, propertySchema) in obj.Object("properties") ?? [])
        {
            var (type, nullable) = Convert(propertySchema, CSharpNames.Pascal(name, "Value"), depth + 1);
            var description = propertySchema.Text("description") ?? Resolve(propertySchema).Text("description");
            if (model.Properties.FirstOrDefault(property => property.JsonName == name) is { } existing)
            {
                // The same property from two parts of an allOf or two choices: the later definition wins.
                existing.Type = type; existing.Nullable |= nullable; existing.Description ??= description;
                continue;
            }
            model.Properties.Add(new PropertyModel { JsonName = name, Type = type, Nullable = nullable, Description = description });
        }
        // A part may require a property another part declares, in either order; the outermost call runs last and sees them all.
        foreach (var property in model.Properties) property.Required |= required.Contains(property.JsonName);
    }

    private NamedRef Enumeration(JsonObject schema, string nameHint)
    {
        var model = new EnumModel { Key = _keys.Claim(nameHint), Name = nameHint, Description = schema.Text("description") ?? schema.Text("title") };
        foreach (var value in schema.Array("enum")!)
            if (value is JsonValue text && text.TryGetValue<string>(out var member) && !model.Members.Any(existing => existing.Value == member))
                model.Members.Add(new EnumMemberModel { Value = member });
        _graph.Enums.Add(model);
        return new NamedRef(model.Key);
    }

    private JsonObject? Resolve(JsonNode? node)
    {
        for (var hops = 0; hops < 16 && node is JsonObject obj && obj.Text("$ref") is { } reference; hops++) node = JsonPointer.Resolve(_root, reference);
        return node as JsonObject;
    }

    private bool IsClassLike(JsonNode? schema) => IsClassLike(schema, new HashSet<JsonNode>(ReferenceEqualityComparer.Instance));

    private bool IsClassLike(JsonNode? schema, HashSet<JsonNode> visited)
    {
        var obj = Resolve(schema);
        if (obj is null || !visited.Add(obj)) return false;
        if (obj.Object("properties") is { Count: > 0 }) return true;
        return obj.Array("allOf") is { Count: > 0 } parts && (parts.Count > 1 || parts.Any(part => IsClassLike(part, visited)));
    }

    private static bool IsStringEnum(JsonNode schema) =>
        schema.Array("enum") is { Count: > 0 } values && values.Where(value => value is not null).All(value => value is JsonValue v && v.TryGetValue<string>(out _))
        && SchemaExamples.TypeOf(schema) is null or "string";

    private static bool IsNullable(JsonNode? schema) =>
        schema.Flag("nullable") || schema.Array("type")?.Any(entry => entry?.ToString() == "null") == true
        || schema.Array("enum")?.Any(value => value is null) == true;

    private PrimitiveKind StringKind(string? format) => format switch
    {
        "date-time" => PrimitiveKind.DateTime,
        "date" => PrimitiveKind.Date,
        "time" => PrimitiveKind.Time,
        "uuid" or "guid" => PrimitiveKind.Guid,
        "uri" or "url" => PrimitiveKind.Uri,
        "byte" => PrimitiveKind.Bytes,
        "int64" or "int32" when _options.DetectIntegerStrings => PrimitiveKind.IntegerString,
        _ => PrimitiveKind.String,
    };
}
