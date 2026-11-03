using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core.Text;

namespace WpfStudio.App.Controls.Diff;

public enum DiffViewMode { SideBySide, Inline }

/// <summary>
/// Read-only, syntax-highlighted diff of two texts. Split view aligns the sides with fillers and scrolls them
/// together; inline view interleaves removed and added lines. Unchanged regions beyond a few lines of context
/// collapse into clickable separators unless <see cref="ShowWholeFile"/> is set. Changed words are highlighted
/// inside changed lines, and an overview ruler marks every change.
/// </summary>
public partial class DiffView : UserControl
{
    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(nameof(Before), typeof(string), typeof(DiffView), new PropertyMetadata(null, ContentChanged));
    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(nameof(After), typeof(string), typeof(DiffView), new PropertyMetadata(null, ContentChanged));
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(DiffView), new PropertyMetadata(null, (d, _) => ((DiffView)d).ApplyHighlighting()));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(DiffView), new PropertyMetadata(null, ContentChanged));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(DiffView), new PropertyMetadata("Select a file to see its changes.", ContentChanged));
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(DiffViewMode), typeof(DiffView), new FrameworkPropertyMetadata(DiffViewMode.SideBySide, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((DiffView)d).ModeChanged()));
    private static readonly DependencyPropertyKey EffectiveModePropertyKey = DependencyProperty.RegisterReadOnly(nameof(EffectiveMode), typeof(DiffViewMode), typeof(DiffView), new PropertyMetadata(DiffViewMode.SideBySide));
    public static readonly DependencyProperty EffectiveModeProperty = EffectiveModePropertyKey.DependencyProperty;
    public static readonly DependencyProperty ShowWholeFileProperty = DependencyProperty.Register(nameof(ShowWholeFile), typeof(bool), typeof(DiffView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((DiffView)d).Rebuild(scrollToFirst: true)));
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(object), typeof(DiffView), new PropertyMetadata(null, (d, _) => ((DiffView)d).Adapt()));
    public static readonly DependencyProperty OldLabelProperty = DependencyProperty.Register(nameof(OldLabel), typeof(string), typeof(DiffView), new PropertyMetadata("Before", (d, _) => ((DiffView)d).UpdateLabels()));
    public static readonly DependencyProperty NewLabelProperty = DependencyProperty.Register(nameof(NewLabel), typeof(string), typeof(DiffView), new PropertyMetadata("After", (d, _) => ((DiffView)d).UpdateLabels()));

    public string? Before { get => (string?)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public string? After { get => (string?)GetValue(AfterProperty); set => SetValue(AfterProperty, value); }
    /// <summary>Selects syntax highlighting by extension.</summary>
    public string? FilePath { get => (string?)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    /// <summary>Shown instead of a diff, for example for binary files.</summary>
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    /// <summary>The presentation asked for. A view too narrow for two columns keeps it for when it widens.</summary>
    public DiffViewMode Mode { get => (DiffViewMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    /// <summary>The presentation the toolbar shows: <see cref="Mode"/>, or inline when there is no room for a split.</summary>
    public DiffViewMode EffectiveMode => (DiffViewMode)GetValue(EffectiveModeProperty);
    public bool ShowWholeFile { get => (bool)GetValue(ShowWholeFileProperty); set => SetValue(ShowWholeFileProperty, value); }
    /// <summary>Content at the left of the toolbar, typically the file name.</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string OldLabel { get => (string)GetValue(OldLabelProperty); set => SetValue(OldLabelProperty, value); }
    public string NewLabel { get => (string)GetValue(NewLabelProperty); set => SetValue(NewLabelProperty, value); }

    public TextDiff? Diff => _diff;
    public DiffLayout? Layout => _layout;
    internal TextEditor LeftEditor => _left;
    internal TextEditor RightEditor => _right;
    internal TextEditor InlineEditor => _inline;
    /// <summary>True when the split view shows both sides; a new or deleted file is shown in one column.</summary>
    public bool ShowsSplit { get; private set; }
    public int CurrentChange => _current;

    private readonly TextEditor _left, _right, _inline;
    private readonly DiffOverviewRuler _splitRuler = new(), _inlineRuler = new();
    private readonly HashSet<int> _expanded = [];
    private IReadOnlyList<DiffDisplayLine> _leftLines = [], _rightLines = [], _inlineLines = [];
    private TextDiff? _diff;
    private DiffLayout? _layout;
    private int _current = -1;
    private int _computeVersion;
    private CancellationTokenSource? _compute;
    private bool _syncing;
    private bool _recomputeQueued;
    private bool _compact;
    private bool _roomForSplit = true;

    /// <summary>Below this width labels become icons; below <see cref="StackedWidth"/> the controls move under the header.</summary>
    private const double CompactWidth = 820, StackedWidth = 600;
    /// <summary>Two columns narrower than this are hard to read, so a split view falls back to inline (as VS Code does).</summary>
    private const double MinSplitWidth = 720;

    public DiffView()
    {
        InitializeComponent();
        InputBindings.Add(new KeyBinding(NextChangeCommand, Key.F8, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(PreviousChangeCommand, Key.F8, ModifierKeys.Shift));
        string group = "DiffMode" + Guid.NewGuid().ToString("N");
        SideBySideOption.GroupName = group; InlineOption.GroupName = group;
        SideBySideOption.Checked += (_, _) => Choose(DiffViewMode.SideBySide);
        InlineOption.Checked += (_, _) => Choose(DiffViewMode.Inline);
        SizeChanged += (_, args) => { if (args.WidthChanged) Adapt(); };
        _left = CreateEditor(() => _leftLines, DiffNumberColumns.Old);
        _right = CreateEditor(() => _rightLines, DiffNumberColumns.New);
        _inline = CreateEditor(() => _inlineLines, DiffNumberColumns.Both);
        // The right side owns the vertical scroll bar; both sides keep a horizontal bar at the bottom so their rows stay aligned.
        _left.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _left.HorizontalScrollBarVisibility = _right.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
        LeftHost.Child = _left; RightHost.Child = _right; InlineEditorHost.Child = _inline;
        SplitRulerHost.Child = _splitRuler; InlineRulerHost.Child = _inlineRuler;
        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);
        _inline.TextArea.TextView.ScrollOffsetChanged += (_, _) => UpdateRulerViewport();
        _right.TextArea.TextView.VisualLinesChanged += (_, _) => UpdateRulerViewport();
        _inline.TextArea.TextView.VisualLinesChanged += (_, _) => UpdateRulerViewport();
        _splitRuler.Navigate += fraction => ScrollToFraction(_right, fraction);
        _inlineRuler.Navigate += fraction => ScrollToFraction(_inline, fraction);
        Loaded += (_, _) => ApplyHighlighting();
        SetResourceReference(ThemePaletteProperty, Palette.ResourceKey);
        UpdateLabels();
        ModeChanged();
        ShowMessage(EmptyText);
    }

    private IReadOnlyList<int> Changes => _layout is null ? [] : ShowsInline ? _layout.InlineChanges : _layout.SideBySideChanges;
    private bool ShowsInline => !ShowsSplit;

    private TextEditor CreateEditor(Func<IReadOnlyList<DiffDisplayLine>> lines, DiffNumberColumns columns)
    {
        var editor = new TextEditor
        {
            IsReadOnly = true, ShowLineNumbers = false, WordWrap = false, FontSize = 12.5,
            FontFamily = new FontFamily("Cascadia Code, Cascadia Mono, Consolas"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 2, 8, 2), Document = new TextDocument()
        };
        editor.SetResourceReference(BackgroundProperty, "EditorBrush");
        editor.SetResourceReference(ForegroundProperty, "TextBrush");
        editor.Options.EnableHyperlinks = false; editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = false; editor.Options.EnableRectangularSelection = false;
        editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "EditorSelectionBrush");
        editor.TextArea.SelectionBorder = null; editor.TextArea.SelectionForeground = null; editor.TextArea.SelectionCornerRadius = 2;
        editor.TextArea.Caret.CaretBrush = Brushes.Transparent;
        var margin = new DiffLineNumberMargin(editor, lines, columns);
        margin.SeparatorClicked += Expand;
        editor.TextArea.LeftMargins.Insert(0, margin);
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(editor, lines));
        editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (LineAt(editor, lines(), e.GetPosition(editor.TextArea.TextView).Y) is { Kind: DiffLineKind.Separator } separator)
            {
                Expand(separator);
                e.Handled = true;
            }
        };
        editor.MouseMove += (_, e) => editor.TextArea.TextView.Cursor =
            LineAt(editor, lines(), e.GetPosition(editor.TextArea.TextView).Y) is { Kind: DiffLineKind.Separator } ? Cursors.Hand : Cursors.IBeam;
        return editor;
    }

    private static DiffDisplayLine? LineAt(TextEditor editor, IReadOnlyList<DiffDisplayLine> lines, double y)
    {
        var view = editor.TextArea.TextView;
        if (!view.VisualLinesValid) return null;
        int? number = view.GetVisualLineFromVisualTop(y + view.VerticalOffset)?.FirstDocumentLine.LineNumber;
        return number is { } n && n >= 1 && n - 1 < lines.Count ? lines[n - 1] : null;
    }

    private static void ContentChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((DiffView)owner).QueueRecompute();

    /// <summary>A new diff source usually changes Before, After and Message together; compare once after all three arrive.</summary>
    private void QueueRecompute()
    {
        if (_recomputeQueued) return;
        _recomputeQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            _recomputeQueued = false;
            Recompute();
        });
    }

    /// <summary>Computes the diff; large inputs are diffed off the UI thread.</summary>
    private void Recompute()
    {
        var version = ++_computeVersion;
        _compute?.Cancel(); _compute?.Dispose(); _compute = null;
        _expanded.Clear();
        _current = -1;
        if (!string.IsNullOrEmpty(Message)) { Clear(); ShowMessage(Message!, "Info"); return; }
        if (Before is null && After is null) { Clear(); ShowMessage(EmptyText); return; }
        string before = Before ?? "", after = After ?? "";
        if (before.Length + after.Length < 400_000) { Apply(TextDiff.Compute(before, after)); return; }
        ShowMessage("Comparing…");
        _compute = new CancellationTokenSource();
        CompareInBackgroundAsync(before, after, version, _compute.Token).Forget();
    }

    private async Task CompareInBackgroundAsync(string before, string after, int version, CancellationToken token)
    {
        try
        {
            var diff = await Task.Run(() => TextDiff.Compute(before, after, cancellationToken: token), token);
            if (version == _computeVersion) Apply(diff);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // A failure while laying out the result lands here too; without a message the view would stay as it was.
        catch (Exception exception) when (version == _computeVersion) { Clear(); ShowMessage(exception.Message, "Warning"); }
    }

    private void Apply(TextDiff diff)
    {
        _diff = diff;
        Rebuild(scrollToFirst: true);
    }

    private void Clear()
    {
        _diff = null; _layout = null;
        _leftLines = _rightLines = _inlineLines = [];
        foreach (var editor in new[] { _left, _right, _inline }) editor.Document = new TextDocument();
        AddedText.Text = RemovedText.Text = ChangeText.Text = "";
        NotifyNavigation();
    }

    private void Rebuild(bool scrollToFirst)
    {
        if (_diff is not { } diff) return;
        _layout = DiffLayout.Build(diff, ShowWholeFile, _expanded);
        _leftLines = _layout.Left; _rightLines = _layout.Right; _inlineLines = _layout.Inline;
        // A new or deleted file has only one side worth showing.
        ShowsSplit = EffectiveMode == DiffViewMode.SideBySide && diff.OldLineCount > 0 && diff.NewLineCount > 0;
        if (ShowsSplit)
        {
            SetText(_left, _leftLines);
            SetText(_right, _rightLines);
            _splitRuler.SetLines(Marks(_leftLines, _rightLines));
        }
        else
        {
            SetText(_inline, _inlineLines);
            _inlineRuler.SetLines(_inlineLines.Select(line => line.Kind switch { DiffLineKind.Removed => DiffMark.Removed, DiffLineKind.Added => DiffMark.Added, _ => DiffMark.None }).ToArray());
        }
        SplitHost.Visibility = ShowsSplit ? Visibility.Visible : Visibility.Collapsed;
        InlineHost.Visibility = ShowsSplit ? Visibility.Collapsed : Visibility.Visible;
        MessagePanel.Visibility = Visibility.Collapsed;
        AddedText.Text = diff.AddedLines > 0 ? $"+{diff.AddedLines}" : "";
        RemovedText.Text = diff.RemovedLines > 0 ? $"−{diff.RemovedLines}" : "";
        UpdateLabels();
        if (diff.OldLineCount == 0 && diff.NewLineCount == 0) ShowMessage("Both versions are empty.", "Info");
        if (_current >= Changes.Count) _current = Changes.Count - 1;
        if (scrollToFirst)
        {
            _current = Changes.Count > 0 ? 0 : -1;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_current >= 0) GoToChange(_current); else ScrollTop(); });
        }
        NotifyNavigation();
    }

    private static DiffMark[] Marks(IReadOnlyList<DiffDisplayLine> left, IReadOnlyList<DiffDisplayLine> right)
    {
        var marks = new DiffMark[left.Count];
        for (int i = 0; i < marks.Length; i++)
            marks[i] = (left[i].Kind, right[i].Kind) switch
            {
                (DiffLineKind.Removed, DiffLineKind.Added) => DiffMark.Modified,
                (DiffLineKind.Removed, _) => DiffMark.Removed,
                (_, DiffLineKind.Added) => DiffMark.Added,
                _ => DiffMark.None
            };
        return marks;
    }

    private static void SetText(TextEditor editor, IReadOnlyList<DiffDisplayLine> lines)
    {
        var text = new StringBuilder();
        for (int i = 0; i < lines.Count; i++) { if (i > 0) text.Append('\n'); text.Append(lines[i].Text); }
        var highlighting = editor.SyntaxHighlighting;
        editor.Document = new TextDocument(text.ToString());
        editor.SyntaxHighlighting = highlighting;
        foreach (var margin in editor.TextArea.LeftMargins.OfType<DiffLineNumberMargin>()) margin.Refresh();
    }

    private void Expand(DiffDisplayLine separator)
    {
        if (separator.GapStart < 0) return;
        var editor = ShowsSplit ? _right : _inline;
        double offset = editor.VerticalOffset;
        _expanded.Add(separator.GapStart);
        Rebuild(scrollToFirst: false);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => editor.ScrollToVerticalOffset(offset));
    }

    private bool CanGoToNextChange() => Changes.Count > 0 && _current < Changes.Count - 1;
    [RelayCommand(CanExecute = nameof(CanGoToNextChange))] private void NextChange() => GoToChange(_current + 1);
    private bool CanGoToPreviousChange() => Changes.Count > 0 && _current > 0;
    [RelayCommand(CanExecute = nameof(CanGoToPreviousChange))] private void PreviousChange() => GoToChange(_current - 1);

    private void GoToChange(int index)
    {
        var changes = Changes;
        if (changes.Count == 0) return;
        _current = Math.Clamp(index, 0, changes.Count - 1);
        var editor = ShowsSplit ? _right : _inline;
        editor.UpdateLayout();
        var view = editor.TextArea.TextView;
        int line = Math.Min(changes[_current] + 1, editor.Document.LineCount);
        double top = view.GetVisualTopByDocumentLine(line);
        editor.ScrollToVerticalOffset(Math.Max(0, top - view.DefaultLineHeight * 3));
        NotifyNavigation();
    }

    private void ScrollTop()
    {
        foreach (var editor in new[] { _left, _right, _inline }) { editor.ScrollToVerticalOffset(0); editor.ScrollToHorizontalOffset(0); }
    }

    private void ScrollToFraction(TextEditor editor, double fraction)
    {
        var view = editor.TextArea.TextView;
        double height = view.DocumentHeight, viewport = view.ActualHeight;
        editor.ScrollToVerticalOffset(Math.Max(0, fraction * height - viewport / 2));
    }

    private void Sync(TextEditor source, TextEditor target)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            // Read and write the text views directly. The editors' ScrollViewer offsets lag one layout pass behind,
            // and ScrollToVerticalOffset is deferred until then: copying a stale position and echoing it back later
            // pulled the scrolled side backwards while the wheel was still turning.
            IScrollInfo from = source.TextArea.TextView, to = target.TextArea.TextView;
            if (Math.Abs(to.VerticalOffset - from.VerticalOffset) > 0.5) to.SetVerticalOffset(from.VerticalOffset);
            if (Math.Abs(to.HorizontalOffset - from.HorizontalOffset) > 0.5) to.SetHorizontalOffset(from.HorizontalOffset);
            to.ScrollOwner?.InvalidateScrollInfo();
        }
        finally { _syncing = false; }
        UpdateRulerViewport();
    }

    private void UpdateRulerViewport()
    {
        foreach (var (editor, ruler) in new[] { (_right, _splitRuler), (_inline, _inlineRuler) })
        {
            var view = editor.TextArea.TextView;
            double height = view.DocumentHeight;
            if (height <= 0 || view.ActualHeight <= 0) continue;
            // A short file fills only part of the ruler, so its marks line up with the rows they describe.
            ruler.SetViewport(view.VerticalOffset / height, Math.Min(1, view.ActualHeight / height), Math.Min(1, height / view.ActualHeight));
        }
    }

    private void NotifyNavigation()
    {
        var count = Changes.Count;
        ChangeText.Text = count == 0 ? (_diff is null ? "" : _compact ? "None" : "No text changes")
            : _compact ? $"{Math.Max(1, _current + 1)}/{count}"
            : $"{Math.Max(1, _current + 1)} of {count} change{(count == 1 ? "" : "s")}";
        NextChangeCommand.NotifyCanExecuteChanged();
        PreviousChangeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Fits the toolbar and the presentation to the available width.</summary>
    private void Adapt()
    {
        double width = ActualWidth;
        bool compact = width < CompactWidth, stacked = width < StackedWidth && Header is not null;
        WholeFileToggle.Content = compact ? null : "Whole file";
        SideBySideOption.Content = compact ? null : "Split";
        InlineOption.Content = compact ? null : "Inline";
        Grid.SetRow(ToolbarControls, stacked ? 1 : 0);
        Grid.SetColumn(ToolbarControls, stacked ? 0 : 1);
        Grid.SetColumnSpan(ToolbarControls, stacked ? 2 : 1);
        Grid.SetColumnSpan(HeaderPresenter, stacked ? 2 : 1);
        ToolbarControls.Margin = new Thickness(0, stacked ? 4 : 0, 0, 0);
        if (compact != _compact) { _compact = compact; NotifyNavigation(); }
        bool room = width <= 0 || width >= MinSplitWidth;
        if (room != _roomForSplit) { _roomForSplit = room; ModeChanged(); }
    }

    // The options follow EffectiveMode, so an option checked to match it says nothing about the mode asked for.
    private void Choose(DiffViewMode mode) { if (mode != EffectiveMode) Mode = mode; }

    private void ModeChanged()
    {
        SetValue(EffectiveModePropertyKey, _roomForSplit ? Mode : DiffViewMode.Inline);
        SideBySideOption.IsEnabled = _roomForSplit;
        SideBySideOption.ToolTip = _roomForSplit ? "Side by side" : "Side by side needs a wider view, so changes are shown inline";
        if (_diff is not null)
        {
            // Keep the reader near the same change when switching presentations.
            int current = _current;
            Rebuild(scrollToFirst: false);
            if (current >= 0) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => GoToChange(current));
        }
    }

    private void UpdateLabels()
    {
        OldLabelText.Text = OldLabel;
        NewLabelText.Text = NewLabel;
        InlineLabelText.Text = _diff is { OldLineCount: 0, NewLineCount: > 0 } ? $"New file · {NewLabel}"
            : _diff is { NewLineCount: 0, OldLineCount: > 0 } ? $"Deleted · {OldLabel}"
            : $"{OldLabel}  →  {NewLabel}";
    }

    private void ShowMessage(string text, string glyph = "Diff")
    {
        MessageText.Text = text;
        MessageGlyph.Kind = glyph;
        MessagePanel.Visibility = Visibility.Visible;
        SplitHost.Visibility = Visibility.Collapsed;
        InlineHost.Visibility = Visibility.Collapsed;
    }

    private void ApplyHighlighting()
    {
        var definition = SyntaxHighlightingSelector.For(FilePath);
        foreach (var editor in new[] { _left, _right, _inline }) editor.SyntaxHighlighting = definition;
    }

    // Follows the application resource, so a closed view holds no subscription on any service.
    private static readonly DependencyProperty ThemePaletteProperty = DependencyProperty.Register("ThemePalette", typeof(Palette), typeof(DiffView),
        new PropertyMetadata(null, (view, _) => ((DiffView)view).ThemeApplied()));

    private void ThemeApplied()
    {
        foreach (var editor in new[] { _left, _right, _inline })
        {
            editor.TextArea.TextView.Redraw();
            foreach (var margin in editor.TextArea.LeftMargins.OfType<DiffLineNumberMargin>()) margin.Refresh();
        }
        _splitRuler.InvalidateVisual(); _inlineRuler.InvalidateVisual();
    }
}
