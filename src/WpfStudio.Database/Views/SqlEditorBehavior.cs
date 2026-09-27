using System.Collections;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;

namespace WpfStudio.Database.Views;

public static class SqlEditorBehavior
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(EditorState), typeof(SqlEditorBehavior));
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(SqlEditorBehavior), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.RegisterAttached("Selection", typeof(string), typeof(SqlEditorBehavior), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty SelectionLineProperty = DependencyProperty.RegisterAttached("SelectionLine", typeof(int), typeof(SqlEditorBehavior), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty CompletionsProperty = DependencyProperty.RegisterAttached("Completions", typeof(IEnumerable), typeof(SqlEditorBehavior));
    public static string GetText(DependencyObject value) => (string)value.GetValue(TextProperty);
    public static void SetText(DependencyObject value, string text) => value.SetValue(TextProperty, text);
    public static string GetSelection(DependencyObject value) => (string)value.GetValue(SelectionProperty);
    public static void SetSelection(DependencyObject value, string text) => value.SetValue(SelectionProperty, text);
    public static int GetSelectionLine(DependencyObject value) => (int)value.GetValue(SelectionLineProperty);
    public static void SetSelectionLine(DependencyObject value, int line) => value.SetValue(SelectionLineProperty, line);
    public static IEnumerable? GetCompletions(DependencyObject value) => (IEnumerable?)value.GetValue(CompletionsProperty);
    public static void SetCompletions(DependencyObject value, IEnumerable? completions) => value.SetValue(CompletionsProperty, completions);

    private static void OnTextChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not TextEditor editor) return;
        if (editor.GetValue(StateProperty) is not EditorState)
        {
            editor.SetValue(StateProperty, new EditorState(editor));
            using var xml = XmlReader.Create(new StringReader(HighlightingDefinition));
            editor.SyntaxHighlighting = HighlightingLoader.Load(xml, HighlightingManager.Instance);
            foreach (var color in editor.SyntaxHighlighting.NamedHighlightingColors)
                color.Foreground = new PaletteHighlightingBrush(editor, color.Name);
        }
        string text = (string?)args.NewValue ?? "";
        if (editor.Text != text) editor.Text = text;
    }
    private sealed class PaletteHighlightingBrush(TextEditor editor, string name) : HighlightingBrush
    {
        public override Brush GetBrush(ITextRunConstructionContext context)
        {
            if (editor.TryFindResource("Syntax" + name) is Brush resource) return resource;
            // The database pane can also be hosted without the application's resource
            // dictionary, for example by a tool window host or the standalone view tests.
            var background = (editor.Background as SolidColorBrush)?.Color ?? Colors.White;
            bool light = .2126 * background.R + .7152 * background.G + .0722 * background.B >= 128;
            return name switch
            {
                "Comment" => light ? Brushes.DarkOliveGreen : Brushes.DarkSeaGreen,
                "String" => light ? Brushes.SaddleBrown : Brushes.BurlyWood,
                _ => light ? Brushes.RoyalBlue : Brushes.LightSkyBlue
            };
        }
    }

    private sealed class EditorState
    {
        private readonly TextEditor _editor;
        private CompletionWindow? _completion;
        public EditorState(TextEditor editor)
        {
            _editor = editor;
            editor.TextChanged += (_, _) => editor.SetCurrentValue(TextProperty, editor.Text);
            editor.TextArea.SelectionChanged += (_, _) =>
            {
                editor.SetCurrentValue(SelectionProperty, editor.SelectedText);
                editor.SetCurrentValue(SelectionLineProperty, editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber);
            };
            editor.PreviewKeyDown += KeyDown;
            editor.Unloaded += (_, _) => _completion?.Close();
        }
        private void KeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.Control) return;
            args.Handled = true;
            _completion?.Close();
            _completion = new CompletionWindow(_editor.TextArea);
            int start = _editor.CaretOffset;
            while (start > 0 && (char.IsLetterOrDigit(_editor.Document.GetCharAt(start - 1)) || _editor.Document.GetCharAt(start - 1) == '_')) start--;
            _completion.StartOffset = start;
            var suggestions = Keywords.Concat(GetCompletions(_editor)?.Cast<object>().Select(item => item.ToString() ?? "") ?? []);
            foreach (string text in suggestions.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                _completion.CompletionList.CompletionData.Add(new SqlCompletion(text));
            _completion.Closed += (_, _) => _completion = null;
            _completion.Show();
        }
    }
    private sealed class SqlCompletion(string text) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => text;
        public object Content => text;
        public object Description => "SQL keyword or browsed schema name";
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => textArea.Document.Replace(completionSegment, text);
    }
    private static readonly string[] Keywords = ["SELECT", "FROM", "WHERE", "INNER JOIN", "LEFT JOIN", "ON", "ORDER BY", "GROUP BY", "HAVING", "INSERT INTO", "UPDATE", "DELETE FROM", "CREATE TABLE", "ALTER TABLE", "EXEC", "DECLARE", "BEGIN TRANSACTION", "COMMIT", "ROLLBACK", "GO", "TOP", "DISTINCT", "AS", "NULL", "IS NULL", "AND", "OR", "CASE", "WHEN", "THEN", "ELSE", "END", "COUNT", "SUM"];
    private const string HighlightingDefinition = """
        <SyntaxDefinition name="SQL" extensions=".sql" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#6A9955" />
          <Color name="String" foreground="#CE9178" />
          <Color name="Keyword" foreground="#569CD6" fontWeight="bold" />
          <RuleSet ignoreCase="true">
            <Span color="Comment" begin="--" />
            <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
            <Span color="String" multiline="true" begin="'" end="'" />
            <Keywords color="Keyword"><Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>JOIN</Word><Word>INNER</Word><Word>LEFT</Word><Word>RIGHT</Word><Word>FULL</Word><Word>ON</Word><Word>ORDER</Word><Word>GROUP</Word><Word>BY</Word><Word>HAVING</Word><Word>INSERT</Word><Word>INTO</Word><Word>UPDATE</Word><Word>DELETE</Word><Word>CREATE</Word><Word>ALTER</Word><Word>DROP</Word><Word>TABLE</Word><Word>VIEW</Word><Word>PROCEDURE</Word><Word>EXEC</Word><Word>DECLARE</Word><Word>BEGIN</Word><Word>TRANSACTION</Word><Word>COMMIT</Word><Word>ROLLBACK</Word><Word>GO</Word><Word>TOP</Word><Word>DISTINCT</Word><Word>AS</Word><Word>NULL</Word><Word>IS</Word><Word>AND</Word><Word>OR</Word><Word>CASE</Word><Word>WHEN</Word><Word>THEN</Word><Word>ELSE</Word><Word>END</Word><Word>SET</Word><Word>VALUES</Word><Word>WITH</Word><Word>UNION</Word><Word>ALL</Word><Word>NOT</Word><Word>EXISTS</Word></Keywords>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
