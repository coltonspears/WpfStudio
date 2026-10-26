using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace WpfStudio.Workspace;

/// <summary>A code fix or refactoring offered at a position. <see cref="Kind"/> is "Fix" or "Refactor".</summary>
public sealed record CodeActionCandidate(string Title, string Kind, CodeAction Action, int Rank);

/// <summary>A file changed by an applied code action. A new file has no <see cref="Before"/> text.</summary>
public sealed record CodeActionFileChange(string Path, SourceText? Before, SourceText After, IReadOnlyList<TextChange> Changes);

/// <summary>
/// Roslyn's built-in C# code fixes and refactorings (the providers Visual Studio shows under Ctrl+.), discovered
/// from the Features assemblies, plus fixes for diagnostics from the project's own analyzers. Providers that need a
/// dialog, or that fail, are skipped rather than failing the request.
/// </summary>
public static class CodeActionCatalog
{
    private static readonly Lazy<Providers> Loaded = new(Load);
    /// <summary>Per-request budget. Slow providers are abandoned, not awaited.</summary>
    public static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(4);
    public const int MaximumActions = 80;

    private sealed record Providers(ImmutableArray<CodeFixProvider> Fixes, ImmutableArray<CodeRefactoringProvider> Refactorings,
        ImmutableArray<DiagnosticAnalyzer> Analyzers, ImmutableDictionary<string, ImmutableArray<CodeFixProvider>> FixesById);

    public static int FixProviderCount => Loaded.Value.Fixes.Length;
    public static void Warm() => _ = Loaded.Value;
    public static int RefactoringProviderCount => Loaded.Value.Refactorings.Length;

    private static Providers Load()
    {
        var fixes = new List<CodeFixProvider>();
        var refactorings = new List<CodeRefactoringProvider>();
        var analyzers = new List<DiagnosticAnalyzer>();
        foreach (var name in new[] { "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" })
        {
            Type?[] types;
            try { types = Assembly.Load(name).GetTypes(); }
            catch (ReflectionTypeLoadException exception) { types = exception.Types; }
            catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException) { continue; }
            foreach (var type in types.OfType<Type>().Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false }))
            {
                if (typeof(CodeFixProvider).IsAssignableFrom(type) && type.GetCustomAttribute<ExportCodeFixProviderAttribute>() is { } fix && fix.Languages.Contains(LanguageNames.CSharp))
                { if (Create<CodeFixProvider>(type) is { } provider) fixes.Add(provider); }
                else if (typeof(CodeRefactoringProvider).IsAssignableFrom(type) && type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>() is { } refactoring && refactoring.Languages.Contains(LanguageNames.CSharp))
                { if (Create<CodeRefactoringProvider>(type) is { } provider) refactorings.Add(provider); }
                else if (typeof(DiagnosticAnalyzer).IsAssignableFrom(type) && type.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is { } analyzer && analyzer.Languages.Contains(LanguageNames.CSharp))
                { if (Create<DiagnosticAnalyzer>(type) is { } instance) analyzers.Add(instance); }
            }
        }
        var byId = new Dictionary<string, List<CodeFixProvider>>(StringComparer.Ordinal);
        foreach (var provider in fixes)
        {
            ImmutableArray<string> ids;
            try { ids = provider.FixableDiagnosticIds; } catch { continue; }
            foreach (var id in ids) (byId.TryGetValue(id, out var list) ? list : byId[id] = []).Add(provider);
        }
        return new([.. fixes], [.. refactorings], [.. analyzers], byId.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal));
    }

    // MEF parts usually have a public parameterless [ImportingConstructor]. Parts that import services need a host.
    private static T? Create<T>(Type type) where T : class
    {
        try
        {
            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
            return constructor?.Invoke(null) as T;
        }
        catch (Exception exception) when (exception is TargetInvocationException or MemberAccessException or TypeLoadException) { return null; }
    }

    public static async Task<IReadOnlyList<CodeActionCandidate>> GetAsync(Document document, TextSpan span, CancellationToken cancellationToken)
    {
        var providers = Loaded.Value;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        var token = budget.Token;
        var results = new ConcurrentBag<CodeActionCandidate>();
        var diagnostics = await DiagnosticsAsync(document, span, providers, token).ConfigureAwait(false);

        var fixWork = diagnostics.SelectMany(diagnostic => providers.FixesById.GetValueOrDefault(diagnostic.Id, []).Select(provider => (diagnostic, provider)));
        await IgnoreBudget(Parallel.ForEachAsync(fixWork, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount }, async (item, ct) =>
        {
            var rank = item.diagnostic.Severity switch { DiagnosticSeverity.Error => 0, DiagnosticSeverity.Warning => 1, DiagnosticSeverity.Info => 2, _ => 3 };
            try
            {
                await item.provider.RegisterCodeFixesAsync(new CodeFixContext(document, item.diagnostic,
                    (action, _) => results.Add(new(action.Title, "Fix", action, rank)), ct)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
        })).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!token.IsCancellationRequested)
            await IgnoreBudget(Parallel.ForEachAsync(providers.Refactorings, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount }, async (provider, ct) =>
            {
                try
                {
                    await provider.ComputeRefactoringsAsync(new CodeRefactoringContext(document, span,
                        action => results.Add(new(action.Title, "Refactor", action, 4)), ct)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
            })).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return Flatten(results).Where(candidate => candidate.Action is not CodeActionWithOptions)
            .GroupBy(candidate => candidate.Title, StringComparer.Ordinal).Select(group => group.OrderBy(c => c.Rank).First())
            .OrderBy(candidate => candidate.Rank).ThenBy(candidate => candidate.Title, StringComparer.CurrentCultureIgnoreCase).Take(MaximumActions).ToArray();
    }

    private static async Task IgnoreBudget(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static IEnumerable<CodeActionCandidate> Flatten(IEnumerable<CodeActionCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var nested = candidate.Action.NestedActions;
            if (nested.IsDefaultOrEmpty) { yield return candidate; continue; }
            foreach (var child in Flatten(nested.Select(action => candidate with { Title = candidate.Title + " › " + action.Title, Action = action })))
                yield return child;
        }
    }

    private static async Task<ImmutableArray<Diagnostic>> DiagnosticsAsync(Document document, TextSpan span, Providers providers, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null) return [];
        var diagnostics = model.GetDiagnostics(span, cancellationToken).ToBuilder();
        var analyzers = document.Project.AnalyzerReferences.SelectMany(reference => reference.GetAnalyzers(document.Project.Language))
            .Concat(providers.Analyzers).ToImmutableArray();
        if (!analyzers.IsEmpty)
        {
            try
            {
                var options = new CompilationWithAnalyzersOptions(document.Project.AnalyzerOptions, null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false);
                var analysis = model.Compilation.WithAnalyzers(analyzers, options);
                diagnostics.AddRange(await analysis.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, span, cancellationToken).ConfigureAwait(false));
                diagnostics.AddRange(await analysis.GetAnalyzerSemanticDiagnosticsAsync(model, span, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
        }
        // A fix at the caret applies to diagnostics that touch it, including zero-length ones at either end.
        return [.. diagnostics.Where(d => d.Location.IsInSource && d.Location.SourceTree == model.SyntaxTree
            && (d.Location.SourceSpan.IntersectsWith(span) || d.Location.SourceSpan.End == span.Start)).Distinct()];
    }

    /// <summary>The files an action changes. Actions that delete files or only open dialogs return null.</summary>
    public static async Task<IReadOnlyList<CodeActionFileChange>?> GetChangesAsync(CodeAction action, Solution original, CancellationToken cancellationToken)
    {
        var operations = await action.GetOperationsAsync(original, new Progress<CodeAnalysisProgress>(), cancellationToken).ConfigureAwait(false);
        var apply = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (apply is null) return null;
        var changed = apply.ChangedSolution;
        var files = new Dictionary<string, CodeActionFileChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in changed.GetChanges(original).GetProjectChanges())
        {
            if (project.GetRemovedDocuments().Any()) return null;
            foreach (var id in project.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var before = original.GetDocument(id)!; var after = changed.GetDocument(id)!;
                if (before.FilePath is not { } path || files.ContainsKey(path)) continue; // Linked files change once.
                var changes = (await after.GetTextChangesAsync(before, cancellationToken).ConfigureAwait(false)).ToArray();
                files[path] = new(path, await before.GetTextAsync(cancellationToken).ConfigureAwait(false), await after.GetTextAsync(cancellationToken).ConfigureAwait(false), changes);
            }
            foreach (var id in project.GetAddedDocuments())
            {
                var added = changed.GetDocument(id)!;
                var path = added.FilePath ?? Path.Combine([Path.GetDirectoryName(added.Project.FilePath) ?? "", .. added.Folders, added.Name]);
                if (files.ContainsKey(path)) continue;
                var text = await added.GetTextAsync(cancellationToken).ConfigureAwait(false);
                files[path] = new(path, null, text, [new TextChange(new TextSpan(0, 0), text.ToString())]);
            }
        }
        return files.Values.ToArray();
    }
}
