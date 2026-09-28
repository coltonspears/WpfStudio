using System.Windows.Controls;

namespace WpfStudio.PreviewFixture;

public partial class FixturePage : Page
{
    public FixturePage() { InitializeComponent(); Message.Text = "Compiled page constructor"; }
}
