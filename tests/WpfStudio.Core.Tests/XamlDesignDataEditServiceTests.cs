using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class XamlDesignDataEditServiceTests
{
    private const string Presentation = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    private const string Language = "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private const string Design = "xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"";
    private const string Compatibility = "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";

    private static string Apply(string text, string type = "Shop.ViewModels.OrdersViewModel", string? assembly = null)
    {
        var result = XamlDesignDataEditService.CreateEdit(new("C:/Shop/Views/OrdersView.xaml", text, 7, type, assembly));
        Assert.True(result.Success, result.Error);
        Assert.Equal(7, result.Edit!.Version);
        Assert.NotNull(result.Edit.ExpectedTextHash);
        Assert.Contains("OrdersViewModel", result.Explanation);
        // Later edits first, so earlier offsets stay valid.
        foreach (var edit in result.Edit.Edits.OrderByDescending(edit => edit.Start))
            text = text[..edit.Start] + edit.NewText + text[(edit.Start + edit.Length)..];
        // Whatever was written must still be well-formed XML.
        System.Xml.Linq.XDocument.Parse(text);
        return text;
    }

    [Fact]
    public void ABareViewReceivesTheDeclarationAndEveryNamespaceItNeeds()
    {
        string text = Apply($"<UserControl x:Class=\"Shop.Views.OrdersView\" {Presentation} {Language}><Grid/></UserControl>");
        Assert.Equal($"<UserControl x:Class=\"Shop.Views.OrdersView\" {Presentation} {Language} {Design} {Compatibility} xmlns:vm=\"clr-namespace:Shop.ViewModels\" mc:Ignorable=\"d\" d:DataContext=\"{{d:DesignInstance Type=vm:OrdersViewModel}}\"><Grid/></UserControl>", text);
    }

    [Fact]
    public void ExistingPrefixesAreReusedAndIgnorableIsExtendedRatherThanReplaced()
    {
        string text = Apply($"<Window {Presentation} xmlns:design=\"http://schemas.microsoft.com/expression/blend/2008\" {Compatibility} xmlns:models=\"clr-namespace:Shop.ViewModels\" xmlns:other=\"urn:other\" mc:Ignorable=\"other\"/>");
        Assert.Contains("mc:Ignorable=\"other design\"", text);
        Assert.Contains("design:DataContext=\"{design:DesignInstance Type=models:OrdersViewModel}\"", text);
        Assert.DoesNotContain("xmlns:d=", text);
        Assert.DoesNotContain("xmlns:vm=", text);
        Assert.EndsWith("/>", text);
    }

    [Fact]
    public void ATakenPrefixIsNotReusedForAnotherNamespace()
    {
        string text = Apply($"<Window {Presentation} xmlns:d=\"urn:something-else\" xmlns:vm=\"clr-namespace:Shop.Other\"/>");
        Assert.Contains("xmlns:design=\"http://schemas.microsoft.com/expression/blend/2008\"", text);
        Assert.Contains("xmlns:viewModels=\"clr-namespace:Shop.ViewModels\"", text);
        Assert.Contains("mc:Ignorable=\"design\"", text);
        Assert.Contains("design:DataContext=\"{design:DesignInstance Type=viewModels:OrdersViewModel}\"", text);
    }

    [Fact]
    public void AViewModelInAnotherAssemblyNamesItAndAStartTagWithOneAttributePerLineKeepsItsLayout()
    {
        string text = Apply($"<UserControl\r\n    x:Class='Shop.Views.OrdersView'\r\n    {Presentation.Replace('"', '\'')}\r\n    {Language.Replace('"', '\'')}>\r\n</UserControl>", assembly: "Shop.Core");
        Assert.Contains("\r\n    xmlns:vm='clr-namespace:Shop.ViewModels;assembly=Shop.Core'\r\n    mc:Ignorable='d'\r\n    d:DataContext='{d:DesignInstance Type=vm:OrdersViewModel}'>", text);
    }

    [Theory]
    [InlineData("<UserControl xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" d:DataContext=\"{d:DesignInstance Type=x}\"/>", "Shop.OrdersViewModel", "already declares")]
    [InlineData("<UserControl/>", "OrdersViewModel", "namespace")]
    [InlineData("<UserControl/>", "Shop.Outer+OrdersViewModel", "could not be read")]
    [InlineData("<UserControl", "Shop.OrdersViewModel", "could not be read")]
    public void WhatCannotBeDeclaredIsRefusedWithAReason(string text, string type, string expected)
    {
        var result = XamlDesignDataEditService.CreateEdit(new("C:/Shop/View.xaml", text, 1, type));
        Assert.False(result.Success);
        Assert.Null(result.Edit);
        Assert.Contains(expected, result.Error);
    }
}
