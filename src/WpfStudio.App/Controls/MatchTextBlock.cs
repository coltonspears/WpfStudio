using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace WpfStudio.App.Controls;

/// <summary>A line of text with one part picked out: the text a search found, or the name a reference query was about.</summary>
public sealed class MatchTextBlock : TextBlock
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(string), typeof(MatchTextBlock), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty MatchStartProperty = DependencyProperty.Register(nameof(MatchStart), typeof(int), typeof(MatchTextBlock), new PropertyMetadata(-1, Changed));
    public static readonly DependencyProperty MatchLengthProperty = DependencyProperty.Register(nameof(MatchLength), typeof(int), typeof(MatchTextBlock), new PropertyMetadata(0, Changed));

    public string Line { get => (string)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public int MatchStart { get => (int)GetValue(MatchStartProperty); set => SetValue(MatchStartProperty, value); }
    public int MatchLength { get => (int)GetValue(MatchLengthProperty); set => SetValue(MatchLengthProperty, value); }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((MatchTextBlock)target).Compose();

    private void Compose()
    {
        Inlines.Clear();
        var line = Line ?? "";
        int start = MatchStart, length = MatchLength;
        if (start < 0 || length <= 0 || start + length > line.Length) { Inlines.Add(new Run(line)); return; }
        if (start > 0) Inlines.Add(new Run(line[..start]));
        var match = new Run(line.Substring(start, length)) { FontWeight = FontWeights.SemiBold };
        match.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        match.SetResourceReference(TextElement.BackgroundProperty, "AccentSoftBrush");
        Inlines.Add(match);
        if (start + length < line.Length) Inlines.Add(new Run(line[(start + length)..]));
    }
}
