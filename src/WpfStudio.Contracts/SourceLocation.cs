namespace WpfStudio.Contracts;

/// <summary><paramref name="LineText"/> is the source line the location is on, so a list of locations can show the code and not only where it is.</summary>
public sealed record SourceLocation(string Path, int Start, int Length, int Line, int Column, string? DisplayText = null,
    string? ExpectedTextHash = null, string? ProjectPath = null, string? ProjectName = null, string? LineText = null);
