using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WpfStudio.Api.CodeGeneration;

/// <summary>
/// Infers types from example JSON, as quicktype does: every object seen at one place in the document is merged, so a
/// property missing from some becomes optional and one that is sometimes null becomes nullable. Strings that are all
/// dates, GUIDs, whole numbers or booleans get those types; a handful of repeated words becomes an enum; objects
/// keyed by ids become dictionaries; and classes with nearly the same properties become one.
/// </summary>
public sealed partial class JsonSampleInferrer
{
    private const int MaxDistinct = 64;
    private readonly CSharpGeneratorOptions _options;
    private readonly TypeGraph _graph = new();
    private readonly NameScope _keys = new();

    private JsonSampleInferrer(CSharpGeneratorOptions options) => _options = options;

    /// <param name="samples">One or more examples of the same document, such as several responses from one endpoint.</param>
    public static TypeGraph Infer(IEnumerable<JsonNode?> samples, CSharpGeneratorOptions options)
    {
        var shape = new Shape();
        foreach (var sample in samples) shape.Add(sample);
        var inferrer = new JsonSampleInferrer(options);
        var top = options.TopLevelName.Trim().Length > 0 ? CSharpNames.Pascal(options.TopLevelName.Trim()) : "Root";
        var (type, _) = inferrer.Convert(shape, top, arrayItemName: top);
        inferrer._graph.Roots.Add(type);
        if (options.MergeSimilarClasses) ClassMerger.Merge(inferrer._graph);
        return inferrer._graph;
    }

    private (TypeRef Type, bool Nullable) Convert(Shape shape, string nameHint, string? arrayItemName = null)
    {
        var kinds = (shape.Booleans > 0 ? 1 : 0) + (shape.Numbers > 0 ? 1 : 0) + (shape.Strings is not null ? 1 : 0) + (shape.Object is not null ? 1 : 0) + (shape.Items is not null ? 1 : 0);
        if (kinds == 0) return (AnyRef.Instance, true);
        if (kinds > 1) return (AnyRef.Instance, shape.Nulls > 0);
        var nullable = shape.Nulls > 0;
        if (shape.Booleans > 0) return (new PrimitiveRef(PrimitiveKind.Boolean), nullable);
        if (shape.Numbers > 0) return (new PrimitiveRef(shape.AllIntegers ? PrimitiveKind.Long : PrimitiveKind.Number), nullable);
        if (shape.Strings is { } strings) return (StringType(strings, nameHint), nullable);
        if (shape.Items is { } items)
        {
            var (item, itemNullable) = Convert(items, arrayItemName ?? CSharpNames.Singular(nameHint));
            return (new ArrayRef(item, itemNullable), nullable);
        }
        var obj = shape.Object!;
        if (_options.DetectMaps && IsMap(obj))
        {
            var merged = new Shape();
            foreach (var property in obj.Properties.Values) merged.Absorb(property.Shape);
            var (value, valueNullable) = Convert(merged, CSharpNames.Singular(nameHint) + "Value");
            return (new MapRef(value, valueNullable), nullable);
        }
        var model = new ClassModel { Key = _keys.Claim(nameHint), Name = nameHint };
        _graph.Classes.Add(model);
        foreach (var name in obj.Order)
        {
            var property = obj.Properties[name];
            var (type, propertyNullable) = Convert(property.Shape, CSharpNames.Pascal(name, "Value"));
            model.Properties.Add(new PropertyModel
            {
                JsonName = name,
                Type = type,
                Nullable = propertyNullable,
                Required = !_options.AllPropertiesOptional && property.Seen == obj.Count,
            });
        }
        return (new NamedRef(model.Key), nullable);
    }

    private TypeRef StringType(StringShape strings, string nameHint)
    {
        if (strings.Count == 0) return new PrimitiveRef(PrimitiveKind.String);
        if (_options.DetectDateTimes && strings.AllDateTime) return new PrimitiveRef(PrimitiveKind.DateTime);
        if (_options.DetectDateTimes && strings.AllDate) return new PrimitiveRef(PrimitiveKind.Date);
        if (_options.DetectDateTimes && strings.AllTime) return new PrimitiveRef(PrimitiveKind.Time);
        if (_options.DetectUuids && strings.AllGuid) return new PrimitiveRef(PrimitiveKind.Guid);
        if (_options.DetectIntegerStrings && strings.AllInteger) return new PrimitiveRef(PrimitiveKind.IntegerString);
        if (_options.DetectBooleanStrings && strings.AllBoolean) return new PrimitiveRef(PrimitiveKind.BooleanString);
        // Few distinct words, each seen more than once: an enumeration rather than free text.
        if (_options.DetectEnums && _options.GenerateEnums && strings.Distinct.Count is > 0 and <= 8 && strings.Count >= 3 && strings.Count >= strings.Distinct.Count * 2
            && !strings.Overflowed && strings.Distinct.All(value => EnumWord().IsMatch(value)))
        {
            var model = new EnumModel { Key = _keys.Claim(nameHint), Name = nameHint };
            foreach (var value in strings.Distinct) model.Members.Add(new EnumMemberModel { Value = value });
            _graph.Enums.Add(model);
            return new NamedRef(model.Key);
        }
        return new PrimitiveRef(PrimitiveKind.String);
    }

    // Keys that are ids, numbers or dates name entries in a dictionary, not properties of a class.
    private static bool IsMap(ObjectShape obj)
    {
        if (obj.Order.Count == 0) return false;
        if (obj.Order.All(key => !IdentifierKey().IsMatch(key))) return true;
        if (obj.Order.Count < 20) return false;
        var first = obj.Properties[obj.Order[0]].Shape;
        return obj.Properties.Values.All(property => property.Shape.SameKind(first));
    }

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_\-$.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierKey();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\- ]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnumWord();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?([Zz]|[+\-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimePattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^\d{2}:\d{2}(:\d{2}(\.\d+)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^(0|-?[1-9]\d{0,17})$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    // ------------------------------------------------------------------ shapes

    /// <summary>Everything seen at one place in the samples.</summary>
    private sealed class Shape
    {
        public int Nulls;
        public int Booleans;
        public int Numbers;
        public bool AllIntegers = true;
        public StringShape? Strings;
        public ObjectShape? Object;
        public Shape? Items;

        public void Add(JsonNode? node)
        {
            switch (node)
            {
                case null: Nulls++; break;
                case JsonObject obj:
                    Object ??= new ObjectShape();
                    Object.Add(obj);
                    break;
                case JsonArray array:
                    Items ??= new Shape();
                    foreach (var item in array) Items.Add(item);
                    break;
                case JsonValue value:
                    if (value.TryGetValue<bool>(out _)) Booleans++;
                    else if (value.TryGetValue<string>(out var text)) (Strings ??= new StringShape()).Add(text);
                    else
                    {
                        Numbers++;
                        var raw = value.ToJsonString();
                        if (raw.Contains('.', StringComparison.Ordinal) || raw.Contains('e', StringComparison.OrdinalIgnoreCase) || !long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) AllIntegers = false;
                    }
                    break;
            }
        }

        public void Absorb(Shape other)
        {
            Nulls += other.Nulls; Booleans += other.Booleans; Numbers += other.Numbers; AllIntegers &= other.AllIntegers;
            if (other.Strings is not null) (Strings ??= new StringShape()).Absorb(other.Strings);
            if (other.Object is not null) (Object ??= new ObjectShape()).Absorb(other.Object);
            if (other.Items is not null) (Items ??= new Shape()).Absorb(other.Items);
        }

        public bool SameKind(Shape other) =>
            (Booleans > 0) == (other.Booleans > 0) && (Numbers > 0) == (other.Numbers > 0) && (Strings is null) == (other.Strings is null)
            && (Object is null) == (other.Object is null) && (Items is null) == (other.Items is null);
    }

    private sealed class StringShape
    {
        public int Count;
        public List<string> Distinct { get; } = [];
        public bool Overflowed;
        public bool AllDateTime = true, AllDate = true, AllTime = true, AllGuid = true, AllInteger = true, AllBoolean = true;

        public void Add(string value)
        {
            Count++;
            if (!Overflowed && !Distinct.Contains(value)) { if (Distinct.Count < MaxDistinct) Distinct.Add(value); else Overflowed = true; }
            AllDateTime &= DateTimePattern().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
            AllDate &= DatePattern().IsMatch(value) && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            AllTime &= TimePattern().IsMatch(value) && TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out _);
            AllGuid &= value.Length == 36 && Guid.TryParseExact(value, "D", out _);
            AllInteger &= IntegerPattern().IsMatch(value);
            AllBoolean &= value is "true" or "false";
        }

        public void Absorb(StringShape other)
        {
            Count += other.Count;
            foreach (var value in other.Distinct) if (!Distinct.Contains(value)) { if (Distinct.Count < MaxDistinct) Distinct.Add(value); else Overflowed = true; }
            Overflowed |= other.Overflowed;
            AllDateTime &= other.AllDateTime; AllDate &= other.AllDate; AllTime &= other.AllTime; AllGuid &= other.AllGuid; AllInteger &= other.AllInteger; AllBoolean &= other.AllBoolean;
        }
    }

    private sealed class ObjectShape
    {
        public int Count;
        public List<string> Order { get; } = [];
        public Dictionary<string, PropertyShape> Properties { get; } = new(StringComparer.Ordinal);

        public void Add(JsonObject obj)
        {
            Count++;
            foreach (var (name, value) in obj)
            {
                if (!Properties.TryGetValue(name, out var property)) { Properties[name] = property = new PropertyShape(); Order.Add(name); }
                property.Seen++;
                property.Shape.Add(value);
            }
        }

        public void Absorb(ObjectShape other)
        {
            Count += other.Count;
            foreach (var name in other.Order)
            {
                if (!Properties.TryGetValue(name, out var property)) { Properties[name] = property = new PropertyShape(); Order.Add(name); }
                property.Seen += other.Properties[name].Seen;
                property.Shape.Absorb(other.Properties[name].Shape);
            }
        }
    }

    private sealed class PropertyShape
    {
        public int Seen;
        public Shape Shape { get; } = new();
    }
}

/// <summary>
/// Combines classes with the same or nearly the same properties, so <c>billingAddress</c> and <c>shippingAddress</c>
/// share one <c>Address</c> class. A property one of them lacks becomes optional.
/// </summary>
internal static class ClassMerger
{
    public static void Merge(TypeGraph graph)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            var classes = graph.Classes.ToList();
            for (var i = 0; i < classes.Count; i++)
            {
                if (mapping.ContainsKey(classes[i].Key)) continue;
                for (var j = i + 1; j < classes.Count; j++)
                    if (!mapping.ContainsKey(classes[j].Key) && Similar(classes[i], classes[j])) mapping[classes[j].Key] = classes[i].Key;
            }
            if (mapping.Count == 0) return;
            foreach (var (from, into) in mapping)
            {
                var source = graph.Class(from)!;
                var target = graph.Class(into)!;
                target.Name = SharedName(target.Name, source.Name);
                foreach (var property in target.Properties.Where(property => !source.Properties.Any(other => other.JsonName == property.JsonName))) property.Required = false;
                foreach (var property in source.Properties)
                {
                    if (target.Properties.FirstOrDefault(existing => existing.JsonName == property.JsonName) is { } existing)
                    {
                        existing.Type = Unify(Rename(existing.Type, mapping), Rename(property.Type, mapping));
                        existing.Required &= property.Required;
                        existing.Nullable |= property.Nullable;
                    }
                    else target.Properties.Add(new PropertyModel { JsonName = property.JsonName, Type = property.Type, Nullable = property.Nullable, Required = false, Description = property.Description });
                }
                graph.Classes.Remove(source);
            }
            foreach (var model in graph.Classes) foreach (var property in model.Properties) property.Type = Rename(property.Type, mapping);
            for (var i = 0; i < graph.Roots.Count; i++) graph.Roots[i] = Rename(graph.Roots[i], mapping);
        }
    }

    /// <summary>The words two names end with: BillingAddress and ShippingAddress share Address.</summary>
    internal static string SharedName(string a, string b)
    {
        var first = CSharpNames.Words(a).ToList();
        var second = CSharpNames.Words(b).ToList();
        var shared = 0;
        while (shared < first.Count && shared < second.Count && string.Equals(first[^(shared + 1)], second[^(shared + 1)], StringComparison.OrdinalIgnoreCase)) shared++;
        return shared == 0 ? a : string.Concat(first.Skip(first.Count - shared));
    }

    private static bool Similar(ClassModel a, ClassModel b)
    {
        var namesA = a.Properties.Select(property => property.JsonName).ToHashSet(StringComparer.Ordinal);
        var namesB = b.Properties.Select(property => property.JsonName).ToHashSet(StringComparer.Ordinal);
        if (namesA.Count == 0 || namesB.Count == 0) return false;
        var common = namesA.Intersect(namesB).ToList();
        var union = namesA.Union(namesB).Count();
        var close = namesA.SetEquals(namesB) || (namesA.Count >= 3 && namesB.Count >= 3 && common.Count >= 0.75 * union);
        if (!close) return false;
        // Shared properties must agree on what kind of value they hold.
        return common.All(name => Compatible(a.Properties.First(p => p.JsonName == name).Type, b.Properties.First(p => p.JsonName == name).Type));
    }

    private static bool Compatible(TypeRef a, TypeRef b) => (a, b) switch
    {
        (AnyRef, _) or (_, AnyRef) => true,
        (PrimitiveRef x, PrimitiveRef y) => x.Kind == y.Kind || IsNumeric(x.Kind) && IsNumeric(y.Kind),
        (ArrayRef x, ArrayRef y) => Compatible(x.Item, y.Item),
        (MapRef x, MapRef y) => Compatible(x.Value, y.Value),
        (NamedRef, NamedRef) => true,
        _ => false,
    };

    private static TypeRef Unify(TypeRef a, TypeRef b) => (a, b) switch
    {
        _ when a == b => a,
        (AnyRef, _) => b,
        (_, AnyRef) => a,
        (PrimitiveRef x, PrimitiveRef y) when IsNumeric(x.Kind) && IsNumeric(y.Kind) => new PrimitiveRef(PrimitiveKind.Number),
        (ArrayRef x, ArrayRef y) => new ArrayRef(Unify(x.Item, y.Item), x.ItemNullable || y.ItemNullable),
        (MapRef x, MapRef y) => new MapRef(Unify(x.Value, y.Value), x.ValueNullable || y.ValueNullable),
        _ => AnyRef.Instance,
    };

    private static bool IsNumeric(PrimitiveKind kind) => kind is PrimitiveKind.Integer or PrimitiveKind.Long or PrimitiveKind.Number;

    private static TypeRef Rename(TypeRef type, Dictionary<string, string> mapping) => type switch
    {
        NamedRef named when mapping.TryGetValue(named.Key, out var into) => new NamedRef(into),
        ArrayRef array => array with { Item = Rename(array.Item, mapping) },
        MapRef map => map with { Value = Rename(map.Value, mapping) },
        _ => type,
    };
}
