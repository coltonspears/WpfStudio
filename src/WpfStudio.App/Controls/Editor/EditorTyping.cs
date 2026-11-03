using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Controls.Editor;

/// <summary>What typing generates beyond the typed text: XAML closes tags and quotes, C# indents around braces.</summary>
internal sealed class EditorTyping(EditorSurface owner)
{
    // AvalonEdit must consume this in TextEntering: a single update contains the
    // typed character and generated suffix, including input replayed after completion.
    public bool TryApply(EditorViewModel? model, string input)
    {
        if (model is not ({ IsXaml: true } or { IsCSharp: true }) || owner.IsReadOnly || owner.TextArea.OverstrikeMode || !owner.TextArea.Selection.IsEmpty ||
            owner.Document.TextLength > XamlTypingService.MaximumCharacters || !owner.TextArea.ReadOnlySectionProvider.CanInsert(owner.CaretOffset)) return false;
        var newLine = TextUtilities.GetNewLineFromDocument(owner.Document, owner.TextArea.Caret.Line);
        // C# gets brace-aware indentation for Enter, { and }; XAML closes tags and quotes.
        var plan = model.IsXaml
            ? XamlTypingService.GetEdit(owner.Text, owner.CaretOffset, owner.SelectionLength, input, owner.Options.IndentationString, newLine)
            : CSharpTypingService.GetEdit(owner.Text, owner.CaretOffset, owner.SelectionLength, input, owner.Options.IndentationString, newLine);
        if (plan is null) return false;
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
