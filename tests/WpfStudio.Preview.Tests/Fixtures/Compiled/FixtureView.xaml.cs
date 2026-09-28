using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.PreviewFixture;

public partial class FixtureView : UserControl
{
    public FixtureView()
    {
        InitializeComponent();
        Message.Text = DesignerProperties.GetIsInDesignMode(this) ? "Constructor in design mode" : "Unexpected runtime mode";
    }
    private void OnLoaded(object sender, RoutedEventArgs e) => Message.Text += " / Loaded handler";
}
