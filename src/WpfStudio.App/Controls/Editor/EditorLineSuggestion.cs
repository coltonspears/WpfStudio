using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Rendering;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;

namespace WpfStudio.App.Controls.Editor;

/// <summary>
/// Code Assist's grey continuation of the current line. It is asked for after a pause in typing at the end of a line,
/// drawn after the caret, inserted by Tab, and gone on any other edit or caret move.
/// </summary>
internal sealed class EditorLineSuggestion(EditorSurface owner, Func<bool> completionBusy) : VisualLineElementGenerator
{
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(300);
    private CancellationTokenSource? _request;
    private string? _text;
    private int _offset = -1, _emittedAt = -1;

    public bool IsVisible => _text is not null;

    /// <summary>The user changed the text. With the feature off this is one property read per edit.</summary>
    public void OnTextChanged()
    {
        Cancel();
        if (owner.ViewModel is not { SuggestsLines: true } vm) return;
        _request = new();
        RequestAsync(vm, _request.Token).Forget(vm.ReportFault);
    }

    public void OnCaretMoved()
    {
        if (_text is not null && owner.CaretOffset != _offset) Hide();
    }

    /// <summary>Inserts the suggestion; false when none is showing, so the key keeps its usual meaning.</summary>
    public bool TryAccept()
    {
        if (_text is not { } text || owner.IsReadOnly || owner.CaretOffset != _offset) return false;
        int offset = _offset;
        Hide();
        owner.Document.Insert(offset, text);
        owner.CaretOffset = offset + text.Length;
        return true;
    }

    public void Cancel()
    {
        _request?.Cancel(); _request?.Dispose(); _request = null;
        Hide();
    }

    private async Task RequestAsync(EditorViewModel vm, CancellationToken token)
    {
        try
        {
            await Task.Delay(TypingPause, token);
            if (!CanSuggestAt(vm, vm.State.Version, owner.CaretOffset)) return;
            long version = vm.State.Version;
            int caret = owner.CaretOffset;
            string? text = await vm.SuggestLineAsync(caret, token);
            if (text is null || token.IsCancellationRequested || caret != owner.CaretOffset || !CanSuggestAt(vm, version, caret)) return;
            Show(text, caret);
        }
        catch (OperationCanceledException) { }
    }

    private bool CanSuggestAt(EditorViewModel vm, long version, int caret)
    {
        if (vm != owner.ViewModel || version != vm.State.Version || !vm.SuggestsLines || completionBusy() || owner.SelectionLength > 0 || !owner.IsKeyboardFocusWithin) return false;
        // Only at the end of a line: anything after the caret would have to be read past, and the model cannot see it.
        var line = owner.Document.GetLineByOffset(caret);
        return string.IsNullOrWhiteSpace(owner.Document.GetText(caret, line.EndOffset - caret));
    }

    public void Show(string text, int offset)
    {
        _text = text; _offset = offset;
        owner.TextArea.TextView.Redraw(offset, 0);
    }

    private void Hide()
    {
        if (_text is null) return;
        int offset = Math.Min(_offset, owner.Document.TextLength);
        _text = null; _offset = -1;
        owner.TextArea.TextView.Redraw(offset, 0);
    }

    public override void StartGeneration(ITextRunConstructionContext context) { base.StartGeneration(context); _emittedAt = -1; }

    public override int GetFirstInterestedOffset(int startOffset) =>
        _text is null || _offset < startOffset || _offset == _emittedAt || _offset > CurrentContext.VisualLine.LastDocumentLine.EndOffset ? -1 : _offset;

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (_text is not { } suggestion || offset != _offset || offset == _emittedAt) return null;
        _emittedAt = offset;
        var text = new TextBlock { Text = suggestion, FontFamily = owner.FontFamily, FontSize = owner.FontSize, FontStyle = FontStyles.Italic, IsHitTestVisible = false };
        text.SetResourceReference(TextBlock.ForegroundProperty, "InlayHintBrush");
        TextBlock.SetBaselineOffset(text, owner.FontSize * owner.FontFamily.Baseline);
        return new HintElement(text);
    }
}
