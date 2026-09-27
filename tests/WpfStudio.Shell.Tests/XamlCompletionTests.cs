using WpfStudio.App.Services;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Shell.Tests;

public sealed class XamlCompletionTests
{
    private static XamlCompletionService Create() => new()
    {
        Index = new WpfIndexSnapshot([], [], [], [], new Dictionary<string, string>
        {
            ["CustomerViewModel.cs"] = "namespace Demo; public partial class CustomerViewModel { [ObservableProperty] private string _title; public partial Customer Selected { get; set; } [CommunityToolkit.Mvvm.Input.RelayCommand] private async Task RefreshAsync() { } }",
            ["Customer.cs"] = "namespace Demo; public class Customer { public string Name { get; set; } }"
        })
    };
    [Fact]
    public void DesignDataContextIncludesToolkitFieldsCommandsAndProperties()
    {
        const string source = "<Window xmlns:vm=\"clr-namespace:Demo\" d:DataContext=\"{d:DesignInstance Type=vm:CustomerViewModel}\"><TextBlock Text=\"{Binding ";
        var result = Create().Complete("Window.xaml", source, source.Length, 3);
        Assert.Contains(result.Items, item => item.DisplayText == "Title");
        Assert.Contains(result.Items, item => item.DisplayText == "RefreshCommand");
        Assert.Contains(result.Items, item => item.DisplayText == "Selected");
        Assert.Equal(3, result.Version);
    }
    [Fact]
    public void NestedBindingCompletionReplacesOnlyFinalMember()
    {
        const string source = "<Window xmlns:vm=\"clr-namespace:Demo\"><Window.DataContext><vm:CustomerViewModel /></Window.DataContext><TextBlock Text=\"{Binding Selected.Na";
        var result = Create().Complete("Window.xaml", source, source.Length, 1);
        Assert.Equal("Name", Assert.Single(result.Items).DisplayText);
        Assert.Equal(source.Length - 2, result.Start);
        Assert.Equal(2, result.Length);
    }
    [Fact]
    public void UnknownRuntimeDataContextDoesNotInventBindings()
    {
        const string source = "<Window DataContext=\"{Binding Current}\"><TextBlock Text=\"{Binding ";
        Assert.Empty(Create().Complete("Window.xaml", source, source.Length, 1).Items);
    }
}
