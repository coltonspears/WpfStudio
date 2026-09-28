using System.Windows;

namespace WpfStudio.PreviewFixture;

public partial class App : Application
{
    public App() => throw new InvalidOperationException("Project App constructor must never run in a compiled preview.");
    private void StartupTrap(object sender, StartupEventArgs e) => throw new InvalidOperationException("Project startup must never run in a compiled preview.");
}
