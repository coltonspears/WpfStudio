using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using WpfStudio.Contracts;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.Workspace;

public sealed partial class WorkspaceEngine
{
    private async Task<XamlCodeAction?> CreateXamlEventHandlerActionAsync(Project project, XamlCompletionRequest request,
        Compilation compilation, XamlEventTarget target, CancellationToken token)
    {
        var elements = XamlSyntax.Read(request.Text, token);
        var reservedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            token.ThrowIfCancellationRequested();
            if (XamlSchemaService.IgnoredElement(element)) continue;
            // The current buffer may introduce fields that are absent from the
            // last generated partial class. Reserve authored names conservatively,
            // including nested namescopes, rather than guess compiler ownership.
            foreach (var attribute in new[] { element.Attribute(XamlSyntax.Language, "Name"), element.Attribute("Name") })
                if (attribute is not null && attribute.Value.Text.Trim() is { Length: > 0 } name) reservedNames.Add(name);
        }
        bool NameUnavailable(string name) => reservedNames.Contains(name) || HasMember(target.RootClass, name);
        TextEdit[] xamlEdits = [];
        if (target.HandlerName.Length == 0)
        {
            var owner = elements.SelectMany(element => element.Attributes)
                .FirstOrDefault(attribute => attribute.NameStart == target.AttributeStart)?.Owner;
            if (owner is null) return null;
            string elementName = owner.Attribute(XamlSyntax.Language, "Name")?.Value.Text ?? owner.Attribute("Name")?.Value.Text ?? owner.LocalName;
            string preferred = IdentifierPart(elementName) + "_" + IdentifierPart(target.EventName.Split('.').Last());
            string name = preferred;
            int suffix = 1;
            while (NameUnavailable(name) && suffix < 1000) name = preferred + suffix++;
            if (NameUnavailable(name) || !SyntaxFacts.IsValidIdentifier(name)) return null;
            xamlEdits = [new(target.Start, target.Length, name)];
            target = target with { HandlerName = name };
        }
        else if (reservedNames.Contains(target.HandlerName)) return null;
        var proposal = await XamlEventHandlerGenerator.CreateAsync(project, compilation, target, token, request.Path).ConfigureAwait(false);
        if (proposal is null) return null;
        lock (_gate)
        {
            if (!IsCurrentXamlProject(project)) return null;
            long version = _versions.GetValueOrDefault(proposal.Path);
            if (version > 0 && (!_syncedTexts.TryGetValue(proposal.Path, out var current) || current != proposal.OriginalText)) return null;
            return new($"Create event handler '{target.HandlerName}'",
                new DocumentEdits(request.Path, request.Version, xamlEdits, TextHash(request.Text)),
                [new DocumentEdits(proposal.Path, version, [proposal.Edit], TextHash(proposal.OriginalText))]);
        }
    }

    private static bool HasMember(INamedTypeSymbol root, string name)
    {
        for (var type = root; type is not null; type = type.BaseType)
            if (type.GetMembers(name).Length != 0) return true;
        return false;
    }

    private static string IdentifierPart(string value)
    {
        string part = new(value.Take(100).Select(character => char.IsLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
        return string.IsNullOrEmpty(part) ? "Element" : char.IsDigit(part[0]) ? "_" + part : part;
    }
}
