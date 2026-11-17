using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Api.CodeGeneration;
using WpfStudio.Api.Import;
using WpfStudio.Api.Models;
using WpfStudio.Api.Storage;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Api;

/// <summary>What models are generated from.</summary>
public enum CodeSource { Schemas, Sample, JsonSchema }

/// <summary>A schema of the OpenAPI document, ticked to generate a type for it.</summary>
public sealed partial class SchemaChoice(SchemaEntry entry) : ObservableObject
{
    public string Name => entry.Name;
    public string Reference => entry.Reference;
    public string Kind => entry.Kind;
    public string Description => entry.Description ?? "";
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;
}

/// <summary>
/// Generates C# models, like quicktype, for System.Text.Json or Newtonsoft.Json, from an OpenAPI document's schemas,
/// a JSON sample such as a response, or a JSON Schema. The preview follows every option as it changes, and the result
/// is saved as one file or one file per type to a folder of the open solution or anywhere else.
/// </summary>
public sealed partial class ApiCodeGeneratorViewModel : ObservableObject
{
    private const string OptionsFile = "codegen.json";
    private static readonly HashSet<string> Inputs = new(StringComparer.Ordinal) { nameof(Source), nameof(SampleText), nameof(JsonSchemaText) };
    private readonly ApiClientViewModel _owner;
    private readonly ApiStore _store;
    private readonly IFileDialogService _files;
    private readonly IClipboardService _clipboard;
    private readonly SemaphoreSlim _generating = new(1, 1);
    private CancellationTokenSource? _pending;
    private JsonNode? _parsedSpecification;
    private IReadOnlyList<GeneratedFile> _generated = [];
    private bool _optionsLoaded;
    private bool _loading;
    private int _version;
    private WorkspaceSnapshot? _workspace;

    internal ApiCodeGeneratorViewModel(ApiClientViewModel owner, ApiStore store, IFileDialogService files, IClipboardService clipboard)
    {
        _owner = owner; _store = store; _files = files; _clipboard = clipboard;
        Apply(new CSharpGeneratorOptions());
    }

    // ------------------------------------------------------------------ choices

    public IReadOnlyList<Choice<JsonFramework>> Frameworks { get; } =
    [
        new(JsonFramework.SystemTextJson, "System.Text.Json", "Built into .NET. Attributes from System.Text.Json.Serialization."),
        new(JsonFramework.NewtonsoftJson, "Newtonsoft.Json", "Json.NET. Needs the Newtonsoft.Json package."),
    ];
    public IReadOnlyList<Choice<CSharpVersion>> Versions { get; } =
    [
        new(CSharpVersion.CSharp11, "C# 11 or later", "File-scoped namespaces, nullable annotations, records, init and required members."),
        new(CSharpVersion.CSharp10, "C# 10", "File-scoped namespaces, nullable annotations, records and init."),
        new(CSharpVersion.CSharp8, "C# 8", "Nullable annotations in block namespaces."),
        new(CSharpVersion.CSharp6, "C# 6", "No nullable annotations, for older projects."),
    ];
    public IReadOnlyList<Choice<ArrayStyle>> ArrayStyles { get; } = [new(ArrayStyle.List, "List<T>"), new(ArrayStyle.Array, "T[]"), new(ArrayStyle.ReadOnlyList, "IReadOnlyList<T>")];
    public IReadOnlyList<Choice<NumberStyle>> NumberStyles { get; } = [new(NumberStyle.Double, "double"), new(NumberStyle.Decimal, "decimal")];
    public IReadOnlyList<Choice<AnyStyle>> AnyStyles { get; } = [new(AnyStyle.Object, "object"), new(AnyStyle.Dynamic, "dynamic"), new(AnyStyle.JsonDom, "JsonElement / JToken")];
    public IReadOnlyList<Choice<DateStyle>> DateStyles { get; } = [new(DateStyle.DateTimeOffset, "DateTimeOffset"), new(DateStyle.DateTime, "DateTime")];
    public IReadOnlyList<Choice<OutputFeatures>> FeatureChoices { get; } =
    [
        new(OutputFeatures.Complete, "Complete", "Types, attributes, converters and FromJson/ToJson helpers."),
        new(OutputFeatures.AttributesOnly, "Attributes only", "Types with serialization attributes and the converters they need; no helpers."),
        new(OutputFeatures.JustTypesAndNamespace, "Just types and namespace", "Plain types in a namespace, without attributes."),
        new(OutputFeatures.JustTypes, "Just types", "Plain types only, without a namespace."),
    ];
    public IReadOnlyList<Choice<PropertyDensity>> Densities { get; } = [new(PropertyDensity.Normal, "Normal"), new(PropertyDensity.Dense, "Dense")];
    public IReadOnlyList<Choice<FileLayout>> Layouts { get; } = [new(FileLayout.SingleFile, "One file"), new(FileLayout.FilePerType, "One file per type")];

    // ------------------------------------------------------------------ state

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSchemaSource), nameof(IsSampleSource), nameof(IsJsonSchemaSource), nameof(UsesTopLevelName))] public partial CodeSource Source { get; set; }
    [ObservableProperty] public partial string SpecificationName { get; set; } = "";
    [ObservableProperty] public partial bool HasSpecification { get; set; }
    public ObservableCollection<SchemaChoice> Schemas { get; } = [];
    [ObservableProperty] public partial string SchemaFilter { get; set; } = "";
    [ObservableProperty] public partial string SampleText { get; set; } = "";
    [ObservableProperty] public partial string JsonSchemaText { get; set; } = "";
    [ObservableProperty] public partial string Preview { get; set; } = "";
    [ObservableProperty] public partial string Problems { get; set; } = "";
    [ObservableProperty] public partial bool IsGenerating { get; set; }

    // Language
    [ObservableProperty] public partial string Namespace { get; set; } = "";
    [ObservableProperty] public partial JsonFramework Framework { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUseRecords), nameof(CanUseRequired))] public partial CSharpVersion Version { get; set; }
    // Types
    [ObservableProperty] public partial ArrayStyle Arrays { get; set; }
    [ObservableProperty] public partial NumberStyle Numbers { get; set; }
    [ObservableProperty] public partial AnyStyle Any { get; set; }
    [ObservableProperty] public partial DateStyle Dates { get; set; }
    [ObservableProperty] public partial bool UseDateOnlyTimeOnly { get; set; }
    [ObservableProperty] public partial bool GenerateEnums { get; set; }
    // Output
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FeaturesExplanation))] public partial OutputFeatures Features { get; set; }
    [ObservableProperty] public partial PropertyDensity Density { get; set; }
    [ObservableProperty] public partial bool UseRecords { get; set; }
    [ObservableProperty] public partial bool InitOnlyProperties { get; set; }
    [ObservableProperty] public partial bool VirtualProperties { get; set; }
    [ObservableProperty] public partial bool RequiredMembers { get; set; }
    [ObservableProperty] public partial bool CheckRequired { get; set; }
    [ObservableProperty] public partial bool KeepPropertyNames { get; set; }
    [ObservableProperty] public partial bool DocumentationComments { get; set; }
    [ObservableProperty] public partial bool InitializeCollections { get; set; }
    [ObservableProperty] public partial string BaseClass { get; set; } = "";
    // Inference
    [ObservableProperty] public partial string TopLevelName { get; set; } = "";
    [ObservableProperty] public partial bool MergeSimilarClasses { get; set; }
    [ObservableProperty] public partial bool DetectMaps { get; set; }
    [ObservableProperty] public partial bool DetectEnums { get; set; }
    [ObservableProperty] public partial bool DetectUuids { get; set; }
    [ObservableProperty] public partial bool DetectDateTimes { get; set; }
    [ObservableProperty] public partial bool DetectIntegerStrings { get; set; }
    [ObservableProperty] public partial bool DetectBooleanStrings { get; set; }
    [ObservableProperty] public partial bool AllPropertiesOptional { get; set; }
    // Saving
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SaveSummary), nameof(FileNameLabel))] public partial FileLayout Layout { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SaveSummary))] public partial string OutputDirectory { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SaveSummary))] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial bool OpenAfterSaving { get; set; }

    public bool IsSchemaSource => Source == CodeSource.Schemas;
    public bool IsSampleSource => Source == CodeSource.Sample;
    public bool IsJsonSchemaSource => Source == CodeSource.JsonSchema;
    public bool UsesTopLevelName => Source != CodeSource.Schemas;
    public bool CanUseRecords => Version >= CSharpVersion.CSharp10;
    public bool CanUseRequired => Version >= CSharpVersion.CSharp11;
    public string FeaturesExplanation => FeatureChoices.FirstOrDefault(choice => choice.Value == Features)?.Description ?? "";
    public string FileNameLabel => Layout == FileLayout.SingleFile ? "File name" : "Shared helpers file";
    public int SelectedSchemaCount => Schemas.Count(schema => schema.IsSelected);
    public string SchemaSummary => $"{SelectedSchemaCount} of {Schemas.Count} schemas";
    public string SaveSummary
    {
        get
        {
            var folder = OutputDirectory.Trim().Length == 0 ? "a folder you choose" : OutputDirectory.Trim();
            if (_generated.Count == 0) return "Nothing to save yet.";
            return _generated.Count == 1 ? $"Saves {_generated[0].FileName} to {folder}." : $"Saves {_generated.Count} files to {folder}: {string.Join(", ", _generated.Take(6).Select(file => file.FileName))}{(_generated.Count > 6 ? ", …" : "")}.";
        }
    }

    // ------------------------------------------------------------------ opening

    internal void WorkspaceChanged(WorkspaceSnapshot? workspace) => _workspace = workspace;

    /// <summary>Opens the generator for a collection's schemas, a request's response or body, or a blank sample.</summary>
    internal async Task OpenAsync(ApiCollection? collection, string? specification, ApiRequestTabViewModel? tab)
    {
        await LoadOptionsAsync();
        _loading = true;
        try
        {
            _parsedSpecification = null;
            HasSpecification = specification is not null;
            SpecificationName = collection?.Name ?? "";
            Schemas.Clear();
            if (specification is not null)
            {
                try
                {
                    _parsedSpecification = await Task.Run(() => StructuredText.Parse(specification));
                    foreach (var entry in SchemaTypeBuilder.List(_parsedSpecification)) Schemas.Add(Track(new SchemaChoice(entry)));
                }
                catch (FormatException exception) { Problems = "The saved OpenAPI document could not be read: " + exception.Message; HasSpecification = false; }
            }
            var wanted = new[] { tab?.ResponseSchema, tab?.RequestSchema }.Where(name => name is not null).Select(name => name!.TrimEnd('[', ']')).ToHashSet(StringComparer.Ordinal);
            if (tab?.Response?.Response is { IsJson: true } response)
            {
                Source = CodeSource.Sample;
                SampleText = response.Pretty.Length > 0 ? response.Pretty : response.Text;
                TopLevelName = wanted.FirstOrDefault(name => name == tab.ResponseSchema?.TrimEnd('[', ']')) ?? CSharpNames.Pascal(tab.Name, "Root") + "Response";
            }
            else if (HasSpecification && wanted.Count > 0 && Schemas.Any(schema => wanted.Contains(schema.Name)))
            {
                Source = CodeSource.Schemas;
                foreach (var schema in Schemas) schema.IsSelected = wanted.Contains(schema.Name);
            }
            else if (tab is { BodyMode: BodyMode.Json } && tab.Body.Trim().Length > 0)
            {
                Source = CodeSource.Sample;
                SampleText = tab.Body;
                TopLevelName = CSharpNames.Pascal(tab.Name, "Root") + "Request";
            }
            else if (HasSpecification)
            {
                Source = CodeSource.Schemas;
                foreach (var schema in Schemas) schema.IsSelected = true;
            }
            else Source = Source == CodeSource.Schemas ? CodeSource.Sample : Source;
            var stem = Source == CodeSource.Schemas ? CSharpNames.Pascal(collection?.Name ?? "Models", "Models") : CSharpNames.Pascal(TopLevelName, "Models");
            FileName = (stem.EndsWith("Models", StringComparison.Ordinal) ? stem : stem + (Source == CodeSource.Schemas ? "Models" : "")) + ".cs";
            if ((_workspace?.Projects.FirstOrDefault(project => project.IsExecutable) ?? _workspace?.Projects.FirstOrDefault()) is { } project)
            {
                if (Namespace is "" or "Models") Namespace = NamespaceFor(project.Name) + ".Models";
                if (OutputDirectory.Trim().Length == 0 || !Directory.Exists(OutputDirectory)) OutputDirectory = Path.Combine(Path.GetDirectoryName(project.ProjectPath)!, "Models");
            }
            SchemaFilter = "";
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(SchemaSummary));
        IsOpen = true;
        Regenerate();
    }

    /// <summary>A project name as a namespace: each dot-separated part made a valid identifier.</summary>
    internal static string NamespaceFor(string projectName) =>
        string.Join('.', projectName.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part => CSharpNames.Verbatim(part, "App").TrimStart('@'))) is { Length: > 0 } name ? name : "App";

    private SchemaChoice Track(SchemaChoice choice)
    {
        choice.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SchemaChoice.IsSelected)) return;
            OnPropertyChanged(nameof(SchemaSummary));
            if (!_loading) Regenerate();
        };
        return choice;
    }

    partial void OnSchemaFilterChanged(string value)
    {
        foreach (var schema in Schemas) schema.IsVisible = value.Trim().Length == 0 || schema.Name.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null) return;
        if (Inputs.Contains(e.PropertyName) || OptionNames.Contains(e.PropertyName)) Regenerate();
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task CloseAsync()
    {
        IsOpen = false;
        _pending?.Cancel();
        await SaveOptionsAsync();
    }

    [RelayCommand] private void ShowSource(string? source) { if (Enum.TryParse<CodeSource>(source, out var parsed)) Source = parsed; }
    [RelayCommand] private void SelectAllSchemas() { _loading = true; try { foreach (var schema in Schemas.Where(schema => schema.IsVisible)) schema.IsSelected = true; } finally { _loading = false; } Regenerate(); }
    [RelayCommand] private void SelectNoSchemas() { _loading = true; try { foreach (var schema in Schemas) schema.IsSelected = false; } finally { _loading = false; } Regenerate(); }

    [RelayCommand]
    private void ResetOptions()
    {
        var keep = (Namespace, OutputDirectory, FileName, TopLevelName);
        Apply(new CSharpGeneratorOptions { Namespace = keep.Namespace, OutputDirectory = keep.OutputDirectory, FileName = keep.FileName, TopLevelName = keep.TopLevelName });
        Regenerate();
    }

    [RelayCommand]
    private void Copy()
    {
        if (Preview.Length == 0) return;
        _owner.Report(_clipboard.TrySetText(Preview) ? "Generated code copied" : "Another application is using the clipboard. Try again.");
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var folder = await _files.OpenFolderAsync("Choose where to save the generated files", Directory.Exists(OutputDirectory) ? OutputDirectory : _owner.WorkspaceDirectory);
        if (folder is not null) OutputDirectory = folder;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await RegenerateNowAsync();
        if (_generated.Count == 0) { _owner.Report("There is nothing to save: " + (Problems.Length > 0 ? Problems : "no types were generated.")); return; }
        if (OutputDirectory.Trim().Length == 0) await BrowseFolderAsync();
        var folder = OutputDirectory.Trim();
        if (folder.Length == 0) return;
        try
        {
            folder = Path.GetFullPath(folder);
            var targets = _generated.Select(file => (File: file, Path: Path.Combine(folder, file.FileName))).ToList();
            var existing = targets.Where(target => File.Exists(target.Path)).Select(target => target.File.FileName).ToList();
            if (existing.Count > 0 && !await _owner.ConfirmAsync("Replace files", $"{string.Join(", ", existing.Take(8))}{(existing.Count > 8 ? $" and {existing.Count - 8} more" : "")} already exist{(existing.Count == 1 ? "s" : "")} in {folder}. Replace {(existing.Count == 1 ? "it" : "them")}?"))
                return;
            Directory.CreateDirectory(folder);
            foreach (var (file, path) in targets) await File.WriteAllTextAsync(path, file.Content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _owner.Report(targets.Count == 1 ? $"Saved {targets[0].Path}" : $"Saved {targets.Count} files to {folder}");
            if (OpenAfterSaving) foreach (var (_, path) in targets.Take(12)) _owner.OpenFile(path);
            await SaveOptionsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _owner.Report("The files could not be saved: " + exception.Message); }
    }

    // ------------------------------------------------------------------ generating

    private void Regenerate()
    {
        if (!IsOpen) return;
        _pending?.Cancel();
        _pending?.Dispose();
        var pending = _pending = new CancellationTokenSource();
        RegenerateLaterAsync(pending.Token).Forget(fault => Problems = fault.Message);
    }

    private async Task RegenerateLaterAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(250, cancellationToken); }
        catch (OperationCanceledException) { return; }
        await RegenerateNowAsync();
    }

    private async Task RegenerateNowAsync()
    {
        var version = Interlocked.Increment(ref _version);
        var options = ToOptions();
        var source = Source;
        var references = Schemas.Where(schema => schema.IsSelected).Select(schema => schema.Reference).ToList();
        var sample = SampleText;
        var schemaText = JsonSchemaText;
        var specification = _parsedSpecification;
        IsGenerating = true;
        await _generating.WaitAsync();
        try
        {
            var (code, problem) = await Task.Run(() => Generate(source, specification, references, sample, schemaText, options));
            if (version != _version) return;
            _generated = code?.Files ?? [];
            Preview = code?.Preview ?? "";
            Problems = problem ?? string.Join(" ", code?.Warnings ?? []);
            OnPropertyChanged(nameof(SaveSummary));
        }
        finally
        {
            _generating.Release();
            if (version == _version) IsGenerating = false;
        }
    }

    internal static (GeneratedCode? Code, string? Problem) Generate(CodeSource source, JsonNode? specification, IReadOnlyList<string> references, string sample, string schemaText, CSharpGeneratorOptions options)
    {
        try
        {
            TypeGraph graph;
            switch (source)
            {
                case CodeSource.Schemas:
                    if (specification is null) return (null, "This collection was not imported from an OpenAPI document. Paste a JSON sample or a JSON Schema instead.");
                    if (references.Count == 0) return (null, "Tick the schemas to generate types for.");
                    graph = SchemaTypeBuilder.FromReferences(specification, references, options);
                    break;
                case CodeSource.JsonSchema:
                    if (schemaText.Trim().Length == 0) return (null, "Paste a JSON Schema to generate types from.");
                    graph = SchemaTypeBuilder.FromSchema(StructuredText.Parse(schemaText), options);
                    break;
                default:
                    if (sample.Trim().Length == 0) return (null, "Paste a JSON sample, such as a response body, to generate types from.");
                    graph = JsonSampleInferrer.Infer([JsonNode.Parse(sample, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })], options);
                    break;
            }
            return (CSharpCodeGenerator.Generate(graph, options), null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        { return (null, "The input could not be read: " + exception.Message); }
    }

    // ------------------------------------------------------------------ options

    private static readonly HashSet<string> OptionNames = new(StringComparer.Ordinal)
    {
        nameof(Namespace), nameof(Framework), nameof(Version), nameof(Arrays), nameof(Numbers), nameof(Any), nameof(Dates), nameof(UseDateOnlyTimeOnly), nameof(GenerateEnums),
        nameof(Features), nameof(Density), nameof(UseRecords), nameof(InitOnlyProperties), nameof(VirtualProperties), nameof(RequiredMembers), nameof(CheckRequired),
        nameof(KeepPropertyNames), nameof(DocumentationComments), nameof(InitializeCollections), nameof(BaseClass), nameof(TopLevelName), nameof(MergeSimilarClasses),
        nameof(DetectMaps), nameof(DetectEnums), nameof(DetectUuids), nameof(DetectDateTimes), nameof(DetectIntegerStrings), nameof(DetectBooleanStrings),
        nameof(AllPropertiesOptional), nameof(Layout), nameof(FileName),
    };

    public CSharpGeneratorOptions ToOptions() => new()
    {
        Namespace = Namespace.Trim(), Framework = Framework, Version = Version, Arrays = Arrays, Numbers = Numbers, Any = Any, Dates = Dates, UseDateOnlyTimeOnly = UseDateOnlyTimeOnly,
        GenerateEnums = GenerateEnums, Features = Features, Density = Density, UseRecords = UseRecords, InitOnlyProperties = InitOnlyProperties, VirtualProperties = VirtualProperties,
        RequiredMembers = RequiredMembers, CheckRequired = CheckRequired, KeepPropertyNames = KeepPropertyNames, DocumentationComments = DocumentationComments,
        InitializeCollections = InitializeCollections, BaseClass = BaseClass.Trim(), TopLevelName = TopLevelName.Trim(), MergeSimilarClasses = MergeSimilarClasses, DetectMaps = DetectMaps,
        DetectEnums = DetectEnums, DetectUuids = DetectUuids, DetectDateTimes = DetectDateTimes, DetectIntegerStrings = DetectIntegerStrings, DetectBooleanStrings = DetectBooleanStrings,
        AllPropertiesOptional = AllPropertiesOptional, Layout = Layout, OutputDirectory = OutputDirectory.Trim(), FileName = FileName.Trim(), OpenAfterSaving = OpenAfterSaving,
    };

    private void Apply(CSharpGeneratorOptions options)
    {
        var loading = _loading;
        _loading = true;
        try
        {
            Namespace = options.Namespace; Framework = options.Framework; Version = options.Version; Arrays = options.Arrays; Numbers = options.Numbers; Any = options.Any; Dates = options.Dates;
            UseDateOnlyTimeOnly = options.UseDateOnlyTimeOnly; GenerateEnums = options.GenerateEnums; Features = options.Features; Density = options.Density; UseRecords = options.UseRecords;
            InitOnlyProperties = options.InitOnlyProperties; VirtualProperties = options.VirtualProperties; RequiredMembers = options.RequiredMembers; CheckRequired = options.CheckRequired;
            KeepPropertyNames = options.KeepPropertyNames; DocumentationComments = options.DocumentationComments; InitializeCollections = options.InitializeCollections; BaseClass = options.BaseClass;
            TopLevelName = options.TopLevelName; MergeSimilarClasses = options.MergeSimilarClasses; DetectMaps = options.DetectMaps; DetectEnums = options.DetectEnums; DetectUuids = options.DetectUuids;
            DetectDateTimes = options.DetectDateTimes; DetectIntegerStrings = options.DetectIntegerStrings; DetectBooleanStrings = options.DetectBooleanStrings; AllPropertiesOptional = options.AllPropertiesOptional;
            Layout = options.Layout; OutputDirectory = options.OutputDirectory; FileName = options.FileName; OpenAfterSaving = options.OpenAfterSaving;
        }
        finally { _loading = loading; }
    }

    private async Task LoadOptionsAsync()
    {
        if (_optionsLoaded) return;
        _optionsLoaded = true;
        if (await _store.ReadSettingsAsync<CSharpGeneratorOptions>(OptionsFile) is { } saved) Apply(saved);
    }

    internal async Task SaveOptionsAsync()
    {
        if (!_optionsLoaded) return;
        try { await _store.WriteSettingsAsync(OptionsFile, ToOptions()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _owner.Report("The generator's options could not be saved: " + exception.Message); }
    }
}
