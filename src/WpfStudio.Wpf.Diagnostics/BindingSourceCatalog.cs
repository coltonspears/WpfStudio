using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Diagnostics;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Wpf.Diagnostics;

/// <summary>
/// Associates an already observed expression with its actual declaration object.
/// Call on the expression target's dispatcher. This catalog does not look up
/// dependency-property values, evaluate paths, or realize style expressions.
/// </summary>
public sealed class BindingSourceCatalog
{
    private sealed record Identity(string Value);
    private sealed class ExpressionIdentity(string id)
    {
        internal string Id { get; } = id;
        internal WeakReference<BindingBase>? Binding;
        internal BindingSourceDeclaration? Observed;
    }

    private readonly ConditionalWeakTable<BindingExpressionBase, ExpressionIdentity> _expressions = new();
    private readonly ConditionalWeakTable<BindingBase, Identity> _declarations = new();

    public BindingSourcesSnapshot Capture(BindingExpressionBase expression,
        Func<BindingExpressionBase, string>? identity = null, int maximumDeclarations = 65, int maximumCharacters = 16384,
        Func<BindingExpressionBase, InspectionBinding>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        expression.Target?.VerifyAccess();
        maximumDeclarations = Math.Clamp(maximumDeclarations, 0, 65);
        maximumCharacters = Math.Clamp(maximumCharacters, 0, 16384);
        var root = IdentityFor(expression, identity);
        var rows = new List<BindingSourceDeclaration>();
        var rowExpressions = new List<BindingExpressionBase>();
        var visited = new HashSet<BindingExpressionBase>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(BindingExpressionBase Expression, string? Parent, int? Index, int Depth)>();
        pending.Push((expression, null, null, 0));
        int characters = root.Id.Length;
        bool truncated = false;
        while (pending.Count > 0)
        {
            if (rows.Count >= maximumDeclarations) { truncated = true; break; }
            var item = pending.Pop();
            if (!visited.Add(item.Expression) || item.Depth > 8) { truncated = true; continue; }
            var entry = IdentityFor(item.Expression, identity);
            var binding = item.Expression.ParentBindingBase;
            var declarationId = _declarations.GetValue(binding, _ => new(Guid.NewGuid().ToString("N"))).Value;
            var row = Describe(item.Expression, entry.Id, declarationId, item.Parent, item.Index);
            int length = CharacterCount(row);
            if (length > maximumCharacters - characters) { truncated = true; break; }
            characters += length;
            rows.Add(row);
            rowExpressions.Add(item.Expression);
            entry.Binding = new(binding);
            entry.Observed = row;
            var children = Children(item.Expression);
            if (children is null) continue;
            int count = Math.Min(children.Count, maximumDeclarations - rows.Count);
            if (count != children.Count) truncated = true;
            for (int i = count - 1; i >= 0; i--) pending.Push((children[i], entry.Id, i, item.Depth + 1));
        }
        // Preserve declaration coverage first. Current observations are a bounded
        // enrichment tied to the same exact expression identities, not a nested
        // binding-source graph or an inferred child selected by path spelling.
        if (observe is not null)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (maximumCharacters - characters < 256) { truncated = true; break; }
                var observation = observe(rowExpressions[i]) with { Sources = null };
                int length = BindingDetailBudget.GetCharacterCount(observation);
                if (length > maximumCharacters - characters) { truncated = true; continue; }
                characters += length;
                rows[i] = rows[i] with { Observation = observation };
            }
        }
        return new(root.Id, rows, truncated);
    }

    /// <summary>
    /// Revalidates exact observed identities and loader metadata against the
    /// supplied current root. The caller must independently verify target/DP,
    /// revision, presentation attachment, and that this remains the current root.
    /// </summary>
    public BindingSourceResponse Validate(BindingExpressionBase currentRoot, BindingSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(currentRoot);
        ArgumentNullException.ThrowIfNull(request);
        BindingSourceResponse Reject(string message) => new(request, false, Status: message);
        currentRoot.Target?.VerifyAccess();
        if (!_expressions.TryGetValue(currentRoot, out var root) || root.Id != request.BindingId)
            return Reject("The property's binding expression was replaced or was not observed.");
        if (currentRoot.Status is BindingStatus.Detached or BindingStatus.Unattached || currentRoot.Target is null)
            return Reject("The binding expression is no longer attached to its observed target.");

        var pending = new Stack<(BindingExpressionBase Expression, string? Parent, int? Index, int Depth)>();
        var visited = new HashSet<BindingExpressionBase>(ReferenceEqualityComparer.Instance);
        pending.Push((currentRoot, null, null, 0));
        int remaining = 65;
        while (pending.Count > 0 && remaining-- > 0)
        {
            var item = pending.Pop();
            if (item.Depth > 8 || !visited.Add(item.Expression)) continue;
            if (!_expressions.TryGetValue(item.Expression, out var entry)) continue;
            if (entry.Id == request.ExpressionId)
            {
                var binding = item.Expression.ParentBindingBase;
                if (entry.Observed is not { } observed || entry.Binding?.TryGetTarget(out var previous) != true ||
                    !ReferenceEquals(previous, binding) || observed.DeclarationId != request.DeclarationId ||
                    !_declarations.TryGetValue(binding, out var declaration) || declaration.Value != request.DeclarationId)
                    return Reject("The selected binding declaration was replaced or was not observed.");
                var current = Describe(item.Expression, entry.Id, declaration.Value, item.Parent, item.Index);
                if (observed.ParentExpressionId != current.ParentExpressionId || observed.ChildIndex != current.ChildIndex ||
                    observed.Kind != current.Kind || observed.Path != current.Path || observed.XPath != current.XPath || observed.Source != current.Source)
                    return Reject("The observed binding declaration, child position, or loader source metadata changed.");
                if (current.Source is null || current.UnavailableReason is not null)
                    return new(request, false, current, current.UnavailableReason ?? "WPF supplied no declaration source location.");
                return new(request, true, current);
            }
            var children = Children(item.Expression);
            if (children is null) continue;
            // Push only a bounded prefix even for application-created composites.
            for (int i = Math.Min(children.Count, remaining) - 1; i >= 0; i--)
                pending.Push((children[i], entry.Id, i, item.Depth + 1));
        }
        return Reject("The selected child expression is no longer present in the observed binding tree.");
    }

    public static int GetCharacterCount(BindingSourcesSnapshot snapshot) =>
        snapshot.BindingId.Length + snapshot.Declarations.Sum(CharacterCount);

    private ExpressionIdentity IdentityFor(BindingExpressionBase expression, Func<BindingExpressionBase, string>? identity) =>
        _expressions.GetValue(expression, value =>
        {
            string id = identity?.Invoke(value) ?? Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(id) || id.Length > 128) throw new ArgumentException("A binding identity must contain 1–128 characters.", nameof(identity));
            return new(id);
        });

    private static IReadOnlyList<BindingExpressionBase>? Children(BindingExpressionBase expression) => expression switch
    {
        MultiBindingExpression multi => multi.BindingExpressions,
        PriorityBindingExpression priority => priority.BindingExpressions,
        _ => null
    };

    private static BindingSourceDeclaration Describe(BindingExpressionBase expression, string id, string declarationId,
        string? parentId, int? childIndex)
    {
        var binding = expression.ParentBindingBase;
        Type type = binding.GetType();
        string kind = type == typeof(Binding) ? "Binding" : type == typeof(MultiBinding) ? "MultiBinding"
            : type == typeof(PriorityBinding) ? "PriorityBinding" : "Unsupported";
        string? path = (binding as Binding)?.Path?.Path, xpath = (binding as Binding)?.XPath;
        string? reason = kind == "Unsupported" ? "This custom binding declaration type cannot be verified as a standard XAML binding." : null;
        if (path?.Length > 2048 || xpath?.Length > 2048)
        {
            path = null; xpath = null;
            reason = "The complete binding path exceeds the declaration navigation limit; it was omitted rather than truncated.";
        }
        InspectionSourceHint? hint = null;
        if (reason is null)
        {
            try
            {
                var info = VisualDiagnostics.GetXamlSourceInfo(binding);
                if (info?.SourceUri?.OriginalString is { Length: > 0 and <= 2048 } uri && info.LineNumber > 0 && info.LinePosition > 0)
                    hint = new(uri, info.LineNumber, info.LinePosition);
                else reason = "WPF supplied no complete loader source location for this actual binding declaration. Code-created bindings and some template clones have no location.";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { reason = "WPF's loader source metadata could not be read for this declaration."; }
        }
        return new(id, declarationId, parentId, childIndex, kind, path, xpath, expression.Status.ToString(), hint, reason);
    }

    private static int CharacterCount(BindingSourceDeclaration row) => row.ExpressionId.Length + row.DeclarationId.Length +
        (row.ParentExpressionId?.Length ?? 0) + row.Kind.Length + (row.Path?.Length ?? 0) + (row.XPath?.Length ?? 0) +
        row.Status.Length + (row.Source?.Uri.Length ?? 0) + (row.UnavailableReason?.Length ?? 0) + 256 +
        (row.Observation is { } observation ? BindingDetailBudget.GetCharacterCount(observation) : 0);
}
