using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WpfStudio.Workspace.Xaml;

public sealed partial class XamlEventService
{
    private sealed record Resolution(string State, IMethodSymbol? Method = null, string? Explanation = null);

    // WPF emits new HandlerDelegate(this.Handler). Keep that exact conversion context:
    // a signature comparison would miss overload resolution, generic inference, variance,
    // accessibility, and the rejection of static handlers through `this`.
    private static Dictionary<string, Resolution> Resolve(Compilation compilation, IReadOnlyList<XamlEventTarget> targets,
        CancellationToken token, out bool complete)
    {
        complete = true;
        var result = new Dictionary<string, Resolution>(StringComparer.Ordinal);
        var candidates = targets.Where(target => SimpleName(target.HandlerName)).DistinctBy(Key).Take(MaximumProbes + 1).ToArray();
        if (candidates.Length == 0) return result;
        if (candidates.Length > MaximumProbes) { complete = false; candidates = candidates[..MaximumProbes]; }
        var verifiable = new List<XamlEventTarget>(candidates.Length);
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (Methods(candidate.RootClass).Where(method => method.Name == candidate.HandlerName).Any(method => !SignatureComplete(method)))
            { complete = false; continue; }
            verifiable.Add(candidate);
        }
        candidates = verifiable.ToArray();
        if (candidates.Length == 0) return result;
        if (compilation is not CSharpCompilation csharp) { complete = false; return result; }
        var declarations = candidates[0].RootClass.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(token))
            .OfType<TypeDeclarationSyntax>().ToArray();
        bool uncertainDeclarations = declarations.Any(type => type.ContainsDiagnostics);
        if (uncertainDeclarations) complete = false;
        var declaration = declarations.FirstOrDefault(type => !type.ContainsDiagnostics) ?? declarations.FirstOrDefault();
        if (declaration is null) { complete = false; return result; }
        var oldTree = declaration.SyntaxTree;
        var oldRoot = oldTree.GetRoot(token);
        var annotations = candidates.Select(_ => new SyntaxAnnotation()).ToArray();
        var statements = new List<StatementSyntax>(candidates.Length);
        for (int index = 0; index < candidates.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var target = candidates[index];
            var member = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ThisExpression(), SyntaxFactory.IdentifierName(SyntaxFactory.ParseToken("@" + target.HandlerName)));
            var creation = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(
                    target.DelegateType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(member))))
                .WithAdditionalAnnotations(annotations[index]);
            // Delegate construction is a conversion expression in Roslyn and is not a
            // legal standalone statement. An explicitly typed local preserves the same
            // conversion without introducing an unrelated CS0201 into every probe.
            statements.Add(SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(creation.Type)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("__handlerProbe" + index)
                    .WithInitializer(SyntaxFactory.EqualsValueClause(creation))))));
        }
        var methodAnnotation = new SyntaxAnnotation();
        var probe = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "__WpfStudioEventProbe_" + Guid.NewGuid().ToString("N"))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithBody(SyntaxFactory.Block(statements)).WithAdditionalAnnotations(methodAnnotation);
        var newRoot = oldRoot.ReplaceNode(declaration, declaration.AddMembers(probe));
        var newTree = oldTree.WithRootAndOptions(newRoot, oldTree.Options);
        var modified = csharp.ReplaceSyntaxTree(oldTree, newTree);
        var model = modified.GetSemanticModel(newTree);
        var attachedRoot = newTree.GetRoot(token);
        var attachedProbe = attachedRoot.GetAnnotatedNodes(methodAnnotation).Single();
        var errors = model.GetDiagnostics(attachedProbe.Span, token)
            .Where(diagnostic => diagnostic.DefaultSeverity == DiagnosticSeverity.Error).ToArray();
        for (int index = 0; index < candidates.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var target = candidates[index];
            var creation = (ObjectCreationExpressionSyntax)attachedRoot.GetAnnotatedNodes(annotations[index]).Single();
            var member = creation.ArgumentList!.Arguments[0].Expression;
            var info = model.GetSymbolInfo(member, token);
            var problems = errors.Where(error => error.Location.SourceSpan.IntersectsWith(creation.Span)).ToArray();
            // File-local imports in the authored partial do not necessarily exist in WPF's
            // generated connection file. A reduced extension is neither proof of a valid
            // root member nor proof of a build failure in that other import context.
            if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension })
            { complete = false; continue; }
            if (problems.Length == 0 && info.Symbol is IMethodSymbol { IsStatic: false, MethodKind: MethodKind.Ordinary } selected)
            {
                // Never return symbols from the probe tree: inserted syntax shifts later source
                // positions. Resolve the declaration back into the caller's immutable compilation.
                string? id = selected.OriginalDefinition.GetDocumentationCommentId();
                var original = id is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) as IMethodSymbol;
                if (original is not null) result[Key(target)] = new("Valid", original);
                else complete = false;
                continue;
            }
            if (uncertainDeclarations) continue;
            if (problems.Any(problem => problem.Id == "CS0121") || info.CandidateReason == CandidateReason.Ambiguous)
                result[Key(target)] = new("Ambiguous");
            else if (!HasMember(target.RootClass, target.HandlerName)) result[Key(target)] = new("Missing");
            else if (problems.Length > 0)
                result[Key(target)] = new("Incompatible", Explanation: Limit(problems[0].GetMessage(CultureInfo.InvariantCulture), 512));
            else complete = false;
        }
        return result;
    }

    private static bool HasMember(INamedTypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.GetMembers(name).Length > 0) return true;
        return false;
    }

    private static bool SignatureComplete(IMethodSymbol method)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        int remaining = 256;
        bool TypeComplete(ITypeSymbol type, int depth = 0)
        {
            if (type.TypeKind == TypeKind.Error || depth > 64) return false;
            if (!visited.Add(type)) return true;
            if (--remaining < 0) return false;
            return type switch
            {
                IArrayTypeSymbol array => TypeComplete(array.ElementType, depth + 1),
                IPointerTypeSymbol pointer => TypeComplete(pointer.PointedAtType, depth + 1),
                IFunctionPointerTypeSymbol pointer => TypeComplete(pointer.Signature.ReturnType, depth + 1)
                    && pointer.Signature.Parameters.All(parameter => TypeComplete(parameter.Type, depth + 1)),
                ITypeParameterSymbol parameter => parameter.ConstraintTypes.All(constraint => TypeComplete(constraint, depth + 1)),
                INamedTypeSymbol named => named.TypeArguments.All(argument => TypeComplete(argument, depth + 1))
                    && (named.BaseType is null || TypeComplete(named.BaseType, depth + 1))
                    && named.Interfaces.All(@interface => TypeComplete(@interface, depth + 1)),
                _ => true
            };
        }
        return TypeComplete(method.ReturnType) && method.Parameters.All(parameter => TypeComplete(parameter.Type))
            && method.TypeParameters.All(parameter => TypeComplete(parameter));
    }
}
