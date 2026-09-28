using System.ComponentModel;
using System.Windows.Controls;

namespace WpfStudio.PreviewFixture;

public interface IPreviewOrderService { ScenarioData CreateData(); }
public sealed class PreviewOrderService : IPreviewOrderService
{
    public ScenarioData CreateData() => new("Constructor dependency", 2);
}
public sealed record ScenarioData(string Title, int Count);

public partial class DependencyView : UserControl
{
    public DependencyView(IPreviewOrderService service)
    {
        if (!DesignerProperties.GetIsInDesignMode(this)) throw new InvalidOperationException("Scenario view must be constructed in design mode.");
        InitializeComponent();
        DataContext = service.CreateData();
    }
}
