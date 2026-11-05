using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Text;

namespace WpfStudio.App.Controls.Editor;

/// <summary>The completion popup and signature help, and accepting an entry whose edit arrives asynchronously.</summary>
internal sealed class EditorCompletion(EditorSurface owner, EditorHover hover, EditorTyping typing)
{
    // Rests on an entry before asking the worker about it, so walking the list does not ask about every row on the way.
    private static readonly TimeSpan DetailsDelay = TimeSpan.FromMilliseconds(60);
    private const double WindowHeight = 312;
    private readonly CompletionInputQueue<(EditorViewModel ViewModel, TextDocument Document)> _input = new(TimeSpan.FromSeconds(5));
    private CompletionWindow? _window;
    private CancellationTokenSource? _request;
    private CancellationTokenSource? _details;
    private ICompletionData? _unconfirmed;
    private int _openedAt;

    public bool IsWindowOpen => _window != null;
    public bool IsPending => _input.IsPending;
    private EditorSurface Surface => owner;

    public void OnTextEntered(object? sender, TextCompositionEventArgs e) => Typed(e.Text);

    /// <summary>What follows a typed character: signature help inside a call, and the completion list as a name begins.</summary>
    private void Typed(string text)
    {
        if (_input.IsReplaying || _input.IsPending) return;
        if (text is "(" or ",") Observe(ShowSignatureAsync());
        else if (text == ")") hover.CloseSignature();
        if (_window != null || text.Length != 1 || owner.ViewModel is not { } vm) return;
        // A dot always lists members. XAML also lists after < and {; names list as they are typed unless that is turned off.
        bool opens = text[0] == '.' || vm.IsXaml && text[0] is '<' or '{'
            || (char.IsLetter(text[0]) || text[0] == '_') && vm.Preferences?.CompleteWhileTyping != false;
        if (opens) Observe(ShowCompletionAsync());
    }

    public void OnTextEntering(object? sender, TextCompositionEventArgs args)
    {
        if (_input.Queue(args.Text)) { args.Handled = true; return; }
        if (!_input.IsReplaying && _window != null && args.Text.Length > 0 && !char.IsLetterOrDigit(args.Text[0]) && args.Text[0] != '_')
        {
            if (IsUnconfirmed(_window)) _window.Close();
            else _window.CompletionList.RequestInsertion(args);
        }
        if (args.Handled || !typing.TryApply(_input.IsReplaying ? owner.AttachedModel : owner.ViewModel, args.Text)) return;
        args.Handled = true;
        // The editor only announces text it inserted itself, so a character typed as part of a planned edit is announced here.
        Typed(args.Text);
    }

    public void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs args)
    {
        if (_input.Queue(args.Command, args.Parameter)) args.Handled = true;
    }

    public void Show() => Observe(ShowCompletionAsync());

    public void CloseWindow() => _window?.Close();

    // The popup opens with an entry selected, so its details show and Tab or Enter can take it. Punctuation cannot until
    // more has been typed: "x.)" should still type ")".
    private bool IsUnconfirmed(CompletionWindow window) =>
        _unconfirmed is not null && ReferenceEquals(window.CompletionList.SelectedItem, _unconfirmed) && owner.CaretOffset == _openedAt;

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
            var window = new CompletionWindow(owner.TextArea) { StartOffset = result.Start, EndOffset = end, CloseWhenCaretAtBeginning = true, MaxHeight = WindowHeight };
            Fill(window.CompletionList, vm, result, contextRevision);
            var list = window.CompletionList;
            if (owner.TryFindResource("CodeCompletionList") is Style rows) list.Style = rows;
            window.Width = CompletionRow.WidthFor(list.CompletionData.OfType<CompletionRow>().ToArray());
            window.SourceInitialized += (_, _) => { if (owner.TryFindResource(Palette.ResourceKey) is Palette palette) WindowChromeService.ApplyFrame(window, palette); };
            // More matched than one reply carries. What is typed next narrows it, so the list is asked for again with that.
            bool incomplete = result.Incomplete;
            void FollowTyping(object? sender, EventArgs args)
            {
                CompletionLabel.SetQuery(window, TypedInto(window));
                if (incomplete) Observe(RefillAsync(window, contextRevision, more => incomplete = more));
            }
            owner.TextArea.Caret.PositionChanged += FollowTyping;
            window.Closed += (_, _) =>
            {
                owner.TextArea.Caret.PositionChanged -= FollowTyping;
                if (_window == window) { _window = null; CancelDetails(); }
            };
            _window = window; window.Show();
            string typed = TypedInto(window);
            CompletionLabel.SetQuery(window, typed);
            var recommended = list.CompletionData.OfType<StudioCompletion>().FirstOrDefault(data => data.IsRecommended);
            if (recommended is null && typed.Length > 0) list.SelectItem(typed);
            list.SelectedItem = recommended ?? list.SelectedItem ?? list.CompletionData.FirstOrDefault();
            _unconfirmed = list.SelectedItem;
            _openedAt = caret;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (vm == owner.ViewModel) vm.LanguageStatus = ex.Message; }
    }

    private void Fill(CompletionList list, EditorViewModel vm, CompletionResult result, long contextRevision)
    {
        foreach (var item in result.Items) list.CompletionData.Add(new StudioCompletion(this, item, result.Version, contextRevision));
        // C# snippets join the list outside member access, as in Visual Studio; Tab or Enter expands them.
        if (vm.IsCSharp && (result.Start == 0 || owner.Document.GetCharAt(result.Start - 1) != '.'))
            foreach (var snippet in SnippetCatalog.For(".cs")) list.CompletionData.Add(new SnippetCompletion(owner, snippet));
    }

    /// <summary>Replaces the entries of an open list whose first reply was cut short, now that more of the name is typed.</summary>
    private async Task RefillAsync(CompletionWindow window, long contextRevision, Action<bool> stillIncomplete)
    {
        if (owner.ViewModel is not { } vm || _window != window || owner.CaretOffset <= window.StartOffset) return;
        CancelRequest(); _request = new();
        var token = _request.Token;
        try
        {
            // The caret moves before the buffer reaches the model, and fast typing should ask once.
            await Task.Delay(30, token);
            int caret = owner.CaretOffset;
            long version = vm.State.Version;
            var result = await vm.CompleteAsync(caret, token);
            if (token.IsCancellationRequested || _window != window || vm != owner.ViewModel || version != vm.State.Version || caret != owner.CaretOffset
                || result.Start != window.StartOffset || result.Items.Count == 0 || vm.IsXaml && contextRevision != vm.XamlContextRevision) return;
            var list = window.CompletionList;
            list.CompletionData.Clear();
            Fill(list, vm, result, contextRevision);
            stillIncomplete(result.Incomplete);
            // The list narrows what it last showed when the typed text only grew, so start it from every new entry.
            list.SelectItem("");
            list.SelectItem(TypedInto(window));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (vm == owner.ViewModel) vm.LanguageStatus = ex.Message; }
    }

    private string TypedInto(CompletionWindow window)
    {
        int caret = owner.CaretOffset;
        return caret > window.StartOffset && caret <= owner.Document.TextLength ? owner.Document.GetText(window.StartOffset, caret - window.StartOffset) : "";
    }

    private void CancelDetails() { _details?.Cancel(); _details?.Dispose(); _details = null; }

    private async Task DescribeAsync(CompletionDetailsCard card, CompletionEntry entry, long version)
    {
        CancelDetails();
        if (owner.ViewModel is not { IsCSharp: true } vm) return;
        _details = new();
        var token = _details.Token;
        try
        {
            await Task.Delay(DetailsDelay, token);
            var info = await vm.CompletionDetailsAsync(entry, version, token);
            if (token.IsCancellationRequested || info is null) return;
            card.Show(info);
            card.IsDescribed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && vm == owner.ViewModel) vm.LanguageStatus = ex.Message; }
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

    private sealed class StudioCompletion(EditorCompletion owner, CompletionEntry entry, long version, long contextRevision)
        : CompletionRow(owner.Surface, entry.DisplayText, entry.Origin, entry.Tags)
    {
        private CompletionDetailsCard? _details;
        public bool IsRecommended => entry.Recommended;
        public override Visibility StarVisibility => entry.Recommended ? Visibility.Visible : Visibility.Collapsed;
        public override object Content => entry.Recommended ? "\u2605 " + entry.DisplayText : entry.DisplayText;
        public override object Description
        {
            get
            {
                _details ??= new CompletionDetailsCard(new HoverCard(owner.Surface), entry, version);
                if (!_details.IsDescribed) owner.Observe(owner.DescribeAsync(_details, entry, version));
                return _details;
            }
        }
        public override double Priority => entry.Recommended ? 1 : 0;
        public override void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) => owner.Observe(owner.AcceptCompletionAsync(entry, version, contextRevision, completionSegment, insertionRequestEventArgs));
    }
    private sealed class SnippetCompletion(EditorSurface owner, CodeSnippet snippet) : CompletionRow(owner, snippet.Shortcut, "Snippet", [], "snippet")
    {
        public override object Description => "Snippet: " + snippet.Title + " (Tab)";
        public override void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (insertionRequestEventArgs is TextCompositionEventArgs typed) typed.Handled = true;
            owner.InsertSnippet(snippet, completionSegment.Offset, completionSegment.Length);
        }
    }
}
