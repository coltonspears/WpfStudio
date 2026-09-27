using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using WpfStudio.Contracts;
using WorkspaceDiagnostic = WpfStudio.Contracts.WorkspaceDiagnostic;

namespace WpfStudio.Workspace;

/// <summary>Lives exclusively in WorkspaceHost. User project assemblies are never loaded into the editor process.</summary>
public sealed class WorkspaceEngine : IWorkspaceRpc, IDisposable
{
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _syncedTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (Document Document, long Version, CompletionItem Item)> _completions = new();
    private long _completionRequest;

    public async Task<WorkspaceSnapshot> LoadAsync(LoadWorkspaceRequest request, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(request.Path);
        var issues = new ConcurrentQueue<WorkspaceIssue>();
        var paths = ProjectDiscovery.GetProjectPaths(path);
        var sdk = "Unavailable";
        try
        {
            sdk = await ToolchainResolver.RegisterAsync(path, cancellationToken).ConfigureAwait(false);
            _workspace?.Dispose();
            var properties = new Dictionary<string, string> { ["Configuration"] = request.Configuration };
            if (!string.IsNullOrWhiteSpace(request.TargetFramework)) properties["TargetFramework"] = request.TargetFramework;
            _workspace = MSBuildWorkspace.Create(properties);
            _workspace.RegisterWorkspaceFailedHandler(args => issues.Enqueue(new WorkspaceIssue(args.Diagnostic.Message, args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "Error" : "Warning")));
            Solution loaded;
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                loaded = (await _workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false)).Solution;
            else loaded = await _workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (_gate) { _solution = loaded; _versions.Clear(); _syncedTexts.Clear(); _completions.Clear(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Enqueue(new WorkspaceIssue($"Language workspace could not load: {ex.Message}. File navigation remains available.", "Error"));
            lock (_gate) { _solution = null; _versions.Clear(); _syncedTexts.Clear(); _completions.Clear(); }
        }
        var models = new List<WorkspaceProject>();
        var configurations = new HashSet<string>(ProjectDiscovery.GetDeclaredConfigurations(path), StringComparer.OrdinalIgnoreCase);
        var loadedProjects = _solution?.Projects.ToArray() ?? [];
        foreach (var projectPath in paths.Concat(loadedProjects.Select(p => p.FilePath).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = loadedProjects.FirstOrDefault(p => string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));
            ProjectDiscovery.EvaluatedProject? evaluated = null;
            try { evaluated = await ProjectDiscovery.EvaluateAsync(projectPath, request.Configuration, request.TargetFramework, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { issues.Enqueue(new WorkspaceIssue(ex.Message)); }
            foreach (var configuration in evaluated?.Configurations.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? ProjectDiscovery.GetDeclaredConfigurations(projectPath)) configurations.Add(configuration);
            var files = (evaluated?.Files ?? ProjectDiscovery.FallbackFiles(projectPath)).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            if (project is not null)
            {
                foreach (var document in project.Documents)
                {
                    if (document.FilePath is not { } documentPath) continue;
                    var generated = IsGenerated(documentPath);
                    files[documentPath] = new WorkspaceFile(documentPath, document.Name, "Compile", generated, document.Id.ToString(), files.GetValueOrDefault(documentPath)?.LogicalPath);
                }
                // Roslyn runs source generators; exposing these documents makes generated Toolkit members navigable.
                foreach (var document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
                    if (document.FilePath is { } generatedPath)
                    {
                        var generatedText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        var cachePath = await CacheGeneratedAsync(generatedPath, generatedText, cancellationToken).ConfigureAwait(false);
                        files[cachePath] = new WorkspaceFile(cachePath, document.Name, "Generated", true, document.Id.ToString());
                    }
            }
            files.TryAdd(projectPath, new WorkspaceFile(projectPath, Path.GetFileName(projectPath), "Project"));
            models.Add(new WorkspaceProject(project?.Id.ToString() ?? projectPath, project?.Name ?? Path.GetFileNameWithoutExtension(projectPath), projectPath,
                evaluated?.TargetFramework, evaluated?.OutputPath ?? project?.OutputFilePath, evaluated?.OutputType is "Exe" or "WinExe",
                files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(), evaluated?.AssemblyName));
        }
        if (configurations.Count == 0) { configurations.Add("Debug"); configurations.Add("Release"); }
        return new WorkspaceSnapshot(path, sdk, models, issues.ToArray(), configurations.OrderBy(value => value == "Debug" ? 0 : value == "Release" ? 1 : 2).ThenBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<DocumentUpdateResult> UpdateDocumentAsync(UpdateDocumentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Document document;
        var path = Path.GetFullPath(request.Path);
        lock (_gate)
        {
            if (_versions.TryGetValue(path, out var current) && request.Version < current) return new DocumentUpdateResult(false, current, []);
            var solution = RequireSolution();
            var ids = solution.GetDocumentIdsWithFilePath(path);
            if (ids.IsEmpty) return new DocumentUpdateResult(false, request.Version, []);
            if (_versions.TryGetValue(path, out current) && current == request.Version && _syncedTexts.TryGetValue(path, out var existingText))
            {
                if (!string.Equals(existingText, request.Text, StringComparison.Ordinal)) throw new InvalidOperationException("A document version cannot identify different text. Increment its version before updating.");
            }
            else
            {
                foreach (var id in ids) solution = solution.WithDocumentText(id, SourceText.From(request.Text), PreservationMode.PreserveIdentity);
                _solution = solution;
                _versions[path] = request.Version;
                _syncedTexts[path] = request.Text;
                _completions.Clear(); // Changes in any file can change the semantic context of a completion.
            }
            document = solution.GetDocument(ids[0])!;
        }
        if (!request.Analyze) return new DocumentUpdateResult(true, request.Version, []);
        var diagnostics = await GetDiagnosticsAsync(document, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return _versions.TryGetValue(path, out var latest) && latest == request.Version ? new DocumentUpdateResult(true, request.Version, diagnostics) : new DocumentUpdateResult(false, _versions.GetValueOrDefault(path), []);
    }

    public async Task CloseDocumentAsync(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        var text = File.Exists(path) ? SourceText.From(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) : null;
        lock (_gate)
        {
            if (_solution is { } solution)
            {
                foreach (var id in solution.GetDocumentIdsWithFilePath(path)) solution = text is null ? solution.RemoveDocument(id) : solution.WithDocumentText(id, text);
                _solution = solution;
            }
            _versions.Remove(path); _syncedTexts.Remove(path);
            _completions.Clear();
        }
    }

    public async Task<CompletionResult> GetCompletionsAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        Document document;
        long generation;
        lock (_gate)
        {
            document = GetDocument(request.Path, request.Version);
            generation = ++_completionRequest;
            _completions.Clear(); // Keep only the active popup, bounded to 1,500 entries and one solution snapshot.
        }
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        ValidatePosition(request.Position, text.Length);
        var service = CompletionService.GetService(document);
        if (service is null) return new CompletionResult(request.Version, request.Position, 0, []);
        var list = await service.GetCompletionsAsync(document, request.Position, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (list is null) return new CompletionResult(request.Version, request.Position, 0, []);
        var prefix = text.ToString(TextSpan.FromBounds(list.Span.Start, Math.Clamp(request.Position, list.Span.Start, list.Span.End)));
        var items = new List<CompletionEntry>();
        lock (_gate)
        {
            if (generation != _completionRequest || !ReferenceEquals(document.Project.Solution, _solution))
                return new CompletionResult(request.Version, request.Position, 0, []);
            foreach (var item in list.ItemsList.Where(i => prefix.Length == 0 || i.FilterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Take(1500))
            {
                var id = Guid.NewGuid().ToString("N");
                _completions[id] = (document, request.Version, item);
                items.Add(new CompletionEntry(id, item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix, item.DisplayText, null, item.Tags));
            }
        }
        return new CompletionResult(request.Version, list.Span.Start, list.Span.Length, items);
    }

    public async Task<TextEdit?> GetCompletionEditAsync(CompletionEditRequest request, CancellationToken cancellationToken)
    {
        _ = GetDocument(request.Path, request.Version);
        if (!_completions.TryGetValue(request.ItemId, out var entry) || entry.Version != request.Version || !string.Equals(entry.Document.FilePath, request.Path, StringComparison.OrdinalIgnoreCase)) return null;
        var service = CompletionService.GetService(entry.Document)!;
        var change = await service.GetChangeAsync(entry.Document, entry.Item, cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return ReferenceEquals(entry.Document.Project.Solution, _solution) && _completions.ContainsKey(request.ItemId) ? ToEdit(change.TextChange) : null;
    }

    public async Task<SignatureHelpResult> GetSignatureHelpAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        var document = GetDocument(request.Path, request.Version);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || root.FullSpan.IsEmpty) return new SignatureHelpResult(request.Version, 0, []);
        ValidatePosition(request.Position, root.FullSpan.Length);
        var node = root.FindToken(Math.Max(0, request.Position - 1)).Parent;
        var argumentList = node?.AncestorsAndSelf().OfType<BaseArgumentListSyntax>().FirstOrDefault(a => a.SpanStart <= request.Position && request.Position <= a.Span.End);
        if (argumentList?.Parent is not { } expression) return new SignatureHelpResult(request.Version, 0, []);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null) return new SignatureHelpResult(request.Version, 0, []);
        var symbolInfo = model.GetSymbolInfo(expression, cancellationToken);
        var symbols = new List<ISymbol>();
        if (symbolInfo.Symbol is { } symbol) symbols.Add(symbol);
        symbols.AddRange(symbolInfo.CandidateSymbols);
        if (expression is InvocationExpressionSyntax invocation) symbols.AddRange(model.GetMemberGroup(invocation.Expression, cancellationToken));
        if (expression is ObjectCreationExpressionSyntax creation && model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol type) symbols.AddRange(type.InstanceConstructors);
        var signatures = symbols.OfType<IMethodSymbol>().Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).Select(method => new SignatureEntry(
            method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), PlainDocumentation(method.GetDocumentationCommentXml(cancellationToken: cancellationToken)),
            method.Parameters.Select(p => new SignatureParameter(p.Name, p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), null)).ToArray())).ToArray();
        var active = argumentList.Arguments.GetSeparators().Count(s => s.SpanStart < request.Position);
        return new SignatureHelpResult(request.Version, active, signatures);
    }

    public async Task<IReadOnlyList<SourceLocation>> GetDefinitionAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        var document = GetDocument(request.Path, request.Version);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, request.Position, cancellationToken).ConfigureAwait(false);
        if (symbol is null) return [];
        var source = await SymbolFinder.FindSourceDefinitionAsync(symbol, document.Project.Solution, cancellationToken).ConfigureAwait(false) ?? symbol;
        return await Task.WhenAll(source.Locations.Where(l => l.IsInSource).Select(l => ToLocationAsync(l, source.ToDisplayString(), cancellationToken))).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SourceLocation>> FindReferencesAsync(DocumentPositionRequest request, CancellationToken cancellationToken)
    {
        var document = GetDocument(request.Path, request.Version);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, request.Position, cancellationToken).ConfigureAwait(false);
        if (symbol is null) return [];
        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, cancellationToken).ConfigureAwait(false);
        var locations = await Task.WhenAll(references.SelectMany(r => r.Locations).Where(l => l.Location.IsInSource).Select(l => ToLocationAsync(l.Location, symbol.Name, cancellationToken))).ConfigureAwait(false);
        return locations.Distinct().ToArray();
    }

    public async Task<WorkspaceEditResult> FormatDocumentAsync(DocumentRequest request, CancellationToken cancellationToken)
    {
        var document = GetDocument(request.Path, request.Version);
        var formatted = await Formatter.FormatAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
        var changes = await formatted.GetTextChangesAsync(document, cancellationToken).ConfigureAwait(false);
        var before = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceEditResult([new DocumentEdits(document.FilePath!, request.Version, changes.Select(ToEdit).ToArray(), TextHash(before.ToString()))], []);
    }

    public async Task<WorkspaceEditResult> RenameAsync(RenameRequest request, CancellationToken cancellationToken)
    {
        if (!SyntaxFacts.IsValidIdentifier(request.NewName)) throw new ArgumentException("The new name must be a valid C# identifier.", nameof(request));
        var document = GetDocument(request.Path, request.Version);
        var original = document.Project.Solution;
        Dictionary<string, long> versions;
        lock (_gate) versions = new(_versions, StringComparer.OrdinalIgnoreCase);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, request.Position, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("No symbol at the caret.");
        if (symbol.Locations.All(l => !l.IsInSource)) throw new InvalidOperationException("External symbols cannot be renamed.");
        if (symbol.Locations.Any(l => IsGenerated(l.SourceTree?.FilePath ?? ""))) throw new InvalidOperationException("Rename the source declaration rather than a generated member.");
        var changed = await Renamer.RenameSymbolAsync(original, symbol, new SymbolRenameOptions(), request.NewName, cancellationToken).ConfigureAwait(false);
        var edits = new Dictionary<string, DocumentEdits>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string> { "C# references are updated. Review XAML bindings, resource keys, x:Class and reflection/string references before applying this rename." };
        foreach (var projectChange in changed.GetChanges(original).GetProjectChanges())
        foreach (var id in projectChange.GetChangedDocuments())
        {
            var before = original.GetDocument(id)!;
            if (before.FilePath is not { } path) continue;
            if (IsGenerated(path)) { warnings.Add($"Generated document excluded: {path}"); continue; }
            var changes = await changed.GetDocument(id)!.GetTextChangesAsync(before, cancellationToken).ConfigureAwait(false);
            var beforeText = await before.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var result = new DocumentEdits(path, versions.GetValueOrDefault(path), changes.Select(ToEdit).ToArray(), TextHash(beforeText.ToString()));
            if (edits.TryGetValue(path, out var existing) && !existing.Edits.SequenceEqual(result.Edits)) throw new InvalidOperationException($"Linked project contexts produce conflicting edits for {path}.");
            edits[path] = result;
        }
        lock (_gate)
            if (!ReferenceEquals(original, _solution)) throw new InvalidOperationException("Documents changed while computing rename. Retry the rename.");
        return new WorkspaceEditResult(edits.Values.ToArray(), warnings);
    }

    public async Task<WorkspaceEditResult> RefactorAsync(RefactorRequest request, CancellationToken cancellationToken)
    {
        var document = GetDocument(request.Path, request.Version);
        var changed = await CSharpRefactorings.ApplyAsync(document, request.Position, request.Action, cancellationToken).ConfigureAwait(false);
        var changes = await changed.GetTextChangesAsync(document, cancellationToken).ConfigureAwait(false);
        var before = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return new([new(document.FilePath!, request.Version, changes.Select(ToEdit).ToArray(), TextHash(before.ToString()))], []);
    }

    private Document GetDocument(string path, long version)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_versions.GetValueOrDefault(path) != version) throw new InvalidOperationException("Document version is stale; synchronize the buffer and retry.");
            var solution = RequireSolution();
            var id = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault() ?? throw new FileNotFoundException("This file is not part of the loaded C# workspace.", path);
            return solution.GetDocument(id)!;
        }
    }

    private Solution RequireSolution() => _solution ?? throw new InvalidOperationException("Language services are unavailable. Check workspace loading diagnostics.");

    private static async Task<IReadOnlyList<WorkspaceDiagnostic>> GetDiagnosticsAsync(Document document, CancellationToken cancellationToken)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null) return [];
        var diagnostics = compilation.GetDiagnostics(cancellationToken);
        var analyzers = document.Project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(document.Project.Language)).ToImmutableArray();
        if (!analyzers.IsEmpty)
        {
            var options = new CompilationWithAnalyzersOptions(document.Project.AnalyzerOptions, null, true, false, false);
            var analysis = compilation.WithAnalyzers(analyzers, options);
            var semantic = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semantic is not null)
            {
                diagnostics = diagnostics.AddRange(await analysis.GetAnalyzerSyntaxDiagnosticsAsync(semantic.SyntaxTree, cancellationToken).ConfigureAwait(false));
                diagnostics = diagnostics.AddRange(await analysis.GetAnalyzerSemanticDiagnosticsAsync(semantic, null, cancellationToken).ConfigureAwait(false));
            }
        }
        return diagnostics.Where(d => d.Severity != DiagnosticSeverity.Hidden && (!d.Location.IsInSource || string.Equals(d.Location.SourceTree?.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase)))
            .Select(ToDiagnostic).Distinct().ToArray();
    }

    private static WorkspaceDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var line = diagnostic.Location.GetMappedLineSpan();
        var span = diagnostic.Location.SourceSpan;
        return new WorkspaceDiagnostic(diagnostic.Id, diagnostic.GetMessage(), diagnostic.Severity.ToString(), diagnostic.Location.IsInSource ? line.Path : null,
            diagnostic.Location.IsInSource ? line.StartLinePosition.Line + 1 : 0, diagnostic.Location.IsInSource ? line.StartLinePosition.Character + 1 : 0, span.Start, span.Length);
    }

    private static async Task<SourceLocation> ToLocationAsync(Location location, string? display, CancellationToken cancellationToken)
    {
        var line = location.GetMappedLineSpan();
        var path = line.Path;
        var start = location.SourceSpan.Start;
        if (!File.Exists(path) && location.SourceTree is { } tree)
        {
            var text = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            path = await CacheGeneratedAsync(tree.FilePath, text, cancellationToken).ConfigureAwait(false);
            line = location.GetLineSpan();
        }
        else if (!string.Equals(path, location.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            // #line maps generated WPF fields back to XAML. Their C# offsets are not XAML offsets.
            var text = SourceText.From(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            var sourceLine = text.Lines[Math.Clamp(line.StartLinePosition.Line, 0, text.Lines.Count - 1)];
            start = Math.Min(sourceLine.End, sourceLine.Start + line.StartLinePosition.Character);
        }
        return new SourceLocation(path, start, location.SourceSpan.Length, line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1, display);
    }
    private static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static async Task<string> CacheGeneratedAsync(string originalPath, SourceText text, CancellationToken cancellationToken)
    {
        var content = text.ToString();
        var directory = Path.Combine(Path.GetTempPath(), "WpfStudio", "GeneratedSources", TextHash(originalPath + "\n" + content));
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(originalPath);
        if (string.IsNullOrWhiteSpace(name)) name = "Generated.g.cs";
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return path;
    }
    private static TextEdit ToEdit(TextChange change) => new(change.Span.Start, change.Span.Length, change.NewText ?? "");
    private static bool IsGenerated(string path) => path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    private static void ValidatePosition(int position, int length) { if (position < 0 || position > length) throw new ArgumentOutOfRangeException(nameof(position)); }
    private static string? PlainDocumentation(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return System.Xml.Linq.XElement.Parse(xml).Element("summary")?.Value.Trim(); }
        catch (System.Xml.XmlException) { return null; }
    }
    public void Dispose() => _workspace?.Dispose();
}
