using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Inspection;
using WpfStudio.App.ViewModels;
using WpfStudio.Core.Documents;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

/// <summary>A request has one owner: a second one is refused, where a second event subscriber used to replace the first's result without a sign.</summary>
public sealed class RequestHostTests
{
    [Fact]
    public async Task InspectionRefusesASecondHost()
    {
        await using var model = new InspectionViewModel(new InlineDispatcher());
        model.AttachHost(new InspectionHostStub());
        Assert.Throws<InvalidOperationException>(() => model.AttachHost(new InspectionHostStub()));
    }

    [Fact]
    public async Task TheDesignerRefusesASecondHost()
    {
        await using var model = new DesignerViewModel(new WpfStudio.Runtime.Design.PreviewClient(), new InlineDispatcher());
        model.AttachSourceHost(new DesignerHostStub());
        Assert.Throws<InvalidOperationException>(() => model.AttachSourceHost(new DesignerHostStub()));
    }

    [Fact]
    public async Task TheShellIsTheHostOfItsDesignerAndInspection()
    {
        await using var context = new ShellTestContext();
        Assert.Throws<InvalidOperationException>(() => context.Shell.LiveInspection.AttachHost(new InspectionHostStub()));
        Assert.Throws<InvalidOperationException>(() => context.Shell.Designer.AttachSourceHost(new DesignerHostStub()));
    }

    [Fact]
    public async Task LaunchCommandsReachTheHostAndDoNothingWithoutOne()
    {
        await using var model = new InspectionViewModel(new InlineDispatcher());
        await model.RunCommand.ExecuteAsync(null);
        var launches = new List<bool>();
        model.StubHost().Launch = debug => { launches.Add(debug); return Task.CompletedTask; };
        await model.RunCommand.ExecuteAsync(null);
        await model.DebugCommand.ExecuteAsync(null);
        Assert.Equal([false, true], launches);
    }

    [Fact]
    public async Task TheShellOwnsEachEditorsCodeActions()
    {
        await using var context = new ShellTestContext();
        var path = await context.CreateFileAsync("Quick.cs", "class C { }");
        await context.Shell.OpenDocumentAsync(path);
        var editor = context.Shell.ActiveDocument!;
        Assert.NotNull(editor.XamlCodeActionApplier);
        Assert.NotNull(editor.CSharpCodeActionApplier);
    }
}
