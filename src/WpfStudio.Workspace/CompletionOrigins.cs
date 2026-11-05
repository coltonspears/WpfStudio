using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WpfStudio.Workspace;

/// <summary>
/// Where the names a completion list offers come from. Roslyn's entries do not say, apart from types that still need a
/// using, so the names are looked up the way the list found them: on the receiver after a dot, otherwise in scope.
/// </summary>
internal sealed class CompletionOrigins
{
    // The list has already been computed. A compilation that is not ready yet should cost its origins, not hold the popup back.
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);
    private static readonly CompletionOrigins None = new(new Dictionary<string, ISymbol>());
    private readonly Dictionary<string, ISymbol> _symbols;

    private CompletionOrigins(Dictionary<string, ISymbol> symbols) => _symbols = symbols;

    public static async Task<CompletionOrigins> FindAsync(Document document, int position, CancellationToken cancellationToken)
    {
        try { return await LookUpAsync(document, position, cancellationToken).WaitAsync(Budget, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return None; }
    }

    /// <summary>The namespace of a type or the type of a member; null where the name alone says enough, as for a local.</summary>
    public string? Of(string displayText) =>
        _symbols.TryGetValue(displayText, out var symbol) || _symbols.TryGetValue(displayText + "Attribute", out symbol) ? Describe(symbol) : null;

    private static async Task<CompletionOrigins> LookUpAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null) return None;
        var root = await model.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        INamespaceOrTypeSymbol? container = null;
        if (Receiver(root, position) is { } receiver)
        {
            container = model.GetSymbolInfo(receiver, cancellationToken).Symbol as INamespaceOrTypeSymbol ?? model.GetTypeInfo(receiver, cancellationToken).Type;
            // What is in scope here says nothing about the members of a receiver that did not bind.
            if (container is null) return None;
        }
        var symbols = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        foreach (var symbol in model.LookupSymbols(position, container, includeReducedExtensionMethods: true)) symbols.TryAdd(symbol.Name, symbol);
        return new(symbols);
    }

    private static ExpressionSyntax? Receiver(SyntaxNode root, int position)
    {
        var token = root.FindToken(position);
        if (token.SpanStart >= position) token = token.GetPreviousToken();
        return token.Parent switch
        {
            MemberAccessExpressionSyntax access when access.OperatorToken == token => access.Expression,
            QualifiedNameSyntax qualified when qualified.DotToken == token => qualified.Left,
            MemberBindingExpressionSyntax binding when binding.OperatorToken == token => binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
            _ => null
        };
    }

    private static string? Describe(ISymbol symbol) => symbol switch
    {
        ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or ILabelSymbol or ITypeParameterSymbol => null,
        IMethodSymbol { ReducedFrom: { } extension } => extension.ContainingType.Name,
        { ContainingType: { } type } => type.Name,
        { ContainingNamespace: { IsGlobalNamespace: false } containing } => containing.ToDisplayString(),
        _ => null
    };
}
