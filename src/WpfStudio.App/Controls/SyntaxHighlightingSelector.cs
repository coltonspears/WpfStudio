using System.IO;
using ICSharpCode.AvalonEdit.Highlighting;

namespace WpfStudio.App.Controls;

/// <summary>Maps file extensions to the themed AvalonEdit highlighting definitions shared by editors and diffs.</summary>
public static class SyntaxHighlightingSelector
{
    public static IHighlightingDefinition? For(string? path)
    {
        var extension = Path.GetExtension(path ?? "").ToLowerInvariant();
        string? name = extension switch
        {
            ".cs" or ".csx" => "C#",
            ".xaml" or ".xml" or ".csproj" or ".props" or ".targets" or ".config" or ".resx" or ".manifest" or ".nuspec" or ".slnx" => "XML",
            ".json" or ".js" => "JavaScript",
            ".sql" => "SQL",
            _ => null
        };
        return name is null ? null : HighlightingManager.Instance.GetDefinition(name);
    }
}
