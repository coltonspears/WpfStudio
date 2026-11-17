using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfStudio.App.Features.DevOps;

/// <summary>Git Credential Manager sign-in and the personal access token box. The token passes straight to the command and the box is cleared.</summary>
public partial class DevOpsSignInView : UserControl
{
    public DevOpsSignInView() => InitializeComponent();

    private void SignInWithToken(object sender, RoutedEventArgs e) => Submit();

    private void TokenKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Submit();
    }

    private void Submit()
    {
        if (DataContext is not DevOpsViewModel model || TokenBox.Password.Length == 0) return;
        var token = TokenBox.Password;
        TokenBox.Clear();
        if (model.SignInWithTokenCommand.CanExecute(token)) model.SignInWithTokenCommand.Execute(token);
    }
}
