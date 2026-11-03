using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Text;

namespace WpfStudio.App.Controls.Editor;

/// <summary>The completion popup and signature help, and accepting an entry whose edit arrives asynchronously.</summary>
internal sealed class EditorCompletion(EditorSurface owner, EditorHover hover, EditorTyping typing)
{
    private readonly CompletionInputQueue<(EditorViewModel ViewModel, TextDocument Document)> _input = new(TimeSpan.FromSeconds(5));
    private CompletionWindow? _window;
    private CancellationTokenSource? _request;
    private ICompletionData? _unconfirmed;

    public bool IsWindowOpen => _window != null;
    public bool IsPending => _input.IsPending;

    public void OnTextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (_input.IsReplaying || _input.IsPending) return;
        if (e.Text is "(" or ",") Observe(ShowSignatureAsync());
        else if (e.Text == ")") hover.CloseSignature();
        if (_window == null && e.Text.Length == 1 && (e.Text[0] is '.' or '<' or '{' || char.IsLetter(e.Text[0]))) Observe(ShowCompletionAsync());
    }

    public void OnTextEntering(object? sender, TextCompositionEventArgs args)
    {
        if (_input.Queue(args.Text)) { args.Handled = true; return; }
        if (!_input.IsReplaying && _window != null && args.Text.Length > 0 && !char.IsLetterOrDigit(args.Text[0]) && args.Text[0] != '_')
        {
            if (IsUnconfirmed(_window)) _window.Close();
            else _window.CompletionList.RequestInsertion(args);
        }
        if (!args.Handled && typing.TryApply(_input.IsReplaying ? owner.AttachedModel : owner.ViewModel, args.Text)) args.Handled = true;
    }

    public void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs args)
    {
        if (_input.Queue(args.Command, args.Parameter)) args.Handled = true;
    }

    public void Show() => Observe(ShowCompletionAsync());

    public void CloseWindow() => _window?.Close();

    // A recommendation is selected before anything is typed, so Tab or Enter can take it. Punctuation cannot: "x.)" should still type ")".
    private bool IsUnconfirmed(CompletionWindow window) =>
        _unconfirmed is not null && ReferenceEquals(window.CompletionList.SelectedItem, _unconfirmed) && owner.CaretOffset == window.StartOffset;

    // Takes the model now: the dependency property cannot be read from the thread a fault may arrive on.
    private void Observe(Task request) => request.Forget(owner.ViewModel is { } vm ? vm.ReportFault : null);

    public void CancelRequest() { _request?.Cancel(); _request?.Dispose(); _request = null; }

    private async Task ShowCompletionAsync()
    {
        if (owner.ViewModel == null || owner.IsReadOnly || _input.IsPending) return;
        CancelRequest(); _request = new();
        var token = _request.Token; var vm = owner.ViewModel; var version = vm.State.Version; var caret = owner.CaretOffset;
        long contextRevision = vm.XamlContextRevision;
        try
        {
            var result = await vm.CompleteAsync(caret, token);
            if (token.IsCancellationRequested || vm != owner.ViewModel || version != vm.State.Version || caret != owner.CaretOffset || result.Items.Count == 0 || !owner.IsKeyboardFocusWithin
                || vm.IsXaml && contextRevision != vm.XamlContextRevision) return;
            _window?.Close();
            var end = vm.State.Extension == ".xaml" ? Math.Clamp(result.Start + result.Length, caret, owner.Document.TextLength) : caret;
            var window = new CompletionWindow(owner.TextArea) { StartOffset = result.Start, EndOffset = end, CloseWhenCaretAtBeginning = true };
            foreach (var item in result.Items.Take(250)) window.CompletionList.CompletionData.Add(new StudioCompletion(this, item, result.Version, contextRevision));
            // C# snippets join the list outside member access, as in Visual Studio; Tab or Enter expands them.
            if (vm.IsCSharp && (result.Start == 0 || owner.Document.GetCharAt(result.Start - 1) != '.'))
                foreach (var snippet in SnippetCatalog.For(".cs")) window.CompletionList.CompletionData.Add(new SnippetCompletion(owner, snippet));
            window.Closed += (_, _) => { if (_window == window) _window = null; };
            _window = window; window.Show();
            _unconfirmed = window.CompletionList.CompletionData.OfType<StudioCompletion>().FirstOrDefault(data => data.IsRecommended);
            if (_unconfirmed is not null) window.CompletionList.SelectedItem = _unconfirmed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (vm == owner.ViewModel) vm.LanguageStatus = ex.Message; }
    }
    private async Task ShowSignatureAsync()
    {
        if (owner.ViewModel is not { } vm) return;
        long version = vm.State.Version;
        int caret = owner.CaretOffset;
        try
        {
            var text = await vm.SignatureAsync(caret);
            if (vm != owner.ViewModel || vm.State.Version != version || caret != owner.CaretOffset || !owner.IsKeyboardFocusWithin) return;
            if (!string.IsNullOrEmpty(text)) hover.ShowSignature(text);
        }
        catch (Exception ex) { if (vm == owner.ViewModel) vm.LanguageStatus = ex.Message; }
    }

    private Task AcceptCompletionAsync(CompletionEntry entry, long version, long contextRevision, ISegment segment, EventArgs insertionRequest)
    {
        if (owner.ViewModel is not { } vm) return Task.CompletedTask;
        string? punctuation = insertionRequest is TextCompositionEventArgs textInput ? textInput.Text : null;
        if (insertionRequest is TextCompositionEventArgs textArgs) textArgs.Handled = true;
        int start = segment.Offset, length = segment.Length;
        return CommitAsync(async token =>
        {
            if (vm.IsXaml) return (vm.State.Version, vm.XamlCompletionEdit(entry, start, length, contextRevision));
            if (vm.State.Extension != ".cs") return (vm.State.Version, new TextEdit(start, length, entry.InsertText));
            if (version != vm.State.Version)
            {
                var current = await vm.CompleteAsync(owner.CaretOffset, token);
                entry = current.Items.FirstOrDefault(item => item.DisplayText == entry.DisplayText)
                    ?? throw new InvalidOperationException("Completion changed. Typed input was kept; request completion again.");
                version = current.Version;
            }
            var edit = await vm.CompletionEditAsync(entry, version, token)
                ?? throw new InvalidOperationException("Completion is no longer available. Typed input was kept.");
            return (version, edit);
        }, punctuation);
    }

    // Keep text and editing commands in order while Roslyn computes the actual change.
    // In particular, AvalonEdit's synchronous completion callback must not let the
    // triggering '(' reach the document before the asynchronous replacement returns.
    public async Task CommitAsync(Func<CancellationToken, Task<(long Version, TextEdit Edit)>> resolve, string? punctuation = null)
    {
        if (owner.ViewModel is not { } vm || owner.IsReadOnly) return;
        CancelPending();
        var pending = _input.Begin((vm, owner.Document), punctuation);
        try
        {
            var result = await resolve(pending.Cancellation.Token);
            if (!_input.IsCurrent(pending) || pending.Cancellation.IsCancellationRequested) return;
            if (owner.ViewModel != vm || owner.Document != pending.Target.Document || vm.State.Version != result.Version)
            { vm.LanguageStatus = "The document changed before completion finished. Typed input was kept."; return; }
            if (result.Edit.Start < 0 || result.Edit.Length < 0 || result.Edit.Start + result.Edit.Length > owner.Document.TextLength)
                throw new InvalidOperationException("The completion edit is outside the current document.");
            owner.Document.Replace(result.Edit.Start, result.Edit.Length, result.Edit.NewText);
            owner.CaretOffset = result.Edit.Start + result.Edit.NewText.Length;
            vm.LanguageStatus = "";
        }
        catch (OperationCanceledException) { vm.LanguageStatus = "Completion cancelled. Typed input was kept."; }
        catch (Exception exception) { vm.LanguageStatus = exception.Message; }
        finally
        {
            if (_input.IsCurrent(pending)) Replay(pending);
            pending.Cancellation.Dispose();
        }
    }
    public void CancelPending()
    {
        if (_input.Current is not { } pending) return;
        pending.Cancellation.Cancel(); Replay(pending);
    }

    private void Replay(CompletionInputQueue<(EditorViewModel ViewModel, TextDocument Document)>.Pending pending) => owner.RunSynced(() => _input.Replay(() =>
    {
        // Detach invokes this before exchanging buffers. Always update the
        // captured model, even when the dependency property already points to a new tab.
        if (owner.Document != pending.Target.Document) return;
        foreach (var input in pending.Input)
        {
            if (input.Text is not null) owner.TextArea.PerformTextInput(input.Text);
            else if (input.Command?.CanExecute(input.Parameter, owner.TextArea) == true) input.Command.Execute(input.Parameter, owner.TextArea);
        }
        pending.Target.ViewModel.State.Content = pending.Target.Document.Text;
        pending.Target.ViewModel.ReportCaret(owner.TextArea.Caret.Line, owner.TextArea.Caret.Column, owner.CaretOffset);
    }));

    private sealed class StudioCompletion(EditorCompletion owner, CompletionEntry entry, long version, long contextRevision) : ICompletionData
    {
        public ImageSource? Image => null;
        public bool IsRecommended => entry.Recommended;
        public string Text => entry.DisplayText;
        public object Content => entry.Recommended ? "\u2605 " + entry.DisplayText : entry.DisplayText;
        public object Description => entry.Description ?? string.Join(", ", entry.Tags);
        public double Priority => entry.Recommended ? 1 : 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => owner.Observe(owner.AcceptCompletionAsync(entry, version, contextRevision, completionSegment, insertionRequestEventArgs));
    }
    private sealed class SnippetCompletion(EditorSurface owner, CodeSnippet snippet) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => snippet.Shortcut;
        public object Content => snippet.Shortcut;
        public object Description => "Snippet: " + snippet.Title + " (Tab)";
        public double Priority => 0;
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (insertionRequestEventArgs is TextCompositionEventArgs typed) typed.Handled = true;
            owner.InsertSnippet(snippet, completionSegment.Offset, completionSegment.Length);
        }
    }
}
