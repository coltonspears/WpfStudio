using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.App.Features.Debugging;

public partial class BreakpointSettingsOverlay : UserControl
{
    public BreakpointSettingsOverlay() => InitializeComponent();

    /// <summary>
    /// Puts a token at the caret of the message, which is where only the view knows: a keyword as it is, and braces
    /// around the selection, or empty braces with the caret between them.
    /// </summary>
    private void InsertToken(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TracepointToken token }) return;
        var box = MessageText;
        // Never focused yet, the caret is at the start; a token belongs after what is already written.
        if (!box.IsKeyboardFocused && box.SelectionLength == 0) box.CaretIndex = box.Text.Length;
        if (token.Text == "{}")
        {
            var selected = box.SelectedText;
            var start = box.SelectionStart;
            box.SelectedText = "{" + selected + "}";
            box.Select(selected.Length == 0 ? start + 1 : start + selected.Length + 2, 0);
        }
        else
        {
            var start = box.SelectionStart;
            box.SelectedText = token.Text;
            box.Select(start + token.Text.Length, 0);
        }
        box.Focus();
    }
}
