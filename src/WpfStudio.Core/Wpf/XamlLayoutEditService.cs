using System.Globalization;
using System.Text;
using System.Xml;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.Core.Wpf;

public sealed record XamlLayoutEditRequest(string Path, string Text, long Version, long ElementSourceVersion,
    string ElementSourceHash, PreviewLayoutEditContext Context, PreviewBounds Bounds, XamlLayoutHandle Handle,
    bool ReplaceProtectedValues = false, string? SourceAssembly = null);

public static partial class XamlLayoutEditService
{
    private sealed record Assignment(string Name, string Owner, bool Attached, string Value);

    /// <summary>Prepares all properties of one completed gesture against the same source snapshot.</summary>
    public static XamlPropertyEditResult CreateEdit(XamlLayoutEditRequest request)
    {
        if (request is null || request.Context is null || request.Bounds is null || request.Text is null)
            return EditFailure("The source snapshot, layout observation and proposed rectangle are required.");
        if (ValidateContext(request.Context) is { } contextError) return EditFailure(contextError);
        if (ValidateProposal(request.Context, request.Bounds, request.Handle) is { } proposalError) return EditFailure(proposalError);
        if (request.Version < 0 || request.Version != request.ElementSourceVersion || request.Text.Length > 2_000_000
            || string.IsNullOrWhiteSpace(request.Path) || request.Context.Element is null || request.Context.Parent is null
            || string.IsNullOrWhiteSpace(request.Context.Token) || string.IsNullOrWhiteSpace(request.Context.NodeId))
            return EditFailure("A current, bounded, authored layout snapshot is required.");
        string hash = DocumentStore.Hash(Encoding.UTF8.GetBytes(request.Text));
        if (!string.Equals(hash, request.ElementSourceHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hash, request.Context.SourceHash, StringComparison.OrdinalIgnoreCase))
            return EditFailure("The source text no longer matches the captured layout. Refresh the preview.");
        try
        {
            var document = XamlPropertySourceDocument.Parse(request.Text);
            var element = Find(document, request.Context.Element, request.Path, request.Text);
            var parent = Find(document, request.Context.Parent, request.Path, request.Text);
            if (element is null || parent is null || element.Xml.Name.LocalName.Contains('.') || parent.Xml.Name.LocalName.Contains('.'))
                return EditFailure("The captured layout nodes no longer identify complete authored object start tags.");
            var lexicalParent = element.Parent;
            while (lexicalParent is not null && lexicalParent.Xml.Name.LocalName.Contains('.')) lexicalParent = lexicalParent.Parent;
            if (!ReferenceEquals(lexicalParent, parent) || parent.Xml.Name.LocalName != request.Context.ParentKind
                || !KnownPanelNamespace(parent.Xml.Name.NamespaceName))
                return EditFailure("The selected source element is not a direct child of the captured Canvas or Grid.");
            var values = ValuesFor(request.Context, request.Bounds, request.Handle);
            var assignments = Assignments(values).ToArray();
            if (assignments.Any(assignment => assignment.Value.Length > 256)
                || values.Margin is { } margin && !ValidInsets(margin)
                || new[] { values.Width, values.Height, values.CanvasLeft, values.CanvasTop, values.CanvasRight, values.CanvasBottom }
                    .Any(value => value is { } number && !Finite(number)))
                return EditFailure("The resulting layout property exceeds the finite value budget.");
            var edits = new List<TextEdit>();
            bool expression = false, objects = false, template = false, styleOverride = false;
            foreach (var assignment in assignments)
            {
                var metadata = request.Context.EditProperties?.Where(property => property.OwnerType == assignment.Owner
                    && property.OwnerAssembly == "PresentationFramework" && property.IsAttached == assignment.Attached
                    && (property.Name == assignment.Name || assignment.Attached && property.Name == "Canvas." + assignment.Name)).ToArray();
                if (metadata is not [var property] || !property.CanWriteSource || property.IsAnimated || property.IsCoerced)
                    return EditFailure($"The captured {assignment.Name} property is unavailable, ambiguous, animated or coerced. Refresh the preview.");
                if (property.IsExpression && !request.ReplaceProtectedValues)
                    return new(null, $"{property.Name} currently uses an expression. Replacing it requires an explicit source review.",
                        "This gesture would override a binding or resource expression.", ReplacesExpression: true);
                var proposal = XamlPropertyEditService.CreateEdit(new(request.Path, request.Text, request.Version,
                    request.Context.Element, request.ElementSourceVersion, request.ElementSourceHash,
                    assignment.Attached ? "Canvas." + assignment.Name : assignment.Name, assignment.Value,
                    ReplaceExistingValue: request.ReplaceProtectedValues, OwnerType: assignment.Owner,
                    OwnerAssembly: "PresentationFramework", IsAttached: assignment.Attached,
                    SourceAssembly: request.SourceAssembly, ContentProperty: property.ContentProperty));
                if (!proposal.Success) return proposal;
                edits.AddRange(proposal.Edit!.Edits);
                expression |= proposal.ReplacesExpression || property.IsExpression;
                objects |= proposal.ReplacesObjectValue;
                template |= proposal.AffectsTemplate;
                styleOverride |= property.ValueSource.Contains("Style", StringComparison.Ordinal)
                    || property.ValueSource.Contains("Inherited", StringComparison.Ordinal)
                    || property.ValueSource.Contains("Template", StringComparison.Ordinal);
            }
            // Each property was planned against the original text. Combine insertions at the same start tag
            // into one deterministic insertion, then validate every range before applying the atomic proposal.
            var combined = edits.Where(edit => edit.Length != 0)
                .Concat(edits.Where(edit => edit.Length == 0).GroupBy(edit => edit.Start)
                    .Select(group => new TextEdit(group.Key, 0, string.Concat(group.Select(edit => edit.NewText)))))
                .OrderBy(edit => edit.Start).ThenBy(edit => edit.Length).ToArray();
            string after = WorkspaceEditTransaction.ApplyTextEdits(request.Text, combined);
            _ = XamlPropertySourceDocument.Parse(after);
            string explanation = assignments.Length == 0 ? "The completed gesture makes no source change."
                : (request.Handle == XamlLayoutHandle.Move ? "Move" : "Resize") + " the selected element in its "
                    + (request.Context.ParentKind == "Grid" ? "current Grid cell" : "Canvas") + ". Update "
                    + string.Join(", ", assignments.Select(assignment => assignment.Attached ? "Canvas." + assignment.Name : assignment.Name))
                    + " together as one undoable source edit.";
            if (expression) explanation += " This explicitly replaces or overrides the affected binding/resource expression.";
            if (objects) explanation += " An affected object-valued property declaration is replaced with a literal.";
            if (styleOverride) explanation += " The new local values override the observed style, template or inherited values.";
            if (template) explanation += " This edits a shared template declaration and affects every instance created from it.";
            return new(new(request.Path, request.Version, combined, hash), explanation,
                ReplacesExpression: expression, ReplacesObjectValue: objects, AffectsTemplate: template);
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException or InvalidOperationException or NotSupportedException or System.IO.PathTooLongException)
        { return EditFailure("The layout source edit could not be prepared safely: " + exception.Message); }
    }

    private static IEnumerable<Assignment> Assignments(PreviewLayoutEditValues values)
    {
        const string framework = "System.Windows.FrameworkElement", canvas = "System.Windows.Controls.Canvas";
        if (values.Width is { } width) yield return new("Width", framework, false, Number(width));
        if (values.Height is { } height) yield return new("Height", framework, false, Number(height));
        if (values.Margin is { } margin) yield return new("Margin", framework, false,
            string.Join(",", new[] { margin.Left, margin.Top, margin.Right, margin.Bottom }.Select(Number)));
        if (values.CanvasLeft is { } left) yield return new("Left", canvas, true, Number(left));
        if (values.CanvasTop is { } top) yield return new("Top", canvas, true, Number(top));
        if (values.CanvasRight is { } right) yield return new("Right", canvas, true, Number(right));
        if (values.CanvasBottom is { } bottom) yield return new("Bottom", canvas, true, Number(bottom));
    }
    private static string Number(double value) => Math.Round(value, 6, MidpointRounding.ToEven).ToString("0.######", CultureInfo.InvariantCulture);
    private static bool KnownPanelNamespace(string value) => value == "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        || value == "clr-namespace:System.Windows.Controls;assembly=PresentationFramework";
    private static XamlPropertySourceDocument.Element? Find(XamlPropertySourceDocument document, SourceLocation source, string path, string text)
    {
        if (!string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(source.Path), StringComparison.OrdinalIgnoreCase)) return null;
        var element = document.Elements.SingleOrDefault(item => item.Start == source.Start);
        if (element is null || element.StartTagEnd - element.Start != source.Length) return null;
        var position = XamlPropertySourceDocument.Position(text, element.Start);
        if (position.Line != source.Line || position.Column != source.Column
            || source.DisplayText is { Length: > 0 } name && name != element.Name && name != element.Xml.Name.LocalName) return null;
        return element;
    }
    private static XamlPropertyEditResult EditFailure(string message) => new(null, message, message);
}
