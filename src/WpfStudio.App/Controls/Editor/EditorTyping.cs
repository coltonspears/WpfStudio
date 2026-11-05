using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Controls.Editor;

/// <summary>
/// What a keystroke does beyond its own character: XAML closes tags and quotes, C# pairs brackets and quotes and
/// indents around braces and statements, and in every language Backspace, Delete and paste know about indentation.
/// </summary>
internal sealed class EditorTyping(EditorSurface owner)
{
    private bool ClosesPairs => owner.ViewModel?.Preferences?.AutoCloseBrackets ?? true;
    private bool IsSmart => owner.ViewModel?.Preferences?.SmartBackspace ?? true;

    // AvalonEdit must consume this in TextEntering: a single update contains the
    // typed character and generated suffix, including input replayed after completion.
    public bool TryApply(EditorViewModel? model, string input)
    {
        if (model is not ({ IsXaml: true } or { IsCSharp: true }) || owner.IsReadOnly || owner.TextArea.OverstrikeMode ||
            owner.Document.TextLength > XamlTypingService.MaximumCharacters) return false;
        var newLine = TextUtilities.GetNewLineFromDocument(owner.Document, owner.TextArea.Caret.Line);
        XamlTypingEdit? plan;
        if (!owner.TextArea.Selection.IsEmpty)
        {
            // An opening bracket or quote typed over selected C# wraps it. Anything else replaces the selection, as always.
            if (!model.IsCSharp || !ClosesPairs || owner.TextArea.Selection is RectangleSelection) return false;
            plan = CSharpTypingService.Surround(owner.Text, owner.SelectionStart, owner.SelectionLength, input);
        }
        else
        {
            if (!owner.TextArea.ReadOnlySectionProvider.CanInsert(owner.CaretOffset)) return false;
            // C# gets brace-aware indentation and paired brackets; XAML closes tags and quotes.
            plan = model.IsXaml
                ? XamlTypingService.GetEdit(owner.Text, owner.CaretOffset, owner.SelectionLength, input, owner.Options.IndentationString, newLine)
                : CSharpTypingService.GetEdit(owner.Text, owner.CaretOffset, owner.SelectionLength, input, owner.Options.IndentationString, newLine, ClosesPairs);
        }
        return plan is not null && Apply(plan);
    }

    /// <summary>Backspace between an empty pair of brackets or quotes, or in indentation. False leaves the key to the editor.</summary>
    public bool TryBackspace(EditorViewModel? model)
    {
        if (model is null || !CanEditAtCaret()) return false;
        var plan = model.IsCSharp && ClosesPairs ? CSharpTypingService.Backspace(owner.Text, owner.CaretOffset) : null;
        if (plan is null && IsSmart) plan = SmartEditing.Backspace(owner.Text, owner.CaretOffset, owner.Options.IndentationSize);
        return plan is not null && Apply(plan);
    }

    /// <summary>Delete at the end of a line joins the next line without its indentation. False leaves the key to the editor.</summary>
    public bool TryDelete(EditorViewModel? model)
    {
        if (model is null || !IsSmart || !CanEditAtCaret()) return false;
        return SmartEditing.Delete(owner.Text, owner.CaretOffset) is { } plan && Apply(plan);
    }

    /// <summary>The text to paste in place of what was copied, moved to the caret line's indentation; null to paste it as it is.</summary>
    public string? ReindentPaste(EditorViewModel? model, string pasted)
    {
        if (model is not ({ IsXaml: true } or { IsCSharp: true }) || model.Preferences?.IndentPastedLines == false || !CanEditAtCaret()) return null;
        return SmartEditing.ReindentPaste(owner.Text, owner.CaretOffset, pasted, owner.Options.IndentationSize, owner.Options.ConvertTabsToSpaces);
    }

    private bool CanEditAtCaret() => !owner.IsReadOnly && owner.TextArea.Selection.IsEmpty && owner.TextArea.StackedInputHandlers.IsEmpty
        && owner.Document.TextLength <= XamlTypingService.MaximumCharacters && owner.TextArea.ReadOnlySectionProvider.CanInsert(owner.CaretOffset);

    private bool Apply(XamlTypingEdit plan)
    {
        if (plan.Edit.Length > 0)
        {
            var editable = owner.TextArea.ReadOnlySectionProvider.GetDeletableSegments(new TextSegment { StartOffset = plan.Edit.Start, Length = plan.Edit.Length }).ToArray();
            if (editable.Length != 1 || editable[0].Offset != plan.Edit.Start || editable[0].Length != plan.Edit.Length) return false;
        }
        using (owner.Document.RunUpdate())
        {
            if (plan.Edit.Length != 0 || plan.Edit.NewText.Length != 0)
            {
                owner.Document.Replace(plan.Edit.Start, plan.Edit.Length, plan.Edit.NewText);
                // AvalonEdit records the pre-edit caret for undo. Redo replays
                // that position before the insertion, so it also needs the
                // intended position inside the generated pair after insertion.
                owner.Document.UndoStack.PushOptional(new RestoreTypingCaretOnRedo(owner.TextArea, owner.Document, plan.CaretOffset));
            }
            owner.CaretOffset = plan.CaretOffset;
            owner.TextArea.ClearSelection();
        }
        owner.TextArea.Caret.BringCaretToView();
        return true;
    }

    private sealed class RestoreTypingCaretOnRedo(TextArea textArea, TextDocument document, int caretOffset) : IUndoableOperation
    {
        private readonly WeakReference<TextArea> _textArea = new(textArea);
        private readonly WeakReference<TextDocument> _document = new(document);
        public void Undo() { }
        public void Redo()
        {
            if (!_textArea.TryGetTarget(out var area) || !_document.TryGetTarget(out var buffer) || area.Document != buffer) return;
            area.Caret.Offset = Math.Clamp(caretOffset, 0, buffer.TextLength);
            area.ClearSelection();
        }
    }
}
