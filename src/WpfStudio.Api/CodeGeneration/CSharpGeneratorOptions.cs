namespace WpfStudio.Api.CodeGeneration;

public enum JsonFramework { SystemTextJson, NewtonsoftJson }

/// <summary>The newest C# the generated code may use. Each step adds to the one before.</summary>
public enum CSharpVersion
{
    /// <summary>Block namespaces, no nullable annotations.</summary>
    CSharp6 = 6,
    /// <summary>Adds nullable reference types.</summary>
    CSharp8 = 8,
    /// <summary>Adds file-scoped namespaces, records and init-only properties.</summary>
    CSharp10 = 10,
    /// <summary>Adds required members.</summary>
    CSharp11 = 11,
}

public enum ArrayStyle { Array, List, ReadOnlyList }
public enum NumberStyle { Double, Decimal }
/// <summary>The type for values whose shape is unknown.</summary>
public enum AnyStyle { Object, Dynamic, JsonDom }
public enum DateStyle { DateTimeOffset, DateTime }

/// <summary>How much beyond the types is written, the same choices as quicktype.</summary>
public enum OutputFeatures
{
    /// <summary>Types, serialization attributes, converters and FromJson/ToJson helpers.</summary>
    Complete,
    /// <summary>Types, serialization attributes and the converters they name; no helpers.</summary>
    AttributesOnly,
    /// <summary>Plain types in a namespace, without serialization attributes.</summary>
    JustTypesAndNamespace,
    /// <summary>Plain types only.</summary>
    JustTypes,
}

public enum PropertyDensity { Normal, Dense }
public enum FileLayout { SingleFile, FilePerType }

/// <summary>Everything that shapes generated C#, modelled on quicktype's C# options, plus where it is saved.</summary>
public sealed class CSharpGeneratorOptions
{
    // ------------------------------------------------------------------ language
    public string Namespace { get; set; } = "Models";
    public JsonFramework Framework { get; set; } = JsonFramework.SystemTextJson;
    public CSharpVersion Version { get; set; } = CSharpVersion.CSharp11;

    // ------------------------------------------------------------------ types
    public ArrayStyle Arrays { get; set; } = ArrayStyle.List;
    public NumberStyle Numbers { get; set; } = NumberStyle.Double;
    public AnyStyle Any { get; set; } = AnyStyle.Object;
    public DateStyle Dates { get; set; } = DateStyle.DateTimeOffset;
    /// <summary>Use DateOnly and TimeOnly for <c>date</c> and <c>time</c> values (.NET 6 and later).</summary>
    public bool UseDateOnlyTimeOnly { get; set; } = true;
    /// <summary>Generate C# enums for string enumerations; when off they stay strings.</summary>
    public bool GenerateEnums { get; set; } = true;

    // ------------------------------------------------------------------ output
    public OutputFeatures Features { get; set; } = OutputFeatures.Complete;
    public PropertyDensity Density { get; set; } = PropertyDensity.Normal;
    /// <summary>Records instead of classes (C# 9 and later).</summary>
    public bool UseRecords { get; set; }
    /// <summary><c>init</c> instead of <c>set</c> (C# 9 and later).</summary>
    public bool InitOnlyProperties { get; set; }
    public bool VirtualProperties { get; set; }
    /// <summary>The <c>required</c> modifier on required properties (C# 11 and later).</summary>
    public bool RequiredMembers { get; set; }
    /// <summary>Deserializing fails when a required property is missing.</summary>
    public bool CheckRequired { get; set; }
    /// <summary>Keep JSON names as property names instead of converting them to PascalCase.</summary>
    public bool KeepPropertyNames { get; set; }
    public bool DocumentationComments { get; set; } = true;
    /// <summary>Start list and dictionary properties empty instead of null.</summary>
    public bool InitializeCollections { get; set; }
    /// <summary>A class every generated class derives from, such as <c>ObservableObject</c>; empty for none.</summary>
    public string BaseClass { get; set; } = "";

    // ------------------------------------------------------------------ inference from JSON samples
    /// <summary>The name of the outermost type when generating from a JSON sample or a single schema.</summary>
    public string TopLevelName { get; set; } = "Root";
    public bool MergeSimilarClasses { get; set; } = true;
    public bool DetectMaps { get; set; } = true;
    public bool DetectEnums { get; set; } = true;
    public bool DetectUuids { get; set; } = true;
    public bool DetectDateTimes { get; set; } = true;
    public bool DetectIntegerStrings { get; set; } = true;
    public bool DetectBooleanStrings { get; set; } = true;
    public bool AllPropertiesOptional { get; set; }

    // ------------------------------------------------------------------ saving
    public FileLayout Layout { get; set; } = FileLayout.SingleFile;
    /// <summary>The folder files are saved to.</summary>
    public string OutputDirectory { get; set; } = "";
    /// <summary>The file name for a single file; with one file per type, it names the file holding shared helpers.</summary>
    public string FileName { get; set; } = "Models.cs";
    public bool OpenAfterSaving { get; set; } = true;

    public bool Nullable => Version >= CSharpVersion.CSharp8;
    public bool CanUseRecords => Version >= CSharpVersion.CSharp10;
    public bool CanUseRequired => Version >= CSharpVersion.CSharp11;
    public bool WritesAttributes => Features is OutputFeatures.Complete or OutputFeatures.AttributesOnly;
    public bool WritesNamespace => Features != OutputFeatures.JustTypes;

    public CSharpGeneratorOptions Clone() => (CSharpGeneratorOptions)MemberwiseClone();
}
