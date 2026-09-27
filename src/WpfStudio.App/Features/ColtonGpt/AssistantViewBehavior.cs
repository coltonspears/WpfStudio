using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.App.Features.ColtonGpt;

/// <summary>View-only password binding and output following, kept outside view code-behind.</summary>
public static class AssistantViewBehavior
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached("Password", typeof(string), typeof(AssistantViewBehavior),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, PasswordChanged));
    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(AssistantViewBehavior), new PropertyMetadata(false));
    public static string GetPassword(DependencyObject value) => (string?)value.GetValue(PasswordProperty) ?? "";
    public static void SetPassword(DependencyObject value, string password) => value.SetValue(PasswordProperty, password);
    private static void PasswordChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not PasswordBox box) return;
        box.PasswordChanged -= BoxPasswordChanged;
        if (!(bool)box.GetValue(UpdatingProperty) && box.Password != (string?)args.NewValue) box.Password = (string?)args.NewValue ?? "";
        box.PasswordChanged += BoxPasswordChanged;
    }
    private static void BoxPasswordChanged(object sender, RoutedEventArgs args)
    {
        var box = (PasswordBox)sender;
        box.SetValue(UpdatingProperty, true);
        box.SetCurrentValue(PasswordProperty, box.Password);
        box.SetValue(UpdatingProperty, false);
    }

    public static readonly DependencyProperty FollowOutputProperty = DependencyProperty.RegisterAttached("FollowOutput", typeof(bool), typeof(AssistantViewBehavior), new PropertyMetadata(false, FollowChanged));
    private static readonly DependencyProperty WasAtBottomProperty = DependencyProperty.RegisterAttached("WasAtBottom", typeof(bool), typeof(AssistantViewBehavior), new PropertyMetadata(true));
    public static bool GetFollowOutput(DependencyObject value) => (bool)value.GetValue(FollowOutputProperty);
    public static void SetFollowOutput(DependencyObject value, bool enabled) => value.SetValue(FollowOutputProperty, enabled);
    private static void FollowChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not ScrollViewer viewer) return;
        viewer.ScrollChanged -= ScrollChanged;
        if ((bool)args.NewValue) viewer.ScrollChanged += ScrollChanged;
    }
    private static void ScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        var viewer = (ScrollViewer)sender;
        if (!ReferenceEquals(args.OriginalSource, viewer)) return;
        if (args.ExtentHeightChange == 0) viewer.SetValue(WasAtBottomProperty, viewer.VerticalOffset >= viewer.ScrollableHeight - 4);
        else if ((bool)viewer.GetValue(WasAtBottomProperty)) viewer.ScrollToEnd();
    }
}
