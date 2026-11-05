namespace WpfStudio.Contracts;

public sealed record SourceLocation(string Path, int Start, int Length, int Line, int Column, string? DisplayText = null,
    string? ExpectedTextHash = null, string? ProjectPath = null, string? ProjectName = null);
