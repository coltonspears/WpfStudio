using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfStudio.App.Controls;

/// <summary>
/// A text box for a number, with spin buttons in its template. It takes digits only; Up and Down step the value, Page Up
/// and Page Down step it ten times, and so does the mouse wheel while the box has focus. What was typed is committed on
/// Enter, when the box loses focus and before every step, clamped to <see cref="Minimum"/> and <see cref="Maximum"/>;
/// text that is not a number is replaced by the last value. Bind <see cref="Value"/>, not <c>Text</c>.
/// </summary>
public sealed class NumberBox : TextBox
{
    public static readonly RoutedCommand IncreaseCommand = new(nameof(IncreaseCommand), typeof(NumberBox));
    public static readonly RoutedCommand DecreaseCommand = new(nameof(DecreaseCommand), typeof(NumberBox));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ValueChanged, CoerceValue));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata(0d, RangeChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata((double)int.MaxValue, RangeChanged));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata(1d));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(0, RangeChanged), static value => value is >= 0 and <= 6);

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>What one press of Up, Down or a spin button adds or takes away.</summary>
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    /// <summary>Digits after the decimal point; 0 for a whole number.</summary>
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

    static NumberBox()
    {
        CommandManager.RegisterClassCommandBinding(typeof(NumberBox), new CommandBinding(IncreaseCommand, (sender, _) => ((NumberBox)sender).StepBy(1), CanStep));
        CommandManager.RegisterClassCommandBinding(typeof(NumberBox), new CommandBinding(DecreaseCommand, (sender, _) => ((NumberBox)sender).StepBy(-1), CanStep));
    }

    private static void CanStep(object sender, CanExecuteRoutedEventArgs args) => args.CanExecute = sender is NumberBox { IsReadOnly: false, IsEnabled: true };

    private static object CoerceValue(DependencyObject owner, object? value)
    {
        var box = (NumberBox)owner;
        if (value is not double number || !double.IsFinite(number)) return box.Minimum;
        return Math.Round(Math.Clamp(number, box.Minimum, Math.Max(box.Minimum, box.Maximum)), box.Decimals, MidpointRounding.AwayFromZero);
    }
    private static void ValueChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((NumberBox)owner).ShowValue();
    private static void RangeChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var box = (NumberBox)owner;
        box.CoerceValue(ValueProperty);
        box.ShowValue();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        ShowValue();
    }

    private string Format(double value) => value.ToString("N" + Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);

    private void ShowValue()
    {
        var text = Format(Value);
        if (Text != text) SetCurrentValue(TextProperty, text);
    }

    /// <summary>Takes what was typed as the value, or puts the value back when it is not a number.</summary>
    public void Commit()
    {
        if (double.TryParse(Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var typed)
            || double.TryParse(Text, NumberStyles.Number, CultureInfo.InvariantCulture, out typed))
            SetCurrentValue(ValueProperty, typed);
        ShowValue();
    }

    private void StepBy(double steps)
    {
        Commit();
        SetCurrentValue(ValueProperty, Value + steps * Step);
        if (IsKeyboardFocused) SelectAll();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || IsReadOnly) return;
        switch (e.Key)
        {
            case Key.Up: StepBy(1); e.Handled = true; break;
            case Key.Down: StepBy(-1); e.Handled = true; break;
            case Key.PageUp: StepBy(10); e.Handled = true; break;
            case Key.PageDown: StepBy(-10); e.Handled = true; break;
            // Not handled: a default button or a key binding on Enter still runs, and sees the committed value.
            case Key.Enter: Commit(); break;
        }
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        var format = CultureInfo.CurrentCulture.NumberFormat;
        foreach (var c in e.Text)
        {
            var allowed = char.IsAsciiDigit(c)
                || format.NumberGroupSeparator.Contains(c, StringComparison.Ordinal)
                || (Decimals > 0 && (format.NumberDecimalSeparator.Contains(c, StringComparison.Ordinal) || c == '.'))
                || (Minimum < 0 && format.NegativeSign.Contains(c, StringComparison.Ordinal));
            if (!allowed) { e.Handled = true; return; }
        }
    }

    // On the way down: the text host's scroll viewer marks the wheel handled before it would bubble up here.
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        // Only while focused, so scrolling a form past the box does not change it.
        if (e.Handled || IsReadOnly || !IsKeyboardFocusWithin) return;
        StepBy(Math.Sign(e.Delta));
        e.Handled = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Commit();
        base.OnLostKeyboardFocus(e);
    }
}
