using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace WpfStudio.Workspace;

/// <summary>Conservative public-Roslyn refactorings. The caller previews and version-checks every edit.</summary>
public static class CSharpRefactorings
{
    public static async Task<Document> ApplyAsync(Document document, int position, string action, CancellationToken token = default)
    {
        var root = await document.GetSyntaxRootAsync(token) as CompilationUnitSyntax ?? throw new InvalidOperationException("Open a C# source document first.");
        var semantic = await document.GetSemanticModelAsync(token) ?? throw new InvalidOperationException("The C# semantic model is unavailable.");
        SyntaxNode changed;
        if (action == "OrganizeUsings")
        {
            // Keep comment/directive groups intact; moving these can change conditional compilation.
            // An import used only in an inactive #if branch looks unused in this
            // compilation. Keep it until every configuration can be considered.
            if (root.ContainsDirectives || root.Usings.Any(u => u.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))))
                throw new InvalidOperationException("Usings with comments or directives are left in place to preserve their grouping.");
            var unused = semantic.GetDiagnostics(cancellationToken: token).Where(d => d.Id == "CS8019").Select(d => d.Location.SourceSpan).ToArray();
            var kept = root.Usings.Where(u => !unused.Any(span => u.Span.Contains(span))).OrderByDescending(u => !u.GlobalKeyword.IsKind(SyntaxKind.None))
                .ThenBy(u => u.Alias != null).ThenBy(u => u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                .ThenBy(u => u.Name?.ToString().StartsWith("System", StringComparison.Ordinal) != true)
                .ThenBy(u => u.Name?.ToString(), StringComparer.Ordinal).ToArray();
            changed = root.WithUsings(SyntaxFactory.List(kept));
        }
        else if (action is "UseVar" or "UseExplicitType")
        {
            var node = root.FindToken(Math.Clamp(position, 0, Math.Max(0, root.FullSpan.End - 1))).Parent;
            var declaration = node?.AncestorsAndSelf().OfType<VariableDeclarationSyntax>().FirstOrDefault();
            if (declaration?.Parent is not LocalDeclarationStatementSyntax local || declaration.Variables.Count != 1 || declaration.Type is RefTypeSyntax)
                throw new InvalidOperationException("Place the caret in a single local variable declaration.");
            var variable = declaration.Variables[0];
            if (local.ContainsDiagnostics || semantic.GetDiagnostics(local.Span, token).Any(d => d.Severity == DiagnosticSeverity.Error)
                || semantic.GetDeclaredSymbol(variable, token) is not ILocalSymbol symbol || symbol.Type.TypeKind == TypeKind.Error)
                throw new InvalidOperationException("Resolve this declaration's type errors before refactoring it.");
            if (symbol.IsConst || symbol.RefKind != RefKind.None)
                throw new InvalidOperationException("Constants and ref locals keep their explicit declaration form.");
            TypeSyntax replacement;
            if (action == "UseVar")
            {
                if (declaration.Type.IsVar) return document;
                var initializer = variable.Initializer?.Value;
                if (initializer == null || initializer is ImplicitObjectCreationExpressionSyntax or CollectionExpressionSyntax or InitializerExpressionSyntax or StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax || initializer.IsKind(SyntaxKind.DefaultLiteralExpression)
                    || !SymbolEqualityComparer.IncludeNullability.Equals(symbol.Type, semantic.GetTypeInfo(initializer, token).Type))
                    throw new InvalidOperationException("Using var here could change the inferred type or remove a required target type. Keep the explicit type.");
                replacement = SyntaxFactory.IdentifierName("var");
            }
            else
            {
                if (!declaration.Type.IsVar) return document;
                if (ContainsAnonymousType(symbol.Type)) throw new InvalidOperationException("Anonymous types, including arrays and generic arguments containing them, require var.");
                var display = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
                replacement = SyntaxFactory.ParseTypeName(symbol.Type.ToDisplayString(display));
            }
            var annotation = new SyntaxAnnotation("WpfStudio.LocalTypeRefactoring");
            changed = root.ReplaceNode(declaration, declaration.WithType(replacement.WithTriviaFrom(declaration.Type))
                .WithAdditionalAnnotations(annotation, Formatter.Annotation));
            // Nested target-typed expressions and aliases named 'var' can look safe in the
            // original model. Rebind the edited declaration before offering a preview.
            var candidate = document.WithSyntaxRoot(changed);
            var candidateRoot = await candidate.GetSyntaxRootAsync(token) ?? throw new InvalidOperationException("The edited declaration is unavailable.");
            var candidateDeclaration = candidateRoot.GetAnnotatedNodes(annotation).OfType<VariableDeclarationSyntax>().Single();
            var candidateModel = await candidate.GetSemanticModelAsync(token) ?? throw new InvalidOperationException("The edited semantic model is unavailable.");
            var candidateSymbol = candidateModel.GetDeclaredSymbol(candidateDeclaration.Variables[0], token) as ILocalSymbol;
            var typeFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
            if (candidateDeclaration.ContainsDiagnostics || candidateModel.GetDiagnostics(candidateDeclaration.Parent!.Span, token).Any(d => d.Severity == DiagnosticSeverity.Error)
                || candidateSymbol is null || candidateSymbol.Type.TypeKind == TypeKind.Error
                || candidateSymbol.Type.ToDisplayString(typeFormat) != symbol.Type.ToDisplayString(typeFormat))
                throw new InvalidOperationException("This edit would change the local's type or require a target type. Keep the current declaration.");
        }
        else throw new ArgumentException("Unknown C# refactoring.", nameof(action));
        return await Formatter.FormatAsync(document.WithSyntaxRoot(changed), cancellationToken: token).ConfigureAwait(false);
    }

    private static bool ContainsAnonymousType(ITypeSymbol type) => type.IsAnonymousType || (type switch
    {
        IArrayTypeSymbol array => ContainsAnonymousType(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsAnonymousType),
        _ => false
    });
}
