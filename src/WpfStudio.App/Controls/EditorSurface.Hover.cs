using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfStudio.Contracts;

namespace WpfStudio.App.Controls;

/// <summary>
/// The hover card: Quick Info for the symbol under the pointer (its classified signature, kind, documentation and
/// notes), the debugger's value while paused, XAML details, and the diagnostics at that position. Parts arrive at
/// different times and the open card is updated in place.
/// </summary>
public sealed partial class EditorSurface
{
    private sealed record HoverParts(QuickInfoResult? QuickInfo, string? DataTip, string? Xaml, IReadOnlyList<WorkspaceDiagnostic> Diagnostics)
    {
        public bool IsEmpty => QuickInfo is null && string.IsNullOrWhiteSpace(DataTip) && string.IsNullOrWhiteSpace(Xaml) && Diagnostics.Count == 0;
    }

    private void ShowHover(HoverParts parts)
    {
        if (parts.IsEmpty) return;
        var card = new StackPanel { MaxWidth = 640 };
        void Separate() { if (card.Children.Count > 0) card.Children.Add(new Border { Height = 1, Margin = new Thickness(-10, 8, -10, 8) }.WithResource(Border.BackgroundProperty, "BorderBrush")); }
        if (parts.QuickInfo is { } info) { AddQuickInfo(card, info); }
        if (!string.IsNullOrWhiteSpace(parts.DataTip))
        {
            Separate();
            card.Children.Add(Caption("Value while paused"));
            card.Children.Add(Code(parts.DataTip!));
        }
        if (!string.IsNullOrWhiteSpace(parts.Xaml)) { Separate(); card.Children.Add(Prose(parts.Xaml!)); }
        if (parts.Diagnostics.Count > 0)
        {
            Separate();
            foreach (var diagnostic in parts.Diagnostics) card.Children.Add(DiagnosticRow(diagnostic));
        }
        // Updating an open popup in place avoids moving it when slower information arrives.
        _hoverContent.Child = card;
        if (!_hover.IsOpen) { _hover.PlacementTarget = this; _hover.IsOpen = true; }
    }

    private void AddQuickInfo(StackPanel card, QuickInfoResult info)
    {
        foreach (var section in info.Sections)
        {
            switch (section.Kind)
            {
                case "Description":
                    var header = new DockPanel();
                    if (KindLabel(info.Tags) is { } kind)
                    {
                        var chip = new Border { Padding = new Thickness(5, 0, 5, 1), CornerRadius = new CornerRadius(3), Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = kind.Text, FontSize = 10.5 }.WithResource(TextBlock.ForegroundProperty, kind.Brush) };
                        chip.SetResourceReference(Border.BackgroundProperty, "InlayHintBackgroundBrush");
                        DockPanel.SetDock(chip, Dock.Right);
                        header.Children.Add(chip);
                    }
                    header.Children.Add(Classified(section.Runs, code: true));
                    card.Children.Add(header);
                    break;
                case "DocumentationComments":
                    card.Children.Add(Spaced(Classified(section.Runs, code: false), 6));
                    break;
                case "ReturnsDocumentationComments": card.Children.Add(Labelled("Returns", section)); break;
                case "ValueDocumentationComments": card.Children.Add(Labelled("Value", section)); break;
                case "RemarksDocumentationComments": card.Children.Add(Labelled("Remarks", section)); break;
                case "Exception": card.Children.Add(Spaced(Muted(Classified(section.Runs, code: false)), 6)); break;
                default: // Type parameters, anonymous types, usage, captures and nullability notes.
                    card.Children.Add(Spaced(Muted(Classified(section.Runs, code: section.Kind is "TypeParameters" or "AnonymousTypes" or "Captures")), 4));
                    break;
            }
        }
    }

    private FrameworkElement Labelled(string label, QuickInfoSection section)
    {
        var block = Classified(section.Runs, code: false);
        block.Inlines.InsertBefore(block.Inlines.FirstInline, new Run(label + "  ") { FontWeight = FontWeights.SemiBold });
        return Spaced(block, 4);
    }

    /// <summary>Quick Info text with each part coloured like the editor colours it.</summary>
    private TextBlock Classified(IReadOnlyList<QuickInfoRun> runs, bool code)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (code) { block.FontFamily = FontFamily; block.FontSize = Math.Max(11, FontSize - 0.5); }
        else block.FontSize = Math.Max(11, FontSize - 0.5);
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        foreach (var run in runs)
        {
            if (run.Tag == "LineBreak") { block.Inlines.Add(new LineBreak()); continue; }
            var inline = new Run(run.Text);
            if (TagBrush(run.Tag) is { } key) inline.SetResourceReference(TextElement.ForegroundProperty, key);
            block.Inlines.Add(inline);
        }
        return block;
    }

    private static string? TagBrush(string tag) => tag switch
    {
        "Keyword" => "SyntaxKeyword",
        "Class" or "Struct" or "Record" or "RecordStruct" or "Delegate" or "Module" or "ErrorType" => "SyntaxType",
        "Interface" => "SyntaxInterface",
        "Enum" => "SyntaxEnum",
        "TypeParameter" => "SyntaxTypeParameter",
        "Method" or "ExtensionMethod" => "SyntaxMethod",
        "Property" or "Field" or "Event" or "EnumMember" or "Constant" => "SyntaxMember",
        "Parameter" or "Local" or "RangeVariable" => "SyntaxParameter",
        "StringLiteral" => "SyntaxString",
        "NumericLiteral" => "SyntaxNumber",
        _ => null
    };

    /// <summary>"public method", "local", "class"… from Roslyn's glyph tags, coloured like that kind of symbol.</summary>
    private static (string Text, string Brush)? KindLabel(IReadOnlyList<string> tags)
    {
        (string Name, string Brush)? kind = tags.Select(tag => tag switch
        {
            "Class" => ("class", "SyntaxType"), "Structure" => ("struct", "SyntaxType"), "Record" => ("record", "SyntaxType"),
            "RecordStruct" => ("record struct", "SyntaxType"), "Delegate" => ("delegate", "SyntaxType"), "Module" => ("module", "SyntaxType"),
            "Interface" => ("interface", "SyntaxInterface"), "Enum" => ("enum", "SyntaxEnum"), "EnumMember" => ("enum member", "SyntaxMember"),
            "Method" => ("method", "SyntaxMethod"), "ExtensionMethod" => ("extension method", "SyntaxMethod"),
            "Property" => ("property", "SyntaxMember"), "Field" => ("field", "SyntaxMember"), "Event" => ("event", "SyntaxMember"),
            "Constant" => ("constant", "SyntaxMember"), "Local" => ("local", "SyntaxParameter"), "Parameter" => ("parameter", "SyntaxParameter"),
            "RangeVariable" => ("range variable", "SyntaxParameter"), "TypeParameter" => ("type parameter", "SyntaxTypeParameter"),
            "Namespace" => ("namespace", "TextBrush"), "Keyword" => ("keyword", "SyntaxKeyword"), "Operator" => ("operator", "SyntaxMethod"),
            "Label" => ("label", "TextBrush"),
            _ => ((string, string)?)null
        }).FirstOrDefault(found => found is not null);
        if (kind is not { } found) return null;
        var access = tags.FirstOrDefault(tag => tag is "Public" or "Protected" or "Private" or "Internal")?.ToLowerInvariant();
        return (access is null || found.Name is "local" or "parameter" or "range variable" or "type parameter" or "namespace" or "keyword" or "label"
            ? found.Name : access + " " + found.Name, found.Brush);
    }

    private FrameworkElement DiagnosticRow(WorkspaceDiagnostic diagnostic)
    {
        var (glyph, brush) = diagnostic.Severity switch
        {
            "Error" => ("Error", "DangerBrush"),
            "Warning" => ("Warning", "WarningBrush"),
            _ => ("Info", "AccentBrush")
        };
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 3) };
        var icon = new Glyph { Kind = glyph, Width = 14, Height = 14, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(Glyph.ForegroundProperty, brush);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = Math.Max(11, FontSize - 0.5) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        if (!string.IsNullOrEmpty(diagnostic.Id))
        {
            var id = new Run(diagnostic.Id + "  ") { FontFamily = FontFamily };
            id.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            text.Inlines.Add(id);
        }
        text.Inlines.Add(new Run(diagnostic.Message));
        row.Children.Add(text);
        return row;
    }

    private TextBlock Caption(string text) =>
        new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) }.WithResource(TextBlock.ForegroundProperty, "MutedBrush");

    private TextBlock Code(string text) =>
        new TextBlock { Text = text, FontFamily = FontFamily, FontSize = Math.Max(11, FontSize - 0.5), TextWrapping = TextWrapping.Wrap }.WithResource(TextBlock.ForegroundProperty, "TextBrush");

    private TextBlock Prose(string text) =>
        new TextBlock { Text = text, FontSize = Math.Max(11, FontSize - 0.5), TextWrapping = TextWrapping.Wrap }.WithResource(TextBlock.ForegroundProperty, "TextBrush");

    private static T Spaced<T>(T element, double top) where T : FrameworkElement { element.Margin = new Thickness(0, top, 0, 0); return element; }

    private static TextBlock Muted(TextBlock block)
    {
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        block.FontSize = Math.Max(10.5, block.FontSize - 1);
        return block;
    }
}
