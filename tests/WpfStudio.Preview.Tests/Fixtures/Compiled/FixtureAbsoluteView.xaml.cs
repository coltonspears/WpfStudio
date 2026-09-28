using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace WpfStudio.PreviewFixture;

public partial class FixtureAbsoluteView : UserControl
{
    public FixtureAbsoluteView()
    {
        if (Assembly.GetEntryAssembly() != GetType().Assembly || Application.ResourceAssembly != GetType().Assembly)
            throw new InvalidOperationException("The compiled view must own the entry and application resource assembly identities.");
        InitializeComponent();
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Theme/PackProbe.txt")).Stream;
        using var reader = new StreamReader(resource);
        Message.Text = reader.ReadToEnd().Trim();
    }
}
