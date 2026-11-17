using System.Text;

namespace WpfStudio.Api.CodeGeneration;

public sealed record GeneratedFile(string FileName, string Content);

/// <summary>The files to write and anything about the options that could not be honoured.</summary>
public sealed record GeneratedCode(IReadOnlyList<GeneratedFile> Files, IReadOnlyList<string> Warnings)
{
    /// <summary>All files as one text, each headed by its name when there are several.</summary>
    public string Preview => Files.Count == 1 ? Files[0].Content
        : string.Join(Environment.NewLine, Files.Select(file => "// ═════ " + file.FileName + " ═════" + Environment.NewLine + file.Content));
}

/// <summary>
/// Writes C# for a <see cref="TypeGraph"/>, for System.Text.Json or Newtonsoft.Json, following quicktype's C# output:
/// partial classes or records with serialization attributes, enums with converters that keep the JSON spelling of each
/// value, converters for numbers and booleans sent as strings, and FromJson/ToJson helpers.
/// </summary>
public static class CSharpCodeGenerator
{
    // Model names that would hide a type the generated code itself uses.
    private static readonly HashSet<string> ReservedTypeNames = new(StringComparer.Ordinal)
    {
        "Type", "String", "Object", "Boolean", "Int32", "Int64", "Double", "Decimal", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan",
        "Guid", "Uri", "Exception", "Array", "Enum", "Attribute", "Math", "Convert", "List", "Dictionary", "IReadOnlyList", "IReadOnlyDictionary",
        "JsonSerializer", "JsonConverter", "JsonException", "JsonElement", "JsonDocument", "JsonNode", "JsonProperty", "JsonPropertyName",
        "JsonIgnore", "JsonRequired", "JsonConvert", "JsonToken", "JToken", "JObject", "JArray", "JsonReader", "JsonWriter", "Utf8JsonReader",
        "Utf8JsonWriter", "JsonSerializerOptions", "JsonSerializerSettings", "JsonSerializerDefaults", "JsonNumberHandling", "JsonIgnoreCondition",
        "Required", "NullValueHandling", "EnumMember", "StringEnumConverter", "IsoDateTimeConverter", "DateTimeStyles", "DateParseHandling",
        "MetadataPropertyHandling", "CultureInfo", "Nullable", "Task", "Action", "Func", "Version", "Index", "Range", "System",
    };

    public static GeneratedCode Generate(TypeGraph graph, CSharpGeneratorOptions options)
    {
        var context = new Context(graph, options);
        return context.Render();
    }

    private sealed class Context
    {
        private readonly TypeGraph _graph;
        private readonly CSharpGeneratorOptions _options;
        private readonly List<string> _warnings = [];
        private readonly bool _records, _initOnly, _requiredKeyword, _nullable, _attributes, _complete, _stj;
        private readonly Dictionary<string, string> _enumConverters = new(StringComparer.Ordinal);
        private readonly HashSet<string> _classesWithObjectFromJson = new(StringComparer.Ordinal);
        private string _helperName = "";
        private string _parseStringConverter = "", _booleanStringConverter = "", _nullableBooleanStringConverter = "";
        private bool _usesParseString, _usesBooleanString, _usesNullableBooleanString;

        public Context(TypeGraph graph, CSharpGeneratorOptions options)
        {
            _graph = graph;
            _options = options;
            _nullable = options.Nullable;
            _stj = options.Framework == JsonFramework.SystemTextJson;
            _attributes = options.WritesAttributes;
            _complete = options.Features == OutputFeatures.Complete;
            _records = options.UseRecords && options.CanUseRecords;
            _initOnly = options.InitOnlyProperties && options.CanUseRecords;
            _requiredKeyword = options.RequiredMembers && options.CanUseRequired;
            if (options.UseRecords && !options.CanUseRecords) _warnings.Add("Records need C# 9 or later, so classes were generated.");
            if (options.InitOnlyProperties && !options.CanUseRecords) _warnings.Add("Init-only properties need C# 9 or later, so setters were generated.");
            if (options.RequiredMembers && !options.CanUseRequired) _warnings.Add("Required members need C# 11 or later, so the 'required' modifier was left out.");
            if (_records && options.BaseClass.Trim().Length > 0) _warnings.Add("A record can only derive from another record. Check that " + options.BaseClass.Trim() + " is one.");
        }

        public GeneratedCode Render()
        {
            KeepNestedEncodedValuesAsText();
            AssignNames();
            var types = new List<(string Name, string Body, HashSet<string> Usings)>();
            // The document's own type first, then what it uses, the way the types read top to bottom.
            foreach (var model in _graph.Classes) { var usings = new HashSet<string>(StringComparer.Ordinal); types.Add((model.Name, RenderClass(model, usings), usings)); }
            foreach (var model in _graph.Enums) { var usings = new HashSet<string>(StringComparer.Ordinal); types.Add((model.Name, RenderEnum(model, usings), usings)); }
            var helperUsings = new HashSet<string>(StringComparer.Ordinal);
            var helpers = RenderHelpers(helperUsings);
            var files = new List<GeneratedFile>();
            var fileName = FileName();
            if (_options.Layout == FileLayout.SingleFile || types.Count == 0)
            {
                var usings = types.SelectMany(type => type.Usings).Concat(helperUsings).ToHashSet(StringComparer.Ordinal);
                var bodies = types.Select(type => type.Body).Concat(helpers.Length > 0 ? [helpers] : System.Array.Empty<string>()).ToList();
                files.Add(new GeneratedFile(fileName, Compose(usings, bodies, header: true)));
            }
            else
            {
                var names = new NameScope();
                foreach (var (name, body, usings) in types) files.Add(new GeneratedFile(names.Claim(name) + ".cs", Compose(usings, [body], header: true)));
                if (helpers.Length > 0) files.Add(new GeneratedFile(names.Claim(Path.GetFileNameWithoutExtension(fileName)) + ".cs", Compose(helperUsings, [helpers], header: false)));
            }
            if (_graph.Classes.Count == 0 && _graph.Enums.Count == 0) _warnings.Add("There are no objects or enumerations to generate types for.");
            return new GeneratedCode(files, _warnings);
        }

        private string FileName()
        {
            var name = Path.GetFileName(_options.FileName.Trim());
            if (name.Length == 0) name = (_options.TopLevelName.Trim().Length > 0 ? CSharpNames.Pascal(_options.TopLevelName) : "Models") + ".cs";
            return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? name : name + ".cs";
        }

        /// <summary>
        /// The converters for numbers and booleans written as strings attach to a property, so they cannot reach the items of
        /// a list or the values of a dictionary. Those stay strings, which read and write the document unchanged.
        /// </summary>
        private void KeepNestedEncodedValuesAsText()
        {
            static TypeRef Plain(TypeRef type) => type switch
            {
                PrimitiveRef { Kind: PrimitiveKind.IntegerString or PrimitiveKind.BooleanString } => new PrimitiveRef(PrimitiveKind.String),
                ArrayRef array => array with { Item = Plain(array.Item) },
                MapRef map => map with { Value = Plain(map.Value) },
                _ => type,
            };
            static TypeRef Outer(TypeRef type) => type switch
            {
                ArrayRef array => array with { Item = Plain(array.Item) },
                MapRef map => map with { Value = Plain(map.Value) },
                _ => type,
            };
            foreach (var property in _graph.Classes.SelectMany(model => model.Properties)) property.Type = Outer(property.Type);
            for (var i = 0; i < _graph.Roots.Count; i++) _graph.Roots[i] = Outer(_graph.Roots[i]);
        }

        // ------------------------------------------------------------------ names

        private void AssignNames()
        {
            var reserved = ReservedTypeNames.Concat(_options.Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries));
            var types = new NameScope(reserved);
            string TypeName(string hint)
            {
                var name = CSharpNames.Pascal(hint, "Model");
                return types.Claim(types.IsTaken(name) && ReservedTypeNames.Contains(name) || _options.Namespace.Split('.').Contains(name) ? name + "Model" : name);
            }
            foreach (var model in _graph.Classes) model.Name = TypeName(model.Name.Length > 0 ? model.Name : model.Key);
            foreach (var model in _graph.Enums) model.Name = TypeName(model.Name.Length > 0 ? model.Name : model.Key);
            var stem = CSharpNames.Pascal(Path.GetFileNameWithoutExtension(FileName()), "Models");
            _helperName = types.Claim(stem + "Json");
            if (_attributes && _stj) foreach (var model in _graph.Enums) _enumConverters[model.Key] = types.Claim(model.Name + "Converter");
            _parseStringConverter = types.Claim("ParseStringConverter");
            _booleanStringConverter = types.Claim("BooleanStringConverter");
            _nullableBooleanStringConverter = types.Claim("NullableBooleanStringConverter");

            foreach (var model in _graph.Enums)
            {
                var members = new NameScope([model.Name]);
                foreach (var member in model.Members)
                    member.Name = members.Claim(member.Value.Length == 0 ? "Empty" : CSharpNames.Pascal(member.Value, "Value"));
            }
            foreach (var model in _graph.Classes)
            {
                // A member may not share its class's name (CS0542), nor the helper this generator adds.
                var members = new NameScope([model.Name, "FromJson"]);
                foreach (var property in model.Properties)
                {
                    var name = _options.KeepPropertyNames ? CSharpNames.Verbatim(property.JsonName) : CSharpNames.Pascal(property.JsonName, "Property");
                    if (name.TrimStart('@') == model.Name || members.IsTaken(name) && name is "FromJson") name += "Value";
                    property.Name = members.Claim(name);
                }
            }
        }

        // ------------------------------------------------------------------ types

        private string RenderEnum(EnumModel model, HashSet<string> usings)
        {
            var builder = new StringBuilder();
            Documentation(builder, model.Description, "");
            if (_attributes)
            {
                if (_stj) { usings.Add("System.Text.Json.Serialization"); builder.Append("[JsonConverter(typeof(").Append(_enumConverters[model.Key]).AppendLine("))]"); }
                else { usings.Add("Newtonsoft.Json"); usings.Add("Newtonsoft.Json.Converters"); builder.AppendLine("[JsonConverter(typeof(StringEnumConverter))]"); }
            }
            builder.Append("public enum ").AppendLine(model.Name).AppendLine("{");
            for (var i = 0; i < model.Members.Count; i++)
            {
                var member = model.Members[i];
                if (_attributes && !_stj) { usings.Add("System.Runtime.Serialization"); builder.Append("    [EnumMember(Value = ").Append(CSharpNames.Literal(member.Value)).AppendLine(")]"); }
                builder.Append("    ").Append(member.Name).AppendLine(i < model.Members.Count - 1 ? "," : "");
            }
            builder.AppendLine("}");
            if (_attributes && _stj) builder.AppendLine().Append(RenderEnumConverter(model, usings));
            return builder.ToString();
        }

        private string RenderEnumConverter(EnumModel model, HashSet<string> usings)
        {
            usings.Add("System.Text.Json"); usings.Add("System.Text.Json.Serialization");
            var name = _enumConverters[model.Key];
            var builder = new StringBuilder();
            builder.Append("internal sealed class ").Append(name).Append(" : JsonConverter<").Append(model.Name).AppendLine(">").AppendLine("{");
            builder.Append("    public override ").Append(model.Name).AppendLine(" Read(ref Utf8JsonReader reader, global::System.Type typeToConvert, JsonSerializerOptions options)");
            builder.AppendLine("    {").AppendLine("        var value = reader.GetString();").AppendLine("        switch (value)").AppendLine("        {");
            foreach (var member in model.Members)
                builder.Append("            case ").Append(CSharpNames.Literal(member.Value)).Append(": return ").Append(model.Name).Append('.').Append(member.Name).AppendLine(";");
            builder.AppendLine("        }");
            builder.Append("        throw new JsonException(\"Cannot read \" + value + \" as ").Append(model.Name).AppendLine(".\");").AppendLine("    }").AppendLine();
            builder.Append("    public override void Write(Utf8JsonWriter writer, ").Append(model.Name).AppendLine(" value, JsonSerializerOptions options)");
            builder.AppendLine("    {").AppendLine("        switch (value)").AppendLine("        {");
            foreach (var member in model.Members)
                builder.Append("            case ").Append(model.Name).Append('.').Append(member.Name).Append(": writer.WriteStringValue(").Append(CSharpNames.Literal(member.Value)).AppendLine("); return;");
            builder.AppendLine("        }");
            builder.Append("        throw new JsonException(\"Cannot write \" + value + \" as ").Append(model.Name).AppendLine(".\");").AppendLine("    }").AppendLine("}");
            return builder.ToString();
        }

        private string RenderClass(ClassModel model, HashSet<string> usings)
        {
            var builder = new StringBuilder();
            Documentation(builder, model.Description, "");
            builder.Append("public partial ").Append(_records ? "record " : "class ").Append(model.Name);
            if (_options.BaseClass.Trim().Length > 0) builder.Append(" : ").Append(_options.BaseClass.Trim());
            builder.AppendLine().AppendLine("{");
            var dense = _options.Density == PropertyDensity.Dense;
            for (var i = 0; i < model.Properties.Count; i++)
            {
                if (i > 0 && !dense) builder.AppendLine();
                RenderProperty(builder, model.Properties[i], usings, dense);
            }
            if (_complete && FromJsonFor(model) is { } fromJson)
            {
                if (model.Properties.Count > 0) builder.AppendLine();
                builder.Append(fromJson);
                usings.Add(_stj ? "System.Text.Json" : "Newtonsoft.Json");
                if (fromJson.Contains("List<", StringComparison.Ordinal) || fromJson.Contains("Dictionary<", StringComparison.Ordinal)) usings.Add("System.Collections.Generic");
            }
            builder.AppendLine("}");
            return builder.ToString();
        }

        private void RenderProperty(StringBuilder builder, PropertyModel property, HashSet<string> usings, bool dense)
        {
            const string indent = "    ";
            Documentation(builder, property.Description, indent);
            var isCollection = property.Type is ArrayRef or MapRef;
            var initialized = _options.InitializeCollections && isCollection && !property.Nullable;
            var typeNullable = (property.Nullable || !property.Required) && !initialized;
            var type = TypeName(property.Type, typeNullable, usings);
            var isValue = IsValueType(property.Type);
            var attributes = new List<string>();
            if (_attributes)
            {
                var kind = (property.Type as PrimitiveRef)?.Kind;
                if (_stj)
                {
                    usings.Add("System.Text.Json.Serialization");
                    attributes.Add("[JsonPropertyName(" + CSharpNames.Literal(property.JsonName) + ")]");
                    if (_options.CheckRequired && property.Required) attributes.Add("[JsonRequired]");
                    else if (!property.Required && typeNullable) attributes.Add("[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]");
                    if (kind == PrimitiveKind.IntegerString) attributes.Add("[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]");
                }
                else
                {
                    usings.Add("Newtonsoft.Json");
                    var arguments = new List<string> { CSharpNames.Literal(property.JsonName) };
                    if (_options.CheckRequired && property.Required) arguments.Add(property.Nullable ? "Required = Required.AllowNull" : "Required = Required.Always");
                    else if (!property.Required) arguments.Add("NullValueHandling = NullValueHandling.Ignore");
                    attributes.Add("[JsonProperty(" + string.Join(", ", arguments) + ")]");
                    if (kind == PrimitiveKind.IntegerString) { _usesParseString = true; attributes.Add("[JsonConverter(typeof(" + _parseStringConverter + "))]"); }
                }
                if (kind == PrimitiveKind.BooleanString)
                {
                    var nullableConverter = _stj && typeNullable;
                    if (nullableConverter) _usesNullableBooleanString = true; else _usesBooleanString = true;
                    attributes.Add("[JsonConverter(typeof(" + (nullableConverter ? _nullableBooleanStringConverter : _booleanStringConverter) + "))]");
                }
            }
            var modifiers = "public " + (_options.VirtualProperties ? "virtual " : "");
            var useRequired = _requiredKeyword && property.Required && !typeNullable && !initialized;
            if (useRequired) modifiers += "required ";
            var accessors = _initOnly ? "{ get; init; }" : "{ get; set; }";
            var initializer = "";
            if (initialized) initializer = " = " + EmptyCollection(property.Type, usings) + ";";
            else if (_nullable && !typeNullable && !isValue && !useRequired) initializer = " = null!;";
            var declaration = modifiers + type + " " + property.Name + " " + accessors + initializer;
            if (dense) builder.Append(indent).AppendLine(string.Join(" ", attributes.Append(declaration)));
            else
            {
                foreach (var attribute in attributes) builder.Append(indent).AppendLine(attribute);
                builder.Append(indent).AppendLine(declaration);
            }
        }

        private string? FromJsonFor(ClassModel model)
        {
            var nullable = _nullable ? "?" : "";
            var builder = new StringBuilder();
            foreach (var root in _graph.Roots)
            {
                string? type = root switch
                {
                    NamedRef named when named.Key == model.Key => model.Name,
                    ArrayRef { Item: NamedRef item } when item.Key == model.Key && !_classesWithObjectFromJson.Contains(model.Key) && !_graph.Roots.Contains(new NamedRef(model.Key)) => TypeName(root, false, []),
                    _ => null,
                };
                if (type is null || !_classesWithObjectFromJson.Add(model.Key)) continue;
                builder.Append("    public static ").Append(type).Append(nullable).Append(" FromJson(string json) => ")
                    .Append(_stj ? "JsonSerializer.Deserialize<" : "JsonConvert.DeserializeObject<").Append(type).Append(">(json, ").Append(_helperName).AppendLine(".Settings);");
            }
            return builder.Length == 0 ? null : builder.ToString();
        }

        // ------------------------------------------------------------------ helpers

        private string RenderHelpers(HashSet<string> usings)
        {
            var builder = new StringBuilder();
            if (_complete)
            {
                usings.Add(_stj ? "System.Text.Json" : "Newtonsoft.Json");
                builder.Append("public static class ").AppendLine(_helperName).AppendLine("{");
                if (_stj) builder.AppendLine("    public static readonly JsonSerializerOptions Settings = new JsonSerializerOptions(JsonSerializerDefaults.General);");
                else
                {
                    usings.Add("Newtonsoft.Json.Converters"); usings.Add("System.Globalization");
                    builder.AppendLine("    public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings")
                        .AppendLine("    {")
                        .AppendLine("        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,")
                        .AppendLine("        DateParseHandling = DateParseHandling.None,")
                        .AppendLine("        Converters = { new IsoDateTimeConverter { DateTimeStyles = DateTimeStyles.AssumeUniversal } },")
                        .AppendLine("    };");
                }
                foreach (var root in _graph.Roots.Distinct())
                {
                    if (root is not (NamedRef or ArrayRef { Item: NamedRef }) || root is NamedRef { } named && _graph.Class(named.Key) is null && _graph.Enum(named.Key) is null) continue;
                    var type = TypeName(root, false, usings);
                    builder.AppendLine().Append("    public static string ToJson(this ").Append(type).Append(" self) => ")
                        .Append(_stj ? "JsonSerializer.Serialize(self, Settings);" : "JsonConvert.SerializeObject(self, Settings);").AppendLine();
                }
                builder.AppendLine("}");
            }
            if (_usesParseString) Separate(builder).Append(ParseStringConverterSource(usings));
            if (_usesBooleanString) Separate(builder).Append(BooleanStringConverterSource(usings, nullable: false));
            if (_usesNullableBooleanString) Separate(builder).Append(BooleanStringConverterSource(usings, nullable: true));
            return builder.ToString();
        }

        private static StringBuilder Separate(StringBuilder builder) => builder.Length > 0 ? builder.AppendLine() : builder;

        private string ParseStringConverterSource(HashSet<string> usings)
        {
            usings.Add("Newtonsoft.Json"); usings.Add("System.Globalization");
            var q = _nullable ? "?" : "";
            return $$"""
                internal sealed class {{_parseStringConverter}} : JsonConverter
                {
                    public override bool CanConvert(global::System.Type t) => t == typeof(long) || t == typeof(long?);

                    public override object{{q}} ReadJson(JsonReader reader, global::System.Type t, object{{q}} existingValue, JsonSerializer serializer)
                    {
                        if (reader.TokenType == JsonToken.Null) return null;
                        var value = serializer.Deserialize<string>(reader);
                        long number;
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
                        throw new JsonSerializationException("Cannot read " + value + " as a whole number.");
                    }

                    public override void WriteJson(JsonWriter writer, object{{q}} untypedValue, JsonSerializer serializer)
                    {
                        if (untypedValue == null) { writer.WriteNull(); return; }
                        writer.WriteValue(((long)untypedValue).ToString(CultureInfo.InvariantCulture));
                    }
                }

                """;
        }

        private string BooleanStringConverterSource(HashSet<string> usings, bool nullable)
        {
            var q = _nullable ? "?" : "";
            if (_stj)
            {
                usings.Add("System.Text.Json"); usings.Add("System.Text.Json.Serialization");
                var type = nullable ? "bool?" : "bool";
                var name = nullable ? _nullableBooleanStringConverter : _booleanStringConverter;
                var nullRead = nullable ? "\n        if (reader.TokenType == JsonTokenType.Null) return null;" : "";
                var write = nullable
                    ? "if (value == null) writer.WriteNullValue(); else writer.WriteStringValue(value.Value ? \"true\" : \"false\");"
                    : "writer.WriteStringValue(value ? \"true\" : \"false\");";
                return $$"""
                    internal sealed class {{name}} : JsonConverter<{{type}}>
                    {
                        public override {{type}} Read(ref Utf8JsonReader reader, global::System.Type typeToConvert, JsonSerializerOptions options)
                        {{{nullRead}}
                            if (reader.TokenType == JsonTokenType.True) return true;
                            if (reader.TokenType == JsonTokenType.False) return false;
                            var value = reader.GetString();
                            bool result;
                            if (bool.TryParse(value, out result)) return result;
                            throw new JsonException("Cannot read " + value + " as true or false.");
                        }

                        public override void Write(Utf8JsonWriter writer, {{type}} value, JsonSerializerOptions options)
                        {
                            {{write}}
                        }
                    }

                    """.Replace("\r\n", "\n", StringComparison.Ordinal);
            }
            usings.Add("Newtonsoft.Json");
            return $$"""
                internal sealed class {{_booleanStringConverter}} : JsonConverter
                {
                    public override bool CanConvert(global::System.Type t) => t == typeof(bool) || t == typeof(bool?);

                    public override object{{q}} ReadJson(JsonReader reader, global::System.Type t, object{{q}} existingValue, JsonSerializer serializer)
                    {
                        if (reader.TokenType == JsonToken.Null) return null;
                        if (reader.TokenType == JsonToken.Boolean) return (bool)reader.Value{{(_nullable ? "!" : "")}};
                        var value = serializer.Deserialize<string>(reader);
                        bool result;
                        if (bool.TryParse(value, out result)) return result;
                        throw new JsonSerializationException("Cannot read " + value + " as true or false.");
                    }

                    public override void WriteJson(JsonWriter writer, object{{q}} untypedValue, JsonSerializer serializer)
                    {
                        if (untypedValue == null) { writer.WriteNull(); return; }
                        writer.WriteValue((bool)untypedValue ? "true" : "false");
                    }
                }

                """;
        }

        // ------------------------------------------------------------------ type names

        private string TypeName(TypeRef type, bool nullable, HashSet<string> usings)
        {
            var name = type switch
            {
                PrimitiveRef primitive => Primitive(primitive.Kind, usings),
                ArrayRef array => Array(array, usings),
                MapRef map => Map(map, usings),
                NamedRef named => _graph.Class(named.Key)?.Name ?? _graph.Enum(named.Key)?.Name ?? "object",
                _ => Any(usings),
            };
            if (!nullable) return name;
            if (IsValueType(type)) return name + "?";
            return _nullable ? name + "?" : name;
        }

        private string Primitive(PrimitiveKind kind, HashSet<string> usings)
        {
            string WithSystem(string name) { usings.Add("System"); return name; }
            return kind switch
            {
                PrimitiveKind.String => "string",
                PrimitiveKind.Boolean or PrimitiveKind.BooleanString => "bool",
                PrimitiveKind.Integer => "int",
                PrimitiveKind.Long or PrimitiveKind.IntegerString => "long",
                PrimitiveKind.Number => _options.Numbers == NumberStyle.Decimal ? "decimal" : "double",
                PrimitiveKind.DateTime => WithSystem(_options.Dates == DateStyle.DateTime ? "DateTime" : "DateTimeOffset"),
                PrimitiveKind.Date => WithSystem(_options.UseDateOnlyTimeOnly ? "DateOnly" : _options.Dates == DateStyle.DateTime ? "DateTime" : "DateTimeOffset"),
                PrimitiveKind.Time => _options.UseDateOnlyTimeOnly ? WithSystem("TimeOnly") : "string",
                PrimitiveKind.Guid => WithSystem("Guid"),
                PrimitiveKind.Uri => WithSystem("Uri"),
                PrimitiveKind.Bytes => "byte[]",
                _ => "string",
            };
        }

        private string Array(ArrayRef array, HashSet<string> usings)
        {
            var item = TypeName(array.Item, array.ItemNullable, usings);
            if (_options.Arrays == ArrayStyle.Array) return item + "[]";
            usings.Add("System.Collections.Generic");
            return (_options.Arrays == ArrayStyle.ReadOnlyList ? "IReadOnlyList<" : "List<") + item + ">";
        }

        private string Map(MapRef map, HashSet<string> usings)
        {
            usings.Add("System.Collections.Generic");
            return (_options.Arrays == ArrayStyle.ReadOnlyList ? "IReadOnlyDictionary<string, " : "Dictionary<string, ") + TypeName(map.Value, map.ValueNullable, usings) + ">";
        }

        private string Any(HashSet<string> usings)
        {
            switch (_options.Any)
            {
                case AnyStyle.Dynamic: return "dynamic";
                case AnyStyle.JsonDom when _stj: usings.Add("System.Text.Json"); return "JsonElement";
                case AnyStyle.JsonDom: usings.Add("Newtonsoft.Json.Linq"); return "JToken";
                default: return "object";
            }
        }

        private string EmptyCollection(TypeRef type, HashSet<string> usings)
        {
            var name = TypeName(type, false, usings);
            if (type is ArrayRef array && _options.Arrays == ArrayStyle.Array)
            {
                usings.Add("System");
                return "Array.Empty<" + TypeName(array.Item, array.ItemNullable, usings) + ">()";
            }
            var concrete = name.StartsWith("IReadOnlyList<", StringComparison.Ordinal) ? "List<" + name["IReadOnlyList<".Length..]
                : name.StartsWith("IReadOnlyDictionary<", StringComparison.Ordinal) ? "Dictionary<" + name["IReadOnlyDictionary<".Length..] : name;
            return "new " + concrete + "()";
        }

        private bool IsValueType(TypeRef type) => type switch
        {
            PrimitiveRef { Kind: PrimitiveKind.String or PrimitiveKind.Uri or PrimitiveKind.Bytes } => false,
            PrimitiveRef { Kind: PrimitiveKind.Time } => _options.UseDateOnlyTimeOnly,
            PrimitiveRef => true,
            NamedRef named => _graph.Enum(named.Key) is not null,
            AnyRef => _options.Any == AnyStyle.JsonDom && _stj,
            _ => false,
        };

        // ------------------------------------------------------------------ files

        private void Documentation(StringBuilder builder, string? text, string indent)
        {
            if (!_options.DocumentationComments || string.IsNullOrWhiteSpace(text)) return;
            builder.Append(indent).AppendLine("/// <summary>");
            foreach (var line in text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                builder.Append(indent).Append("/// ").AppendLine(CSharpNames.XmlText(line.TrimEnd()));
            builder.Append(indent).AppendLine("/// </summary>");
        }

        private string Compose(HashSet<string> usings, IReadOnlyList<string> bodies, bool header)
        {
            var builder = new StringBuilder();
            if (header && _complete) Usage(builder);
            if (_nullable) builder.AppendLine("#nullable enable").AppendLine();
            foreach (var name in usings.OrderBy(name => name.StartsWith("System", StringComparison.Ordinal) ? 0 : 1).ThenBy(name => name, StringComparer.Ordinal))
                builder.Append("using ").Append(name).AppendLine(";");
            if (usings.Count > 0) builder.AppendLine();
            var ns = _options.Namespace.Trim();
            var inNamespace = _options.WritesNamespace && ns.Length > 0;
            var fileScoped = inNamespace && _options.Version >= CSharpVersion.CSharp10;
            var indent = inNamespace && !fileScoped ? "    " : "";
            if (fileScoped) builder.Append("namespace ").Append(ns).AppendLine(";").AppendLine();
            else if (inNamespace) builder.Append("namespace ").AppendLine(ns).AppendLine("{");
            for (var i = 0; i < bodies.Count; i++)
            {
                if (i > 0) builder.AppendLine();
                foreach (var line in bodies[i].Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'))
                    builder.AppendLine(line.Length == 0 ? "" : indent + line);
            }
            if (inNamespace && !fileScoped) builder.AppendLine("}");
            return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        private void Usage(StringBuilder builder)
        {
            var root = _graph.Roots.FirstOrDefault(type => type is NamedRef named && _graph.Class(named.Key) is not null || type is ArrayRef { Item: NamedRef });
            var model = root switch { NamedRef named => _graph.Class(named.Key), ArrayRef { Item: NamedRef item } => _graph.Class(item.Key), _ => null };
            builder.AppendLine("// Generated by WpfStudio's API client.");
            if (!_stj) builder.AppendLine("// Requires the Newtonsoft.Json package.");
            if (model is null) { builder.AppendLine(); return; }
            var variable = char.ToLowerInvariant(model.Name[0]) + model.Name[1..];
            if (CSharpNames.IsKeyword(variable)) variable = "@" + variable;
            builder.AppendLine("//").AppendLine("// To read JSON with these types:").AppendLine("//");
            if (_options.WritesNamespace && _options.Namespace.Trim().Length > 0) builder.Append("//    using ").Append(_options.Namespace.Trim()).AppendLine(";").AppendLine("//");
            builder.Append("//    var ").Append(variable).Append(" = ").Append(model.Name).AppendLine(".FromJson(jsonString);").AppendLine();
        }
    }
}
