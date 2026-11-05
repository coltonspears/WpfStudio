using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using WpfStudio.Contracts;

namespace WpfStudio.App.Controls.Editor;

/// <summary>
/// Signature help: a card above the call being typed that shows one overload with the current argument's parameter
/// picked out and described. It follows the caret from argument to argument, closes when the caret leaves the call,
/// and Up and Down step through the overloads.
/// </summary>
internal sealed class EditorSignatureHelp
{
    private static readonly TimeSpan FollowDelay = TimeSpan.FromMilliseconds(70);
    private readonly EditorSurface _owner;
    private readonly Popup _popup = new() { Placement = PlacementMode.Top, StaysOpen = true, AllowsTransparency = true, PopupAnimation = PopupAnimation.None, Focusable = false };
    private readonly Border _content = new();
    private CancellationTokenSource? _request;
    private SignatureHelpResult? _shown;
    private int _selected;
    // The overload was picked with Up or Down, so it stays while the caret remains in the same call and it still fits.
    private bool _picked;

    public EditorSignatureHelp(EditorSurface owner)
    {
        _owner = owner;
        var chrome = new Border
        {
            Child = _content, Padding = new Thickness(10, 7, 10, 7), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 4),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.28, Direction = 270 }, MaxWidth = 720
        };
        chrome.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "StrongBorderBrush");
        _popup.Child = chrome;
        owner.TextArea.Caret.PositionChanged += (_, _) => { if (IsOpen) Request(follow: true); };
        owner.TextArea.TextView.ScrollOffsetChanged += (_, _) => { if (IsOpen && _shown is { } shown) Place(shown.Start); };
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>The card as it is drawn, for a test to look at.</summary>
    internal FrameworkElement Card => (FrameworkElement)_popup.Child;

    /// <summary>Asks for the call at the caret now: after ( or , is typed, and on Ctrl+Shift+Space.</summary>
    public void Trigger() => Request(follow: false);

    public void Close()
    {
        _request?.Cancel(); _request?.Dispose(); _request = null;
        _popup.IsOpen = false;
        _shown = null;
        _picked = false;
    }

    /// <summary>Shows the next or previous overload. False when there is only one, so the arrow key moves the caret as usual.</summary>
    public bool Cycle(int direction)
    {
        if (!IsOpen || _shown is not { Signatures.Count: > 1 } shown) return false;
        _selected = (_selected + direction + shown.Signatures.Count) % shown.Signatures.Count;
        _picked = true;
        Render(shown);
        return true;
    }

    private void Request(bool follow)
    {
        _request?.Cancel(); _request?.Dispose();
        _request = null;
        if (_owner.ViewModel is not { IsCSharp: true } vm) { Close(); return; }
        var request = _request = new CancellationTokenSource();
        RequestAsync(vm, follow, request.Token).Forget(vm.ReportFault);
    }

    private async Task RequestAsync(ViewModels.EditorViewModel vm, bool follow, CancellationToken token)
    {
        try
        {
            // Typing moves the caret with every key; one question once it rests is enough.
            if (follow) await Task.Delay(FollowDelay, token);
            var result = await vm.SignatureHelpAsync(_owner.CaretOffset, token);
            if (token.IsCancellationRequested || !ReferenceEquals(vm, _owner.ViewModel)) return;
            if (result is null || !_owner.IsKeyboardFocusWithin) { Close(); return; }
            Show(result);
        }
        catch (OperationCanceledException) { }
    }

    private void Show(SignatureHelpResult result)
    {
        bool sameCall = IsOpen && _shown is { } previous && previous.Start == result.Start && previous.Signatures.Count == result.Signatures.Count;
        if (!sameCall || !_picked) { _selected = Math.Clamp(result.ActiveSignature, 0, result.Signatures.Count - 1); _picked = false; }
        // An overload that has no parameter for the argument being typed gives way to the next one that has.
        if (!Takes(result.Signatures[_selected], result.ActiveParameter))
            for (int step = 1; step < result.Signatures.Count; step++)
            {
                int candidate = (_selected + step) % result.Signatures.Count;
                if (Takes(result.Signatures[candidate], result.ActiveParameter)) { _selected = candidate; break; }
            }
        _shown = result;
        Render(result);
        Place(result.Start);
        _popup.IsOpen = true;
    }

    private static bool Takes(SignatureEntry signature, int argument) =>
        argument < signature.Parameters.Count || signature.Parameters.Count > 0 && IsParams(signature.Parameters[^1]) || argument == 0;

    private static bool IsParams(SignatureParameter parameter) => parameter.Display?.StartsWith("params ", StringComparison.Ordinal) == true;

    /// <summary>The parameter the argument at <paramref name="argument"/> goes to: its own, or a trailing params array.</summary>
    internal static int ActiveParameter(SignatureEntry signature, int argument) =>
        argument < signature.Parameters.Count ? argument
        : signature.Parameters.Count > 0 && IsParams(signature.Parameters[^1]) ? signature.Parameters.Count - 1 : -1;

    private void Render(SignatureHelpResult result)
    {
        var signature = result.Signatures[_selected];
        int active = ActiveParameter(signature, result.ActiveParameter);
        var text = new TextBlock { FontFamily = _owner.FontFamily, FontSize = _owner.FontSize, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var prefix = signature.Prefix ?? (signature.Label.IndexOf('(') is > 0 and var paren ? signature.Label[..paren] : signature.Label);
        text.Inlines.Add(new Run(prefix + "("));
        for (int index = 0; index < signature.Parameters.Count; index++)
        {
            if (index > 0) text.Inlines.Add(new Run(", "));
            var parameter = signature.Parameters[index];
            var run = new Run(parameter.Display ?? parameter.Type + " " + parameter.Name);
            if (index == active)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            }
            text.Inlines.Add(run);
        }
        text.Inlines.Add(new Run(")"));

        var card = new StackPanel();
        if (result.Signatures.Count > 1)
        {
            var count = Note($"{_selected + 1} of {result.Signatures.Count} overloads   ↑ ↓", 11);
            count.Margin = new Thickness(0, 0, 0, 3);
            card.Children.Add(count);
        }
        card.Children.Add(text);
        // What the argument being typed is for; before any parameter, what the method does.
        var parameterNote = active >= 0 && signature.Parameters[active].Documentation is { Length: > 0 } described
            ? signature.Parameters[active].Name + ": " + described : null;
        if ((parameterNote ?? signature.Documentation) is { Length: > 0 } note)
        {
            var documentation = Note(note, 12);
            documentation.Margin = new Thickness(0, 5, 0, 0);
            card.Children.Add(documentation);
        }
        _content.Child = card;
    }

    private TextBlock Note(string text, double size)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (_owner.TryFindResource("UiFont") is FontFamily font) block.FontFamily = font;
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return block;
    }

    // Above where the arguments begin, so the card stays put while they are typed and never covers them.
    private void Place(int start)
    {
        var view = _owner.TextArea.TextView;
        var document = _owner.Document;
        var caret = _owner.TextArea.Caret.CalculateCaretRectangle();
        var anchor = new Rect(caret.X - view.HorizontalOffset, caret.Y - view.VerticalOffset, 1, caret.Height);
        if (start >= 0 && start <= document.TextLength && view.VisualLinesValid)
        {
            var location = document.GetLocation(start);
            // A call that began on a line above the caret's is anchored to the caret's line, where the typing is.
            if (location.Line == _owner.TextArea.Caret.Line)
            {
                var point = view.GetVisualPosition(new TextViewPosition(location), VisualYPosition.LineTop);
                anchor = new Rect(point.X - view.HorizontalOffset, point.Y - view.VerticalOffset, 1, caret.Height);
            }
        }
        _popup.PlacementTarget = view;
        _popup.PlacementRectangle = anchor;
        _popup.VerticalOffset = -2;
        // An open popup only moves when one of its offsets changes.
        if (_popup.IsOpen) { _popup.HorizontalOffset += 0.01; _popup.HorizontalOffset -= 0.01; }
    }
}
