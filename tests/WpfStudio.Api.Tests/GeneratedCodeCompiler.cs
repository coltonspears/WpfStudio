using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Api.CodeGeneration;

namespace WpfStudio.Api.Tests;

/// <summary>Compiles generated C# at the language version it was generated for, failing on any error or warning.</summary>
internal static class GeneratedCodeCompiler
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        paths.Add(typeof(Newtonsoft.Json.JsonConvert).Assembly.Location);
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();
    });

    public static Assembly Compile(GeneratedCode code, CSharpVersion version)
    {
        var language = version switch
        {
            CSharpVersion.CSharp6 => LanguageVersion.CSharp6,
            CSharpVersion.CSharp8 => LanguageVersion.CSharp8,
            CSharpVersion.CSharp10 => LanguageVersion.CSharp10,
            _ => LanguageVersion.CSharp11,
        };
        var parse = new CSharpParseOptions(language);
        var trees = code.Files.Select(file => CSharpSyntaxTree.ParseText(file.Content, parse, file.FileName)).ToList();
        var compilation = CSharpCompilation.Create("Generated" + Guid.NewGuid().ToString("N"), trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable, warningLevel: 4,
                // The SDK suppresses these unification notes in every project, as here.
                specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic> { ["CS1701"] = ReportDiagnostic.Suppress, ["CS1702"] = ReportDiagnostic.Suppress }));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        var problems = result.Diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning).ToList();
        Assert.True(problems.Count == 0, "The generated code does not compile cleanly:\n" + string.Join("\n", problems) + "\n\n" + code.Preview);
        // A collectible context per test keeps the generated types from meeting each other.
        var context = new AssemblyLoadContext(null, isCollectible: true);
        stream.Position = 0;
        return context.LoadFromStream(stream);
    }

    public static Type Type(Assembly assembly, string name) =>
        assembly.GetTypes().FirstOrDefault(type => type.Name == name) ?? throw new InvalidOperationException($"{name} was not generated. Types: {string.Join(", ", assembly.GetTypes().Select(type => type.Name))}");

    public static object? Call(Type type, string method, params object?[] arguments) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static).First(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length
            && candidate.GetParameters().Select((parameter, index) => arguments[index] is null || parameter.ParameterType.IsInstanceOfType(arguments[index])).All(match => match)).Invoke(null, arguments);
}
