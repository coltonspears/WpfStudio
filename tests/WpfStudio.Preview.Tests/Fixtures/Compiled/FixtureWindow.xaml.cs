using System.Windows;

namespace WpfStudio.PreviewFixture;

public partial class FixtureWindow : Window
{
    public FixtureWindow() { InitializeComponent(); Title = "Compiled window ancestor"; }
}
