using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.PreviewFixture;

public partial class BindingSourceView : UserControl
{
    public BindingSourceView() { InitializeComponent(); DataContext = BindingSourceData.Instance; }
}

public sealed class BindingSourceData
{
    public static readonly BindingSourceData Instance = new();
    public string Primary => "Primary";
    public string Secondary => "Secondary";
}

public static class BindingSourceOptions
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached("Value", typeof(object), typeof(BindingSourceOptions));
    public static object GetValue(DependencyObject target) => target.GetValue(ValueProperty);
    public static void SetValue(DependencyObject target, object value) => target.SetValue(ValueProperty, value);
}

public sealed class BindingSourceMutationControl : TextBlock
{
    static BindingSourceMutationControl() => TagProperty.OverrideMetadata(typeof(BindingSourceMutationControl),
        new FrameworkPropertyMetadata(null, (target, args) =>
        {
            if (args.NewValue is "replace") ((BindingSourceMutationControl)target).SetBinding(TextProperty, new Binding("Primary"));
        }));

    public BindingSourceMutationControl() => Loaded += (_, _) =>
    {
        if (Tag is "replace-on-load") SetBinding(TextProperty, new Binding("Primary"));
    };

}

public sealed class BindingSourceRemovalPanel : StackPanel
{
    static BindingSourceRemovalPanel() => TagProperty.OverrideMetadata(typeof(BindingSourceRemovalPanel),
        new FrameworkPropertyMetadata(null, (target, args) =>
        {
            if (args.NewValue is "remove") ((BindingSourceRemovalPanel)target).Children.Clear();
        }));
}
