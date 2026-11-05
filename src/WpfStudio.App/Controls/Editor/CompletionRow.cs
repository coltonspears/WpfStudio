using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using WpfStudio.Contracts;
using WpfStudio.Core.Text;

namespace WpfStudio.App.Controls.Editor;

/// <summary>What a row of the completion popup shows: its kind, name and origin. The row itself is in StudioTheme.xaml.</summary>
internal abstract class CompletionRow(FrameworkElement resources, string label, string? origin, IReadOnlyList<string> tags, string? kind = null) : ICompletionData
{
    private const double MinimumWidth = 280, MaximumWidth = 640;
    // The badge, gaps, padding and scroll bar, then the width of a character in each column of the row.
    private const double RowChrome = 72, LabelCharacter = 7.6, OriginGap = 16, OriginCharacter = 6.2;

    private readonly (string? Name, string Brush) _kind = kind is not null ? (kind, "MutedBrush")
        : HoverCard.Kind(tags) is { } known ? known : (null, "MutedBrush");

    public string Label => label;
    public string? Origin => origin;
    public virtual Visibility StarVisibility => Visibility.Collapsed;
    public string KindGlyph => GlyphFor(_kind.Name);
    public Brush? KindBrush => resources.TryFindResource(_kind.Brush) as Brush;

    public ImageSource? Image => null;
    public virtual string Text => label;
    public virtual object Content => label;
    public abstract object? Description { get; }
    public virtual double Priority => 0;
    public abstract void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs);

    /// <summary>The glyph for a kind of symbol, named as <see cref="HoverCard.Kind"/> names it.</summary>
    internal static string GlyphFor(string? kind) => kind switch
    {
        "class" or "record" or "module" => "SymbolClass",
        "struct" or "record struct" => "SymbolStruct",
        "interface" => "SymbolInterface",
        "enum" => "SymbolEnum",
        "enum member" => "SymbolEnumMember",
        "delegate" => "SymbolDelegate",
        "method" => "SymbolMethod",
        "extension method" => "SymbolExtensionMethod",
        "operator" => "SymbolOperator",
        "property" => "SymbolProperty",
        "field" => "SymbolField",
        "constant" => "SymbolConstant",
        "event" => "SymbolEvent",
        "local" or "range variable" => "SymbolLocal",
        "parameter" => "SymbolParameter",
        "type parameter" => "SymbolTypeParameter",
        "namespace" => "SymbolNamespace",
        "keyword" => "SymbolKeyword",
        "label" => "SymbolLabel",
        "snippet" => "SymbolSnippet",
        _ => "SymbolOther"
    };

    /// <summary>
    /// Wide enough that no name is cut off, and fixed for the life of the popup, so filtering does not make it jump. A
    /// long origin is what gives way when the window would be too wide.
    /// </summary>
    public static double WidthFor(IReadOnlyCollection<CompletionRow> rows)
    {
        if (rows.Count == 0) return MinimumWidth;
        int label = rows.Max(row => row.Label.Length + (row.StarVisibility == Visibility.Visible ? 2 : 0));
        int origin = rows.Max(row => row.Origin?.Length ?? 0);
        return Math.Clamp(RowChrome + label * LabelCharacter + (origin > 0 ? OriginGap + origin * OriginCharacter : 0), MinimumWidth, MaximumWidth);
    }
}

/// <summary>A row's name with the part that matches what has been typed picked out.</summary>
public sealed class CompletionLabel : TextBlock
{
    /// <summary>Set on the popup and inherited by its rows, so each one follows the typing without being rebuilt.</summary>
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached("Query", typeof(string), typeof(CompletionLabel),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits, Changed));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(CompletionLabel), new PropertyMetadata("", Changed));

    public static string GetQuery(DependencyObject target) => (string)target.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject target, string value) => target.SetValue(QueryProperty, value);
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) { if (target is CompletionLabel label) label.Compose(); }

    private void Compose()
    {
        Inlines.Clear();
        int at = 0;
        foreach (var (start, length) in CompletionMatch.Ranges(Label, GetQuery(this)))
        {
            if (start > at) Inlines.Add(new Run(Label[at..start]));
            var match = new Run(Label.Substring(start, length)) { FontWeight = FontWeights.SemiBold };
            match.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            Inlines.Add(match);
            at = start + length;
        }
        if (at < Label.Length) Inlines.Add(new Run(Label[at..]));
    }
}

/// <summary>The card beside the list: what the selected entry is, filled in further when the worker has answered.</summary>
internal sealed class CompletionDetailsCard : Border
{
    private const double MaximumHeight = 280;
    private readonly HoverCard _card;
    private readonly string? _note;
    private readonly string _plain;

    public CompletionDetailsCard(HoverCard card, CompletionEntry entry, long version)
    {
        _card = card;
        _note = entry.Description;
        _plain = entry.Description ?? entry.DisplayText;
        MaxHeight = MaximumHeight;
        ClipToBounds = true;
        Show(new QuickInfoResult(version, 0, 0, entry.Tags, [new QuickInfoSection("Description", [new QuickInfoRun("Text", entry.DisplayText)])]));
    }

    public bool IsDescribed { get; set; }

    public void Show(QuickInfoResult info) => Child = _card.Build(new HoverParts(info, null, _note, []));

    public override string ToString() => _plain;
}
