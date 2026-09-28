namespace WpfStudio.Contracts;

public sealed record XamlFormattingOptions(int IndentSize = 4, bool UseTabs = false,
    string? NewLine = null, int AttributeWrapColumn = 120, bool UseKnownFrameworkContent = true);
public sealed record XamlFormattingResult(bool Accepted, IReadOnlyList<TextEdit> Edits,
    IReadOnlyList<string> Warnings);
public sealed record XamlFormattingRequest(string Path, string Text, long Version,
    string? ProjectPath = null, XamlFormattingOptions? Options = null);
