using System.Xml;
using Microsoft.CodeAnalysis;

namespace WpfStudio.Workspace.Xaml;

/// <summary>A compiler-resolved declaration referenced by one exact raw UTF-16 XAML span.</summary>
public sealed record XamlSymbolOccurrence(ISymbol Symbol, int Start, int Length, string Kind);

/// <summary>Verified occurrences plus explicit limits on the completeness of this snapshot.</summary>
public sealed record XamlSymbolOccurrenceResult(IReadOnlyList<XamlSymbolOccurrence> Occurrences, bool IsComplete,
    IReadOnlyList<string> Warnings, bool CoverageLimited = false);

internal sealed class XamlOccurrenceCollector
{
    internal const int MaximumCharacters = 1_000_000;
    internal const int MaximumOccurrences = 8192;
    private readonly List<XamlSymbolOccurrence> _occurrences = [];
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
    private bool _complete = true;
    private bool _coverageLimited;

    internal bool Validate(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (text.Length > MaximumCharacters)
        { Warn("The XAML document exceeds the symbol-analysis text budget.", coverageLimited: true); return false; }
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumCharacters });
            int elements = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.Depth > 256 || reader.NodeType == XmlNodeType.Element && ++elements > 32768)
                { Warn("The XAML tree exceeds the bounded depth or element budget for symbol analysis.", coverageLimited: true); return false; }
            }
            return true;
        }
        catch (XmlException)
        { Warn("The XAML document is not well formed; symbol occurrences cannot be trusted until its XML syntax is repaired.", coverageLimited: true); return false; }
    }

    internal bool Add(ISymbol symbol, int start, int length, string kind)
    {
        if (_occurrences.Count >= MaximumOccurrences)
        { Warn("The XAML symbol-occurrence budget was reached; coverage is incomplete.", coverageLimited: true); return false; }
        _occurrences.Add(new(symbol.OriginalDefinition, start, length, kind));
        return true;
    }

    internal void Warn(string warning, bool coverageLimited = false)
    {
        _complete = false;
        _coverageLimited |= coverageLimited;
        Note(warning);
    }

    internal void Note(string warning)
    {
        if (_warnings.Count < 16) _warnings.Add(warning.Length <= 512 ? warning : warning[..512] + "…");
    }

    internal XamlSymbolOccurrenceResult Result() => new(_occurrences.OrderBy(occurrence => occurrence.Start)
        .ThenBy(occurrence => occurrence.Length).ToArray(), _complete, _warnings.Order(StringComparer.Ordinal).ToArray(), _coverageLimited);
}
