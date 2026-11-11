using WpfStudio.App.Features.Designer;
using WpfStudio.Contracts.Diagnostics;

namespace WpfStudio.Shell.Tests;

public sealed partial class DesignerTests
{
    [Fact]
    public async Task ASourceEditIsTimedAsPartOfTheKeystrokeThatMadeIt()
    {
        var perf = new PerfMonitor();
        await using var model = new DesignerViewModel(new FakePreview(), new InlineDispatcher(), perf) { AutoRefresh = false };
        var document = Document("view.xaml");
        await model.OpenAsync(document);
        Assert.DoesNotContain(perf.Snapshot(), stat => stat.Area == PerfAreas.Edit);
        document.Content += " ";
        document.Content += " ";
        var edit = Assert.Single(perf.Snapshot(), stat => stat.Area == PerfAreas.Edit);
        Assert.Equal(("text changed: designer invalidation", 2L), (edit.Name, edit.Count));
    }
}
