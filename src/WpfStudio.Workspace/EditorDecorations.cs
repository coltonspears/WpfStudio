using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace;

/// <summary>Semantic classification and inlay hints for one C# document.</summary>
public static class EditorDecorations
{
    /// <summary>Larger documents keep syntax colouring only.</summary>
    public const int MaximumCharacters = 1_000_000;
    public const int MaximumHints = 5_000;

    private static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal)
    {
        [ClassificationTypeNames.ClassName] = "Type", [ClassificationTypeNames.RecordClassName] = "Type",
        [ClassificationTypeNames.StructName] = "Type", [ClassificationTypeNames.RecordStructName] = "Type",
        [ClassificationTypeNames.DelegateName] = "Type", [ClassificationTypeNames.ModuleName] = "Type",
        [ClassificationTypeNames.InterfaceName] = "Interface", [ClassificationTypeNames.EnumName] = "Enum",
        [ClassificationTypeNames.TypeParameterName] = "TypeParameter",
        [ClassificationTypeNames.MethodName] = "Method", [ClassificationTypeNames.ExtensionMethodName] = "Method",
        [ClassificationTypeNames.PropertyName] = "Property", [ClassificationTypeNames.FieldName] = "Field",
        [ClassificationTypeNames.EventName] = "Event", [ClassificationTypeNames.ConstantName] = "Constant",
        [ClassificationTypeNames.EnumMemberName] = "EnumMember", [ClassificationTypeNames.ParameterName] = "Parameter",
        [ClassificationTypeNames.LocalName] = "Local", [ClassificationTypeNames.NamespaceName] = "Namespace",
    };

    public static async Task<IReadOnlyList<SemanticSpan>> ClassifyAsync(Document document, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length > MaximumCharacters) return [];
        var classified = await Classifier.GetClassifiedSpansAsync(document, new TextSpan(0, text.Length), cancellationToken).ConfigureAwait(false);
        var spans = new List<SemanticSpan>();
        foreach (var span in classified)
            if (Kinds.TryGetValue(span.ClassificationType, out var kind) && span.TextSpan.Length > 0)
                spans.Add(new(span.TextSpan.Start, span.TextSpan.Length, kind));
        // Additive classifications (such as static symbols) can repeat a range; keep the first.
        return spans.OrderBy(span => span.Start).DistinctBy(span => span.Start).ToArray();
    }

    public static async Task<IReadOnlyList<InlayHint>> GetHintsAsync(Document document, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length > MaximumCharacters) return [];
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return [];
        var hints = new List<InlayHint>();
        foreach (var node in root.DescendantNodes())
        {
            if (hints.Count >= MaximumHints) break;
            cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case ArgumentListSyntax { Parent: { } owner } arguments:
                    AddParameterHints(model, owner, arguments.Arguments, hints, cancellationToken);
                    break;
                case VariableDeclarationSyntax { Type.IsVar: true, Variables: [{ Initializer.Value: { } value } variable] } declaration
                    when !IsApparent(value):
                    if (model.GetTypeInfo(declaration.Type, cancellationToken).Type is { } type && IsNameable(type))
                        hints.Add(new(variable.Identifier.Span.End, ": " + type.ToMinimalDisplayString(model, variable.SpanStart), "Type"));
                    break;
                case ForEachStatementSyntax { Type.IsVar: true } loop:
                    if (model.GetTypeInfo(loop.Type, cancellationToken).Type is { } element && IsNameable(element))
                        hints.Add(new(loop.Identifier.Span.End, ": " + element.ToMinimalDisplayString(model, loop.SpanStart), "Type"));
                    break;
            }
        }
        return hints.OrderBy(hint => hint.Position).ToArray();
    }

    // Literal arguments are the ones whose meaning is unclear without the parameter name.
    private static void AddParameterHints(SemanticModel model, SyntaxNode owner, SeparatedSyntaxList<ArgumentSyntax> arguments, List<InlayHint> hints, CancellationToken cancellationToken)
    {
        if (!arguments.Any(argument => argument.NameColon is null && IsLiteral(argument.Expression))) return;
        if (model.GetSymbolInfo(owner, cancellationToken).Symbol is not IMethodSymbol method || method.Parameters.IsEmpty) return;
        var parameters = method.Parameters;
        if (HaveOnlyNumericSuffixes(parameters)) return;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.NameColon is not null) break; // Later positional arguments no longer map by index.
            if (!IsLiteral(argument.Expression)) continue;
            var parameter = index < parameters.Length ? parameters[index] : parameters[^1].IsParams ? parameters[^1] : null;
            if (parameter is null || parameter.Name.Length == 0 || MatchesMethodIntent(method, parameter)) continue;
            hints.Add(new(argument.SpanStart, parameter.Name + ":", "Parameter"));
        }
    }

    private static bool IsLiteral(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax or DefaultExpressionSyntax => true,
        PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } => true,
        InterpolatedStringExpressionSyntax => true,
        _ => false
    };

    // SetEnabled(true), Contains(item): the method name already says what the argument is.
    private static bool MatchesMethodIntent(IMethodSymbol method, IParameterSymbol parameter) =>
        method.Parameters.Length == 1 && (method.Name.EndsWith(parameter.Name, StringComparison.OrdinalIgnoreCase)
            || parameter.Name is "value" or "item" or "obj" or "format" or "message" or "text" or "args");

    // Max(int val1, int val2) style names add nothing.
    private static bool HaveOnlyNumericSuffixes(IReadOnlyList<IParameterSymbol> parameters) =>
        parameters.Count > 1 && parameters.All(p => p.Name.Length > 1 && char.IsDigit(p.Name[^1]))
        && parameters.Select(p => p.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')).Distinct().Count() == 1;

    // The type is visible in the initializer, as with var x = new Customer() or var y = (int)value.
    private static bool IsApparent(ExpressionSyntax value) => value switch
    {
        ObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or CastExpressionSyntax or LiteralExpressionSyntax
            or DefaultExpressionSyntax or TypeOfExpressionSyntax or InterpolatedStringExpressionSyntax => true,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression) => true,
        _ => false
    };

    private static bool IsNameable(ITypeSymbol type) => type is not IErrorTypeSymbol && !type.IsAnonymousType && type.TypeKind != TypeKind.Error
        && !(type is INamedTypeSymbol { IsTupleType: false } named && named.TypeArguments.Any(argument => argument.IsAnonymousType));
}
