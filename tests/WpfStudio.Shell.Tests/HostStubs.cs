using System.Runtime.CompilerServices;
using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Shell.Tests;

/// <summary>Answers a view model's requests with whatever the test assigned; the first use attaches it as the one host.</summary>
internal sealed class InspectionHostStub : IInspectionRequestHost
{
    public Func<bool, Task>? Launch { get; set; }
    public Func<InspectionSourceRequest, CancellationToken, Task<string>>? Source { get; set; }
    public Func<InspectionBindingSourceNavigation, CancellationToken, Task<string>>? BindingSource { get; set; }
    public Func<InspectionSourceEditRequest, CancellationToken, Task<InspectionSourceEditOutcome>>? SourceEdit { get; set; }
    public Task LaunchAsync(bool debug) => (Launch ?? throw new InvalidOperationException("Unexpected launch request."))(debug);
    public Task<string> NavigateSourceAsync(InspectionSourceRequest request, CancellationToken cancellationToken) =>
        (Source ?? throw new InvalidOperationException("Unexpected source request."))(request, cancellationToken);
    public Task<string> NavigateBindingSourceAsync(InspectionBindingSourceNavigation navigation, CancellationToken cancellationToken) =>
        (BindingSource ?? throw new InvalidOperationException("Unexpected binding source request."))(navigation, cancellationToken);
    public Task<InspectionSourceEditOutcome> EditSourceAsync(InspectionSourceEditRequest request, CancellationToken cancellationToken) =>
        (SourceEdit ?? throw new InvalidOperationException("Unexpected source edit request."))(request, cancellationToken);
}

internal sealed class DesignerHostStub : IDesignerSourceHost
{
    public Func<XamlPropertyEditResult, Task<bool>>? PropertyEdit { get; set; }
    public Func<DesignerLayoutSourceEdit, Task<bool>>? LayoutEdit { get; set; }
    public Func<PreviewBindingSourceNavigation, CancellationToken, Task<string>>? BindingSource { get; set; }
    public Action<SourceLocation>? Revealed { get; set; }
    public void RevealSelection(SourceLocation source) => Revealed?.Invoke(source);
    public Task<bool> ReviewPropertyEditAsync(XamlPropertyEditResult proposal) =>
        (PropertyEdit ?? throw new InvalidOperationException("Unexpected property edit."))(proposal);
    public Task<bool> ReviewLayoutEditAsync(DesignerLayoutSourceEdit edit) =>
        (LayoutEdit ?? throw new InvalidOperationException("Unexpected layout edit."))(edit);
    public Task<string> NavigateBindingSourceAsync(PreviewBindingSourceNavigation navigation, CancellationToken cancellationToken) =>
        (BindingSource ?? throw new InvalidOperationException("Unexpected binding source request."))(navigation, cancellationToken);
}

internal static class HostStubs
{
    private static readonly ConditionalWeakTable<InspectionViewModel, InspectionHostStub> InspectionHosts = new();
    private static readonly ConditionalWeakTable<DesignerViewModel, DesignerHostStub> DesignerHosts = new();
    public static InspectionHostStub StubHost(this InspectionViewModel model) =>
        InspectionHosts.GetValue(model, owner => { var stub = new InspectionHostStub(); owner.AttachHost(stub); return stub; });
    public static DesignerHostStub StubHost(this DesignerViewModel model) =>
        DesignerHosts.GetValue(model, owner => { var stub = new DesignerHostStub(); owner.AttachSourceHost(stub); return stub; });
}
