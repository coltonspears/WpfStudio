using System.Text;
using System.Text.RegularExpressions;

namespace WpfStudio.Core.Text;

/// <summary>
/// A code snippet. In <see cref="Body"/>, <c>$name=default$</c> declares an editable field, a later <c>$name$</c>
/// repeats its value, <c>$end$</c> places the caret, <c>$selected$</c> wraps the selection, <c>$classname$</c> is the
/// enclosing type's name and <c>$$</c> writes a dollar sign. Lines use '\n' and four-space indentation; the editor
/// re-indents them at the insertion point.
/// </summary>
public sealed record CodeSnippet(string Shortcut, string Title, string Extension, string Body)
{
    public bool WrapsSelection => Body.Contains("$selected$", StringComparison.Ordinal);
}

public abstract record SnippetPart;
public sealed record SnippetLiteral(string Text) : SnippetPart;
public sealed record SnippetField(string Name, string Default) : SnippetPart;
public sealed record SnippetMirror(string Name) : SnippetPart;
public sealed record SnippetCaret : SnippetPart;
public sealed record SnippetSelection : SnippetPart;

public static partial class SnippetCatalog
{
    public static IReadOnlyList<CodeSnippet> All { get; } =
    [
        // CommunityToolkit.Mvvm
        new("obsprop", "Observable property (partial property)", ".cs",
            "[ObservableProperty]\npublic partial $type=string$ $Name=Title$ { get; set; }$end$"),
        new("obsfield", "Observable property (field)", ".cs",
            "[ObservableProperty]\nprivate $type=string$ _$name=title$;$end$"),
        new("relay", "Relay command", ".cs",
            "[RelayCommand]\nprivate void $Name=Refresh$()\n{\n    $end$\n}"),
        new("relayasync", "Async relay command with cancellation", ".cs",
            "[RelayCommand]\nprivate async Task $Name=Load$Async(CancellationToken cancellationToken)\n{\n    $end$\n}"),
        new("relaycan", "Relay command with CanExecute", ".cs",
            "[RelayCommand(CanExecute = nameof(Can$Name=Save$))]\nprivate void $Name$()\n{\n    $end$\n}\n\nprivate bool Can$Name$() => true;"),
        new("notify", "Property with SetProperty", ".cs",
            "private $type=string$ _$field=title$;\npublic $type$ $Name=Title$\n{\n    get => _$field$;\n    set => SetProperty(ref _$field$, value);\n}$end$"),
        new("msg", "Messenger recipient", ".cs",
            "public void Receive($message=ItemSavedMessage$ message)\n{\n    $end$\n}"),
        // WPF
        new("dp", "Dependency property", ".cs",
            "public static readonly DependencyProperty $Name=Value$Property =\n    DependencyProperty.Register(nameof($Name$), typeof($type=string$), typeof($classname$), new PropertyMetadata($default=null$));\n\npublic $type$ $Name$\n{\n    get => ($type$)GetValue($Name$Property);\n    set => SetValue($Name$Property, value);\n}$end$"),
        new("dpa", "Attached property", ".cs",
            "public static readonly DependencyProperty $Name=IsEnabled$Property =\n    DependencyProperty.RegisterAttached(\"$Name$\", typeof($type=bool$), typeof($classname$), new PropertyMetadata($default=false$));\n\npublic static $type$ Get$Name$(DependencyObject element) => ($type$)element.GetValue($Name$Property);\npublic static void Set$Name$(DependencyObject element, $type$ value) => element.SetValue($Name$Property, value);$end$"),
        new("ivc", "Value converter", ".cs",
            "public sealed class $Name=Bool$Converter : IValueConverter\n{\n    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)\n    {\n        $end$return value;\n    }\n\n    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();\n}"),
        // General C#
        new("prop", "Auto-property", ".cs", "public $type=int$ $Name=MyProperty$ { get; set; }$end$"),
        new("ctor", "Constructor", ".cs", "public $classname$($end$)\n{\n}"),
        new("if", "if statement", ".cs", "if ($condition=true$)\n{\n    $selected$$end$\n}"),
        new("foreach", "foreach loop", ".cs", "foreach (var $item=item$ in $collection=items$)\n{\n    $selected$$end$\n}"),
        new("try", "try/catch", ".cs", "try\n{\n    $selected$$end$\n}\ncatch ($exception=Exception$ ex)\n{\n    throw;\n}"),
        // XAML
        new("grid", "Grid with rows and columns", ".xaml",
            "<Grid>\n    <Grid.RowDefinitions>\n        <RowDefinition Height=\"Auto\" />\n        <RowDefinition Height=\"*\" />\n    </Grid.RowDefinitions>\n    <Grid.ColumnDefinitions>\n        <ColumnDefinition Width=\"*\" />\n    </Grid.ColumnDefinitions>\n    $selected$$end$\n</Grid>"),
        new("stack", "StackPanel", ".xaml", "<StackPanel Orientation=\"$orientation=Vertical$\">\n    $selected$$end$\n</StackPanel>"),
        new("style", "Style with a setter", ".xaml",
            "<Style x:Key=\"$key=PrimaryButton$\" TargetType=\"$type=Button$\">\n    <Setter Property=\"$property=Margin$\" Value=\"$value=4$\" />$end$\n</Style>"),
        new("template", "Typed DataTemplate", ".xaml", "<DataTemplate DataType=\"{x:Type $type=local:ItemViewModel$}\">\n    $end$\n</DataTemplate>"),
        new("trigger", "DataTrigger", ".xaml",
            "<DataTrigger Binding=\"{Binding $path=IsBusy$}\" Value=\"$value=True$\">\n    <Setter Property=\"$property=Visibility$\" Value=\"$setter=Collapsed$\" />\n</DataTrigger>$end$"),
        new("bind", "Binding expression", ".xaml", "{Binding $path=Name$}$end$"),
        new("cmd", "Button bound to a command", ".xaml", "<Button Content=\"$text=Save$\" Command=\"{Binding $command=SaveCommand$}\" />$end$"),
    ];

    public static IEnumerable<CodeSnippet> For(string extension) =>
        All.Where(snippet => snippet.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase));

    public static CodeSnippet? Find(string shortcut, string extension) =>
        For(extension).FirstOrDefault(snippet => snippet.Shortcut.Equals(shortcut, StringComparison.Ordinal));

    /// <summary>Splits a snippet body into literal text, fields, mirrors and markers. The first occurrence of a name is the field.</summary>
    public static IReadOnlyList<SnippetPart> Parse(string body)
    {
        var parts = new List<SnippetPart>();
        var literal = new StringBuilder();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '$') { literal.Append(body[i]); continue; }
            var close = body.IndexOf('$', i + 1);
            if (close < 0) { literal.Append(body, i, body.Length - i); break; }
            var token = body[(i + 1)..close];
            i = close;
            if (token.Length == 0) { literal.Append('$'); continue; }
            if (literal.Length > 0) { parts.Add(new SnippetLiteral(literal.ToString())); literal.Clear(); }
            var equals = token.IndexOf('=');
            var name = equals < 0 ? token : token[..equals];
            if (name == "end") parts.Add(new SnippetCaret());
            else if (name == "selected") parts.Add(new SnippetSelection());
            else if (fields.Add(name)) parts.Add(new SnippetField(name, equals < 0 ? name : token[(equals + 1)..]));
            else parts.Add(new SnippetMirror(name));
        }
        if (literal.Length > 0) parts.Add(new SnippetLiteral(literal.ToString()));
        return parts;
    }

    /// <summary>
    /// Replaces automatic fields: <c>$classname$</c> becomes the type declared closest before <paramref name="offset"/>.
    /// </summary>
    public static IReadOnlyList<SnippetPart> Resolve(IReadOnlyList<SnippetPart> parts, string text, int offset)
    {
        var className = EnclosingTypeName(text, offset) ?? "MyClass";
        return parts.Select(part => part switch
        {
            SnippetField { Name: "classname" } => new SnippetField("classname", className),
            _ => part
        }).ToArray();
    }

    public static string? EnclosingTypeName(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var match = TypeDeclaration().Matches(text[..offset]).LastOrDefault();
        return match?.Groups["name"].Value;
    }

    /// <summary>The shortcut directly before the caret, when it is a whole word that is not a member access.</summary>
    public static string? WordBefore(string text, int caret, out int start)
    {
        start = caret;
        if (caret < 0 || caret > text.Length || caret < text.Length && IsWordPart(text[caret])) return null;
        while (start > 0 && IsWordPart(text[start - 1])) start--;
        if (start == caret || start > 0 && text[start - 1] is '.' or '$' or '@') return null;
        return text[start..caret];
    }

    private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    [GeneratedRegex(@"\b(?:class|struct|record|interface)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclaration();
}
