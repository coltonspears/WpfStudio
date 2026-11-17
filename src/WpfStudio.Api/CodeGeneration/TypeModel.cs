namespace WpfStudio.Api.CodeGeneration;

/// <summary>The value types JSON can carry once a schema or a sample has been read, before any C# is chosen for them.</summary>
public enum PrimitiveKind
{
    String,
    Boolean,
    Integer,
    Long,
    Number,
    DateTime,
    Date,
    Time,
    Guid,
    Uri,
    Bytes,
    /// <summary>A whole number written as a JSON string, such as <c>"42"</c>.</summary>
    IntegerString,
    /// <summary>A boolean written as a JSON string, such as <c>"true"</c>.</summary>
    BooleanString,
}

/// <summary>A type a property, array item or dictionary value has.</summary>
public abstract record TypeRef;
public sealed record PrimitiveRef(PrimitiveKind Kind) : TypeRef;
public sealed record ArrayRef(TypeRef Item, bool ItemNullable = false) : TypeRef;
public sealed record MapRef(TypeRef Value, bool ValueNullable = false) : TypeRef;
/// <summary>A class or enum of the graph, by its key.</summary>
public sealed record NamedRef(string Key) : TypeRef;
/// <summary>Anything at all: a value whose shape is not known or varies.</summary>
public sealed record AnyRef : TypeRef
{
    public static AnyRef Instance { get; } = new();
}

public sealed class PropertyModel
{
    public string JsonName { get; set; } = "";
    public TypeRef Type { get; set; } = AnyRef.Instance;
    public bool Required { get; set; }
    /// <summary>JSON null is an allowed value, as opposed to the property being absent.</summary>
    public bool Nullable { get; set; }
    public string? Description { get; set; }
    /// <summary>Filled in by the generator: the C# name.</summary>
    public string Name { get; set; } = "";
}

public sealed class ClassModel
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<PropertyModel> Properties { get; } = [];
}

public sealed class EnumMemberModel
{
    public required string Value { get; init; }
    public string Name { get; set; } = "";
}

public sealed class EnumModel
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<EnumMemberModel> Members { get; } = [];
}

/// <summary>Every class and enum to generate, and the types a document as a whole can be (for FromJson helpers).</summary>
public sealed class TypeGraph
{
    public List<ClassModel> Classes { get; } = [];
    public List<EnumModel> Enums { get; } = [];
    public List<TypeRef> Roots { get; } = [];

    public ClassModel? Class(string key) => Classes.FirstOrDefault(model => model.Key == key);
    public EnumModel? Enum(string key) => Enums.FirstOrDefault(model => model.Key == key);
}
