using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using WpfStudio.App.Controls.Editor;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Controls;

/// <summary>
/// Keys and gestures that act on lines and names: indentation-aware Backspace, Delete and paste, duplicating, moving and
/// commenting lines, opening a line above or below, and Ctrl+Click to go to a definition.
/// </summary>
public sealed partial class EditorSurface
{
    private EditorTyping _typing = null!;
    private EditorGuides _guides = null!;

    private void InitializeEditing()
    {
        _guides = new EditorGuides(this);
        // After the completion handler, which holds commands back while an accepted completion is still being applied.
        CommandManager.AddPreviewExecutedHandler(TextArea, OnEditingCommand);
        DataObject.AddPastingHandler(TextArea, OnPasting);
        TextArea.PreviewMouseLeftButtonDown += OnFollowClick;
        TextArea.PreviewMouseMove += (_, e) => UpdateFollowLink(e);
        TextArea.MouseLeave += (_, _) => ShowFollowLink(null);
        // Pressing Ctrl with the pointer already resting on a name underlines it without waiting for the pointer to move.
        PreviewKeyDown += (_, e) => { if (e.Key is Key.LeftCtrl or Key.RightCtrl && !e.IsRepeat && TextArea.IsMouseOver && FollowsClicks) ShowFollowLink(NameAt(Mouse.GetPosition(this))); };
        PreviewKeyUp += (_, e) => { if (e.Key is Key.LeftCtrl or Key.RightCtrl) ShowFollowLink(null); };
        LostKeyboardFocus += (_, _) => ShowFollowLink(null);
    }

    private void OnEditingCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Handled || ViewModel is not { } vm || !ReferenceEquals(vm, _attached)) return;
        if (e.Command == EditingCommands.Backspace) e.Handled = _typing.TryBackspace(vm);
        else if (e.Command == EditingCommands.Delete) e.Handled = _typing.TryDelete(vm);
    }

    // A whole-line or rectangular copy is pasted by its own rules, so only plain text is moved to the caret's indentation.
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.CommandCancelled || ViewModel is not { } vm || e.DataObject.GetDataPresent("MSDEVLineSelect") || e.DataObject.GetDataPresent("AvalonEditRectangularSelection")) return;
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || _typing.ReindentPaste(vm, text) is not { } reindented) return;
        e.DataObject = new DataObject(DataFormats.UnicodeText, reindented);
    }

    /// <summary>Line commands and their keys. True when the key was one of them and has been dealt with.</summary>
    private bool HandleEditingKey(KeyEventArgs e)
    {
        if (IsReadOnly || ViewModel is null || _completion.IsPending) return false;
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key, modifiers)
        {
            case (Key.D, ModifierKeys.Control): DuplicateLines(); return true;
            case (Key.K, ModifierKeys.Control | ModifierKeys.Shift): AvalonEditCommands.DeleteLine.Execute(null, TextArea); return true;
            case (Key.Up, ModifierKeys.Alt): MoveLines(up: true); return true;
            case (Key.Down, ModifierKeys.Alt): MoveLines(up: false); return true;
            case (Key.OemQuestion or Key.Divide, ModifierKeys.Control): return ToggleComment();
            case (Key.Enter, ModifierKeys.Control) when !_completion.IsWindowOpen: OpenLine(above: false); return true;
            case (Key.Enter, ModifierKeys.Control | ModifierKeys.Shift) when !_completion.IsWindowOpen: OpenLine(above: true); return true;
            default: return false;
        }
    }

    /// <summary>The lines the caret or selection touches. A selection that ends at the start of a line does not include that line.</summary>
    private (DocumentLine First, DocumentLine Last) TouchedLines()
    {
        int start = SelectionLength == 0 ? CaretOffset : SelectionStart, end = SelectionLength == 0 ? CaretOffset : SelectionStart + SelectionLength;
        var first = Document.GetLineByOffset(start);
        var last = Document.GetLineByOffset(end);
        if (SelectionLength > 0 && end == last.Offset && last.LineNumber > first.LineNumber) last = last.PreviousLine;
        return (first, last);
    }

    /// <summary>Ctrl+D: copies the selection after itself, or the caret's line below itself, and leaves the caret in the copy.</summary>
    internal void DuplicateLines()
    {
        if (IsReadOnly) return;
        if (SelectionLength > 0 && TextArea.Selection is not RectangleSelection)
        {
            int end = SelectionStart + SelectionLength;
            var selected = Document.GetText(SelectionStart, SelectionLength);
            Document.Insert(end, selected);
            Select(end, selected.Length);
            return;
        }
        var line = Document.GetLineByOffset(CaretOffset);
        int column = CaretOffset - line.Offset;
        var newLine = TextUtilities.GetNewLineFromDocument(Document, line.LineNumber);
        var text = Document.GetText(line.Offset, line.Length);
        int copy = line.EndOffset + newLine.Length;
        Document.Insert(line.EndOffset, newLine + text);
        TextArea.ClearSelection();
        CaretOffset = copy + column;
        TextArea.Caret.BringCaretToView();
    }

    /// <summary>Alt+Up and Alt+Down: moves the touched lines past the line above or below, keeping the caret and selection on them.</summary>
    internal void MoveLines(bool up)
    {
        if (IsReadOnly || TextArea.Selection is RectangleSelection) return;
        var (first, last) = TouchedLines();
        var neighbour = up ? first.PreviousLine : last.NextLine;
        if (neighbour is null) return;
        int caret = CaretOffset, selectionStart = SelectionStart, selectionLength = SelectionLength;
        bool caretAtStart = selectionLength > 0 && caret == selectionStart;
        var block = Document.GetText(first.Offset, last.EndOffset - first.Offset);
        var other = Document.GetText(neighbour.Offset, neighbour.Length);
        // The break between the two is kept where it is, so a last line without one stays without one.
        var between = up ? Document.GetText(neighbour.EndOffset, neighbour.DelimiterLength) : Document.GetText(last.EndOffset, last.DelimiterLength);
        int start = up ? neighbour.Offset : first.Offset, end = up ? last.EndOffset : neighbour.EndOffset;
        int shift = up ? -(other.Length + between.Length) : other.Length + between.Length;
        using (Document.RunUpdate())
            Document.Replace(start, end - start, up ? block + between + other : other + between + block);
        if (selectionLength > 0)
        {
            int anchor = caretAtStart ? selectionStart + selectionLength : selectionStart;
            TextArea.Selection = Selection.Create(TextArea, anchor + shift, (caretAtStart ? selectionStart : selectionStart + selectionLength) + shift);
        }
        else TextArea.ClearSelection();
        CaretOffset = caret + shift;
        TextArea.Caret.BringCaretToView();
    }

    /// <summary>
    /// Ctrl+/: comments the touched lines, or uncomments them when every one already is a comment. C# and SQL use line
    /// comments; XAML and XML wrap the lines in one comment. False in a language without comments this knows how to write.
    /// </summary>
    internal bool ToggleComment()
    {
        if (IsReadOnly || ViewModel is not { } vm) return false;
        var extension = vm.State.Extension;
        if (extension is ".xaml" or ".xml" or ".csproj" or ".props" or ".targets" or ".resx") { ToggleBlockComment("<!--", "-->"); return true; }
        var marker = extension switch { ".cs" => "//", ".sql" => "--", _ => null };
        if (marker is null) return false;
        var (first, last) = TouchedLines();
        var lines = new List<(DocumentLine Line, int Indent)>();
        for (var line = first; line is not null && line.LineNumber <= last.LineNumber; line = line.NextLine)
        {
            int indent = TextUtilities.GetLeadingWhitespace(Document, line).Length;
            if (indent < line.Length) lines.Add((line, indent));
        }
        if (lines.Count == 0) return true;
        bool commented = lines.All(item => Document.GetText(item.Line.Offset + item.Indent, Math.Min(marker.Length, item.Line.Length - item.Indent)) == marker);
        // Markers line up under the shallowest line, so the commented code keeps its shape.
        int column = lines.Min(item => item.Indent);
        using (Document.RunUpdate())
            foreach (var (line, indent) in lines.AsEnumerable().Reverse())
            {
                if (!commented) { Document.Insert(line.Offset + column, marker + " "); continue; }
                int at = line.Offset + indent, length = marker.Length;
                if (at + length < line.EndOffset && Document.GetCharAt(at + length) == ' ') length++;
                Document.Remove(at, length);
            }
        return true;
    }

    private void ToggleBlockComment(string open, string close)
    {
        var (first, last) = TouchedLines();
        int start = SelectionLength > 0 ? SelectionStart : first.Offset + TextUtilities.GetLeadingWhitespace(Document, first).Length;
        int end = SelectionLength > 0 ? SelectionStart + SelectionLength : last.EndOffset;
        var text = Document.GetText(start, end - start);
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        int contentStart = start + text.IndexOf(trimmed, StringComparison.Ordinal), contentEnd = contentStart + trimmed.Length;
        using (Document.RunUpdate())
        {
            if (trimmed.StartsWith(open, StringComparison.Ordinal) && trimmed.EndsWith(close, StringComparison.Ordinal) && trimmed.Length >= open.Length + close.Length)
            {
                // The markers go, each with the one space that commenting put beside it.
                int openEnd = contentStart + open.Length, closeStart = contentEnd - close.Length;
                if (closeStart > openEnd && Document.GetCharAt(closeStart - 1) == ' ') closeStart--;
                Document.Remove(closeStart, contentEnd - closeStart);
                if (openEnd < closeStart && Document.GetCharAt(openEnd) == ' ') openEnd++;
                Document.Remove(contentStart, openEnd - contentStart);
            }
            else
            {
                Document.Insert(contentEnd, " " + close);
                Document.Insert(contentStart, open + " ");
            }
        }
    }

    /// <summary>Ctrl+Enter and Ctrl+Shift+Enter: opens an indented line below or above the caret's line without breaking it.</summary>
    internal void OpenLine(bool above)
    {
        if (IsReadOnly) return;
        var line = Document.GetLineByOffset(CaretOffset);
        TextArea.ClearSelection();
        if (!above)
        {
            // Enter at the end of the line indents as typing it would.
            CaretOffset = line.EndOffset;
            TextArea.PerformTextInput("\n");
            return;
        }
        var indent = Document.GetText(TextUtilities.GetLeadingWhitespace(Document, line));
        Document.Insert(line.Offset, indent + TextUtilities.GetNewLineFromDocument(Document, line.LineNumber));
        CaretOffset = line.Offset + indent.Length;
        TextArea.Caret.BringCaretToView();
    }

    // ------------------------------------------------------------------ Ctrl+Click

    private bool FollowsClicks => ViewModel is { Preferences: null or { ControlClickNavigation: true } } vm && (vm.IsCSharp || vm.IsXaml);

    /// <summary>The name under the pointer that going to a definition would act on.</summary>
    private (int Start, int Length)? NameAt(Point point)
    {
        if (EditorAdornment.IsUnder(Mouse.DirectlyOver) || GetPositionFromPoint(point) is not { } position) return null;
        int offset = Document.GetOffset(position.Location);
        static bool Part(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or ':';
        int start = offset, end = offset;
        bool xaml = ViewModel?.IsXaml == true;
        // A C# name is one identifier. In XAML a click anywhere in local:Type.Member or Path.To.Member is on that reference.
        bool Name(char c) => xaml ? Part(c) : char.IsLetterOrDigit(c) || c == '_';
        while (start > 0 && Name(Document.GetCharAt(start - 1))) start--;
        while (end < Document.TextLength && Name(Document.GetCharAt(end))) end++;
        return end > start ? (start, end - start) : null;
    }

    // The text view's own cursor while the hand is shown over a name, so that exactly it comes back.
    private object? _cursorBeforeLink;

    private void UpdateFollowLink(MouseEventArgs e) =>
        ShowFollowLink(Keyboard.Modifiers == ModifierKeys.Control && e.LeftButton == MouseButtonState.Released && FollowsClicks ? NameAt(e.GetPosition(this)) : null);

    private void ShowFollowLink((int Start, int Length)? link)
    {
        _guides.SetLink(link);
        var view = TextArea.TextView;
        if (link is not null && _cursorBeforeLink is null)
        {
            _cursorBeforeLink = view.ReadLocalValue(CursorProperty);
            view.Cursor = Cursors.Hand;
        }
        else if (link is null && _cursorBeforeLink is { } before)
        {
            _cursorBeforeLink = null;
            if (before == DependencyProperty.UnsetValue) view.ClearValue(CursorProperty); else view.SetValue(CursorProperty, before);
        }
    }

    private void OnFollowClick(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.ClickCount != 1 || !FollowsClicks || ViewModel is not { } vm
            || NameAt(e.GetPosition(this)) is null || GetPositionFromPoint(e.GetPosition(this)) is not { } position) return;
        e.Handled = true;
        _completion.CancelPending(); _hover.Cancel();
        ShowFollowLink(null);
        TextArea.ClearSelection();
        CaretOffset = Document.GetOffset(position.Location);
        Focus();
        vm.ActionCommand.Execute(EditorAction.Definition);
    }

    // ------------------------------------------------------------------ Members

    /// <summary>
    /// Inserts a member snippet where a member belongs: on the caret's line when that is blank, otherwise on a new line after
    /// it, indented as the lines around it and set off by a blank line. The snippet's fields are then edited in place.
    /// </summary>
    internal void InsertMemberSnippet(WpfStudio.Core.Text.CodeSnippet snippet, string? requiredUsing = null)
    {
        if (IsReadOnly) return;
        var place = MemberPlacement.Find(Text, CaretOffset, Options.IndentationString, TextUtilities.GetNewLineFromDocument(Document, TextArea.Caret.Line));
        using (Document.RunUpdate())
        {
            TextArea.ClearSelection();
            Document.Replace(place.Start, place.Length, place.Before + place.After);
            CaretOffset = place.Start + place.Before.Length;
            // The directive goes in above the caret, which the document moves along with the text.
            if (requiredUsing is not null && MemberPlacement.UsingInsertion(Text, requiredUsing, TextUtilities.GetNewLineFromDocument(Document, 1)) is { } directive)
                Document.Insert(directive.Offset, directive.Text);
            InsertSnippet(snippet, CaretOffset, 0);
        }
        Focus();
    }
}
