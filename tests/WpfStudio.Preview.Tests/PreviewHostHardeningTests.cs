using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WpfStudio.Contracts;
using WpfStudio.PreviewHost;
using WpfStudio.Wpf.PropertyEditing;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class PreviewHostHardeningTests(PreviewFixture fixture)
{
    [Fact]
    public void PreviewDocumentToleratesMissingClrNamespaceAssembly()
    {
        const string xaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:missing="clr-namespace:Missing.Namespace;assembly=WpfStudio.NonExistent.Assembly.Test">
                <missing:FakeControl />
            </Grid>
            """;
        var request = new PreviewRequest("View.xaml", xaml, Version: 1, Width: 400, Height: 300);
        var exception = Record.Exception(() => PreviewDocument.Parse(request, projectAssembly: null));
        Assert.Null(exception);
    }

    [Fact]
    public void PrepareApplicationResetsResourcesWhenApplicationResourcePathIsCleared() => fixture.OnDispatcher(() =>
    {
        var application = Application.Current!;
        application.Resources = new ResourceDictionary { ["StaleKey"] = "StaleValue" };
        var request = new PreviewRequest("View.xaml", "<Grid/>", Version: 1, Width: 400, Height: 300, ApplicationResourcePath: null);
        var assembly = Application.ResourceAssembly ?? typeof(PreviewHostHardeningTests).Assembly;
        bool loaded = CompiledPreview.PrepareApplication(request, assembly);
        Assert.False(loaded);
        Assert.False(application.Resources.Contains("StaleKey"));
        return true;
    });

    [Fact]
    public void ConflictedResetReleasesOverrideCapacitySlotWhilePreservingAppValue() => fixture.OnDispatcher(() =>
    {
        var editor = new TemporaryPropertyEdits(maximumOverrides: 1);
        var first = new TextBlock { Text = "Original" };
        Assert.True(editor.Apply(first, TextBlock.TextProperty, "Override").Success);

        var appBinding = new Binding { Source = "AppOwned", Mode = BindingMode.OneWay };
        BindingOperations.SetBinding(first, TextBlock.TextProperty, appBinding);

        var reset = editor.Reset(first, TextBlock.TextProperty);
        Assert.True(reset.Conflict);
        Assert.Same(appBinding, BindingOperations.GetBindingBase(first, TextBlock.TextProperty));

        var second = new TextBlock { Text = "SecondOriginal" };
        Assert.True(editor.Apply(second, TextBlock.TextProperty, "SecondOverride").Success);
        return true;
    });
}
