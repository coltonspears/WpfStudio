using WpfStudio.App.Features.Inspection;
using WpfStudio.Inspection.Protocol;

namespace WpfStudio.Shell.Tests;

public sealed class InspectionSourcePropertyMappingTests
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly Guid FrameworkMvid = new("cd3bde67-909d-4f7f-bfcf-c3ca2d712015");
    private static readonly Guid AppMvid = new("a5ea35f0-7242-473e-90ea-39a6ed7f88d4");
    private static readonly Guid LibraryMvid = new("0f58c5bd-52b5-496d-bc22-63e2be48f7fd");
    private static readonly InspectionSourcePropertyTarget Window = new("System.Windows.Window", "PresentationFramework", FrameworkMvid, [Presentation], "Content");
    private static readonly InspectionSourcePropertyTarget MainWindow = new("Demo.MainWindow", "Demo", AppMvid, [], "Content");
    private static readonly InspectionSourcePropertyTarget Widget = new("Controls.Widget", "ControlsLibrary", LibraryMvid, ["urn:controls", "https://example.test/controls"], "Children");

    [Fact]
    public void RootWindowUsesTheAuthoredBaseTypeInsteadOfItsRuntimeSubclass()
    {
        var property = Identity([MainWindow, Window]);
        var result = Match(property, "Window", Presentation);
        Assert.Same(Window, result);
        Assert.Equal("Content", result!.ContentProperty);
        Assert.Null(Match(property with { AuthoredTargets = [MainWindow] }, "Window", Presentation));
    }

    [Theory]
    [InlineData("Window", Presentation, true)]
    [InlineData("Window", "urn:unrelated", false)]
    [InlineData("window", Presentation, false)]
    [InlineData("w:Window", Presentation, false)]
    [InlineData("Button", Presentation, false)]
    [InlineData(null, Presentation, false)]
    [InlineData("Window", null, false)]
    public void LocalTypeNameAndResolvedNamespaceMustBothMatch(string? localType, string? uri, bool expected)
    {
        Assert.Equal(expected, Match(Identity([Window, MainWindow]), localType, uri) is not null);
    }

    [Fact]
    public void InheritedFrameworkPropertyCanUseItsVerifiedAuthoredTargetAndContentMetadata()
    {
        var button = new InspectionSourcePropertyTarget("System.Windows.Controls.Button", "PresentationFramework", FrameworkMvid, [Presentation], "Content");
        var property = Identity([button]) with { PropertyName = "Margin", OwnerType = "System.Windows.FrameworkElement" };
        Assert.Same(button, Match(property, "Button", Presentation));
    }

    [Theory]
    [InlineData("clr-namespace:Controls", "ControlsLibrary", true)]
    [InlineData("clr-namespace:Controls", "Demo", false)]
    [InlineData("clr-namespace:Controls;assembly=ControlsLibrary", "Demo", true)]
    [InlineData("clr-namespace:Controls;assembly=controlslibrary", "Demo", true)]
    [InlineData("clr-namespace:Controls;assembly=Demo", "ControlsLibrary", false)]
    [InlineData("clr-namespace:Other;assembly=ControlsLibrary", "ControlsLibrary", false)]
    [InlineData("clr-namespace:Controls;assembly=", "ControlsLibrary", false)]
    [InlineData("clr-namespace:Controls;assembly=ControlsLibrary;assembly=Demo", "ControlsLibrary", false)]
    [InlineData("clr-namespace:Controls;other=ControlsLibrary", "ControlsLibrary", false)]
    public void ClrNamespaceResolvesAbsentAssemblyOnlyAgainstTheVerifiedResourceAssembly(string uri, string sourceAssembly, bool expected)
    {
        Assert.Equal(expected, Match(Identity([Widget]), "Widget", uri, sourceAssembly) is not null);
    }

    [Theory]
    [InlineData("urn:controls", true)]
    [InlineData("https://example.test/controls", true)]
    [InlineData("urn:other-controls", false)]
    public void XmlnsDefinitionMetadataSupportsResolvedCustomAliases(string uri, bool expected)
    {
        Assert.Equal(expected, Match(Identity([Widget]), "Widget", uri) is not null);
    }

    [Fact]
    public void MultipleAuthoredCandidatesNeverChooseAnArbitraryType()
    {
        var other = new InspectionSourcePropertyTarget("Other.Widget", "Demo", AppMvid, ["urn:controls"], "Content");
        Assert.Null(Match(Identity([Widget, other]), "Widget", "urn:controls"));
        Assert.Null(Match(Identity([Widget, Widget]), "Widget", "urn:controls"));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("owner")]
    [InlineData("candidate")]
    public void EveryReferencedTypeMustHaveAKnownNonemptyModuleIdentity(string role)
    {
        var identity = Identity([Widget]);
        identity = role switch
        {
            "target" => identity with { TargetModuleVersionId = Guid.Empty },
            "owner" => identity with { OwnerModuleVersionId = Guid.Empty },
            _ => identity with { AuthoredTargets = [Widget with { ModuleVersionId = Guid.Empty }] }
        };
        Assert.Null(Match(identity, "Widget", "urn:controls"));

        Guid unknown = new("4aa5a562-2aad-4c27-8540-3d9f55fda11a");
        identity = role switch
        {
            "target" => Identity([Widget]) with { TargetModuleVersionId = unknown },
            "owner" => Identity([Widget]) with { OwnerModuleVersionId = unknown },
            _ => Identity([Widget with { ModuleVersionId = unknown }])
        };
        Assert.Null(Match(identity, "Widget", "urn:controls"));
    }

    [Theory]
    [InlineData("Demo", true)]
    [InlineData("PresentationFramework", true)]
    [InlineData("ControlsLibrary", true)]
    [InlineData("Demo", false)]
    [InlineData("PresentationFramework", false)]
    [InlineData("ControlsLibrary", false)]
    public void DuplicateModuleNamesAreAmbiguousEvenWhenOnlyOneMvidMatches(string assembly, bool sameMvid)
    {
        var modules = Modules();
        var original = modules.Modules.Single(module => module.AssemblyName == assembly);
        var duplicate = original with
        {
            Path = @"C:\OtherLoad\" + assembly + ".dll",
            ModuleVersionId = sameMvid ? original.ModuleVersionId : new Guid("3b828592-c5af-49a7-8ba9-8eefb4d66c88")
        };
        Assert.Null(Match(Identity([Widget]), "Widget", "urn:controls", modules: modules with { Modules = [.. modules.Modules, duplicate] }));
    }

    [Fact]
    public void MissingCandidateMetadataAndIncompleteCatalogsCannotAuthorizeSourceWrites()
    {
        Assert.Null(Match(Identity([]), "Window", Presentation));
        Assert.Null(Match(Identity([Window]) with { AuthoredTargets = null }, "Window", Presentation));
        Assert.Null(Match(Identity([Window]), "Window", Presentation, modules: Modules() with { Truncated = true }));
        Assert.Null(Match(Identity([Window]), "Window", Presentation, modules: new([])));
    }

    private static InspectionSourcePropertyIdentity Identity(IReadOnlyList<InspectionSourcePropertyTarget> targets) =>
        new("Tag", "System.Windows.FrameworkElement", "PresentationFramework", false, "Content", "Demo.MainWindow", "Demo", AppMvid, FrameworkMvid, targets);

    private static InspectionModuleCatalog Modules() => new([
        Module("Demo", AppMvid), Module("PresentationFramework", FrameworkMvid), Module("ControlsLibrary", LibraryMvid)]);

    private static InspectionModule Module(string assembly, Guid mvid) => new(assembly, assembly + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", @"C:\Project\" + assembly + ".dll", mvid);

    private static InspectionSourcePropertyTarget? Match(InspectionSourcePropertyIdentity property, string? type, string? uri,
        string sourceAssembly = "Demo", InspectionModuleCatalog? modules = null) =>
        InspectionSourcePropertyMapping.Match(property, type, uri, sourceAssembly, modules ?? Modules());
}
