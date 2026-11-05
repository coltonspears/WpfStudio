using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Snippets;
using WpfStudio.App.ViewModels;
using WpfStudio.Core.Text;

namespace WpfStudio.App.Controls;

/// <summary>Editing commands that act on the control's own caret and document: the quick actions menu and snippets.</summary>
public sealed partial class EditorSurface
{
    private ContextMenu? _quickActions;
    /// <summary>Ctrl+. in C#: a menu at the caret listing fixes first, then refactorings. Each opens the change review.</summary>
    private async Task ShowQuickActionsAsync(EditorViewModel vm)
    {
        if (_quickActions is { } previous) previous.IsOpen = false;
        var view = TextArea.TextView;
        var caret = TextArea.Caret.CalculateCaretRectangle();
        var menu = new ContextMenu
        {
            PlacementTarget = view, Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = caret.Left - view.HorizontalOffset, VerticalOffset = caret.Bottom - view.VerticalOffset
        };
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Finding quick actions…" }, IsEnabled = false });
        _quickActions = menu;
        menu.IsOpen = true;
        // A failure shows as the menu's text below; the menu must not stay on "Finding quick actions…".
        try { await vm.RefreshQuickFixesAsync(CaretOffset); }
        catch (Exception ex) { vm.LanguageStatus = ex.Message; }
        if (!ReferenceEquals(_quickActions, menu) || !menu.IsOpen || vm != ViewModel) return;
        menu.Items.Clear();
        if (vm.CodeActions.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = vm.LanguageStatus is { Length: > 0 } status ? status : "No quick actions here" }, IsEnabled = false });
            return;
        }
        bool? fixes = null;
        foreach (var item in vm.CodeActions)
        {
            if (fixes is not null && fixes != item.IsFix) menu.Items.Add(new Separator());
            fixes = item.IsFix;
            // A TextBlock header keeps underscores in titles from becoming access keys.
            menu.Items.Add(new MenuItem { Header = new TextBlock { Text = item.Title }, Command = item.ApplyCommand, CommandParameter = item.Action, ToolTip = item.IsFix ? "Code fix" : "Refactoring" });
        }
        if (menu.Items[0] is MenuItem first) first.Focus();
    }
    /// <summary>Expands the snippet whose shortcut is the word before the caret. Returns false to let Tab indent.</summary>
    internal bool TryExpandSnippet()
    {
        if (ViewModel is not { } vm || IsReadOnly || _completion.IsWindowOpen || _completion.IsPending || !TextArea.Selection.IsEmpty
            || !TextArea.StackedInputHandlers.IsEmpty) return false; // An active snippet's fields own Tab.
        if (SnippetCatalog.WordBefore(Text, CaretOffset, out var start) is not { } word || SnippetCatalog.Find(word, vm.State.Extension) is not { } snippet) return false;
        InsertSnippet(snippet, start, CaretOffset - start);
        return true;
    }
    private void InsertRequestedSnippet(CodeSnippet snippet)
    {
        if (IsReadOnly) return;
        InsertSnippet(snippet, CaretOffset, 0);
        Focus();
    }
    private bool InsertRequestedMember(CodeSnippet snippet, string? requiredUsing)
    {
        if (IsReadOnly || !ReferenceEquals(ViewModel, _attached)) return false;
        _completion.CancelPending(); _completion.CloseWindow();
        InsertMemberSnippet(snippet, requiredUsing);
        return true;
    }
    /// <summary>Replaces <paramref name="length"/> characters at <paramref name="start"/> with an interactive snippet: Tab moves between fields, Enter or Esc finishes.</summary>
    internal void InsertSnippet(CodeSnippet snippet, int start, int length)
    {
        var parts = SnippetCatalog.Resolve(SnippetCatalog.Parse(snippet.Body), Text, start);
        var expansion = new Snippet();
        var fields = new Dictionary<string, SnippetReplaceableTextElement>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            switch (part)
            {
                // Snippet bodies are written with four spaces a level. Tabs become this editor's own indentation as they are inserted.
                case SnippetLiteral literal: expansion.Elements.Add(new SnippetTextElement { Text = SnippetIndentation().Replace(literal.Text, match => new string('\t', match.Length / 4)) }); break;
                case SnippetField field: expansion.Elements.Add(fields[field.Name] = new SnippetReplaceableTextElement { Text = field.Default }); break;
                case SnippetMirror mirror: expansion.Elements.Add(new SnippetBoundElement { TargetElement = fields[mirror.Name] }); break;
                case SnippetCaret: expansion.Elements.Add(new SnippetCaretElement()); break;
                case SnippetSelection: expansion.Elements.Add(new SnippetSelectionElement()); break;
            }
        }
        // One undo step removes both the shortcut and the expansion.
        using (Document.RunUpdate())
        {
            if (length > 0) { Document.Remove(start, length); CaretOffset = start; }
            expansion.Insert(TextArea);
        }
        TextArea.Caret.BringCaretToView();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=\n)(?:    )+")]
    private static partial System.Text.RegularExpressions.Regex SnippetIndentation();
}
