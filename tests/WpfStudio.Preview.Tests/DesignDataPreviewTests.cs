using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using WpfStudio.Contracts;
using WpfStudio.PreviewHost;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class DesignDataPreviewTests(PreviewFixture fixture)
{
    private const string Namespaces = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:d='http://schemas.microsoft.com/expression/blend/2008' xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' mc:Ignorable='d' xmlns:sample='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'";
    private const string Path = "C:/preview/DesignData.xaml";
    private Task<PreviewSnapshot> Render(string markup, bool useDesignValues = true) => fixture.Engine.RenderAsync(
        new(Path, markup, 1, 480, 360, UseDesignTimeValues: useDesignValues), default);
    private static string Messages(PreviewSnapshot snapshot) => string.Join("\n", snapshot.Diagnostics.Select(diagnostic => diagnostic.Severity + ": " + diagnostic.Message));
    private async Task<string> Value(PreviewSnapshot snapshot, string name, string property = "Text")
    {
        Assert.True(snapshot.Success, Messages(snapshot));
        var node = snapshot.Nodes.First(node => node.Name == name);
        var inspection = await fixture.Engine.InspectAsync(new(snapshot.Version, node.Id), default);
        return Assert.Single(inspection.Properties, candidate => candidate.Name == property).Value;
    }
    private static void AssertNoBindingFailures(PreviewSnapshot snapshot) =>
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.BindingId is not null || diagnostic.Message.Contains("binding trace", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task DesignInstanceFillsTheDeclaredViewModelWithoutRunningItsConstructor()
    {
        DesignOrdersViewModel.ConstructorCalls = 0;
        string markup = $$"""
            <Grid {{Namespaces}} Name='Root' d:DataContext='{d:DesignInstance Type=sample:DesignOrdersViewModel}'>
              <StackPanel>
                <TextBlock Name='Title' Text='{Binding Title}'/>
                <TextBlock Name='Customer' Text='{Binding Customer.FullName}'/>
                <TextBlock Name='Email' Text='{Binding Customer.Email}'/>
                <TextBlock Name='Count' Text='{Binding OrderCount}'/>
                <TextBlock Name='Total' Text='{Binding Total}'/>
                <TextBlock Name='Status' Text='{Binding Status}'/>
                <CheckBox Name='Busy' IsChecked='{Binding IsBusy}'/>
                <CheckBox Name='HasOrders' IsChecked='{Binding HasOrders, Mode=OneWay}'/>
                <Button Name='Refresh' Command='{Binding RefreshCommand}' Content='Refresh'/>
                <ListBox Name='Orders' ItemsSource='{Binding Orders}' DisplayMemberPath='Product'/>
              </StackPanel>
            </Grid>
            """;
        var snapshot = await Render(markup);
        Assert.True(snapshot.Success, Messages(snapshot));
        Assert.Equal(0, DesignOrdersViewModel.ConstructorCalls);
        AssertNoBindingFailures(snapshot);

        Assert.False(string.IsNullOrWhiteSpace(await Value(snapshot, "Title")));
        Assert.Equal("Ada Lovelace", await Value(snapshot, "Customer"));
        Assert.Equal("ada.lovelace@example.com", await Value(snapshot, "Email"));
        // Computed members read the generated fields exactly as they read real ones.
        Assert.Equal("5", await Value(snapshot, "Count"));
        Assert.NotEqual("0", await Value(snapshot, "Total"));
        Assert.Equal("Shipped", await Value(snapshot, "Status"));
        Assert.Equal("False", await Value(snapshot, "Busy", "IsChecked"));
        Assert.Equal("True", await Value(snapshot, "HasOrders", "IsChecked"));
        Assert.Equal("True", await Value(snapshot, "Refresh", "IsEnabled"));
        Assert.Equal(5, snapshot.Nodes.Count(node => node.Type == "System.Windows.Controls.ListBoxItem"));

        var data = Assert.Single(snapshot.DesignData!);
        Assert.Equal(("DataContext", PreviewDesignDataKinds.Sample, typeof(DesignOrdersViewModel).FullName, 1), (data.Property, data.Kind, data.TypeName, data.Line));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Information" && diagnostic.Message.Contains("constructor was not run", StringComparison.Ordinal));
        var root = await fixture.Engine.InspectAsync(new(snapshot.Version, snapshot.Nodes.First(node => node.Name == "Root").Id), default);
        var context = Assert.Single(root.Properties, property => property.Name == "DataContext");
        Assert.Equal("Local (design baseline)", context.ValueSource);
        Assert.False(context.CanWriteSource);

        var runtime = await Render(markup, useDesignValues: false);
        Assert.True(runtime.Success, Messages(runtime));
        Assert.Null(runtime.DesignData);
        Assert.Equal("", await Value(runtime, "Title"));
    }

    [Fact]
    public async Task CreatableDesignInstanceRunsTheConstructorAndAFailingOneFallsBackToSampleValues()
    {
        string Markup(string type) => $"<Grid {Namespaces} d:DataContext='{{d:DesignInstance sample:{type}, IsDesignTimeCreatable=True}}'><TextBlock Name='Title' Text='{{Binding Title}}'/></Grid>";
        var created = await Render(Markup(nameof(DesignCreatableViewModel)));
        Assert.Equal("Set by the constructor", await Value(created, "Title"));
        Assert.Equal(PreviewDesignDataKinds.Created, Assert.Single(created.DesignData!).Kind);

        DesignOrdersViewModel.ConstructorCalls = 0;
        var fallback = await Render(Markup(nameof(DesignOrdersViewModel)));
        Assert.Equal(1, DesignOrdersViewModel.ConstructorCalls);
        Assert.False(string.IsNullOrWhiteSpace(await Value(fallback, "Title")));
        Assert.Equal(PreviewDesignDataKinds.Sample, Assert.Single(fallback.DesignData!).Kind);
        Assert.Contains(fallback.Diagnostics, diagnostic => diagnostic.Severity == "Warning" && diagnostic.Line == 1
            && diagnostic.Message.Contains("could not be constructed", StringComparison.Ordinal) && diagnostic.Message.Contains("needs its services", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{d:DesignInstance sample:DesignOrder, CreateList=True}", 5)]
    [InlineData("{d:DesignInstance Type={x:Type sample:DesignOrder}}", 5)]
    [InlineData("{d:DesignInstance sample:DesignCreatableViewModel, CreateList=True, IsDesignTimeCreatable=True}", 3)]
    public async Task DesignInstanceListsFillAnItemsControl(string declaration, int expected)
    {
        string markup = $"<ListBox {Namespaces} Name='Subject' d:ItemsSource='{declaration}'><ListBoxItem>Runtime</ListBoxItem></ListBox>";
        var snapshot = await Render(markup);
        Assert.True(snapshot.Success, Messages(snapshot));
        Assert.Equal(expected, snapshot.Nodes.Count(node => node.Type == "System.Windows.Controls.ListBoxItem"));
        Assert.Equal("Local (design baseline)", (await fixture.Engine.InspectAsync(new(snapshot.Version, snapshot.Nodes.First(node => node.Name == "Subject").Id), default))
            .Properties.Single(property => property.Name == "ItemsSource").ValueSource);
        AssertNoBindingFailures(snapshot);
        Assert.Equal(1, (await Render(markup, useDesignValues: false)).Nodes.Count(node => node.Type == "System.Windows.Controls.ListBoxItem"));
    }

    [Fact]
    public async Task InterfaceAndAbstractDesignTypesUseTheProjectsImplementation()
    {
        string markup = $"<StackPanel {Namespaces} d:DataContext='{{d:DesignInstance sample:IDesignShape}}'><TextBlock Name='Kind' Text='{{Binding Kind}}'/><TextBlock Name='Sides' Text='{{Binding Sides}}'/></StackPanel>";
        var snapshot = await Render(markup);
        Assert.False(string.IsNullOrWhiteSpace(await Value(snapshot, "Kind")));
        Assert.NotEqual("", await Value(snapshot, "Sides"));
        AssertNoBindingFailures(snapshot);
        Assert.Equal(PreviewDesignDataKinds.Sample, Assert.Single(snapshot.DesignData!).Kind);
    }

    [Fact]
    public async Task SampleDataAnswersWhateverTheItemTemplateBindsTo()
    {
        string markup = $$"""
            <ListBox {{Namespaces}} Name='Subject' d:ItemsSource='{d:SampleData ItemCount=4}'>
              <ListBox.ItemTemplate><DataTemplate><StackPanel>
                <TextBlock Name='ItemName' Text='{Binding Name}'/>
                <TextBlock Name='ItemEmail' Text='{Binding Customer.Email}'/>
                <TextBlock Name='ItemPrice' Text='{Binding Price, StringFormat=N2}'/>
                <TextBlock Name='ItemDate' Text='{Binding CreatedAt, StringFormat=yyyy}'/>
                <CheckBox Name='ItemActive' IsChecked='{Binding IsActive}'/>
                <Image Name='ItemPhoto' Source='{Binding Photo}' Width='16' Height='16'/>
              </StackPanel></DataTemplate></ListBox.ItemTemplate>
            </ListBox>
            """;
        var snapshot = await Render(markup);
        Assert.True(snapshot.Success, Messages(snapshot));
        Assert.Equal(4, snapshot.Nodes.Count(node => node.Type == "System.Windows.Controls.ListBoxItem"));
        AssertNoBindingFailures(snapshot);
        Assert.Equal("Aurora", await Value(snapshot, "ItemName"));
        Assert.Equal("ada.lovelace@example.com", await Value(snapshot, "ItemEmail"));
        // A binding formats in its element's language, which is en-US unless the markup says otherwise.
        Assert.Equal("19.99", await Value(snapshot, "ItemPrice"));
        Assert.Matches("^[0-9]{4}$", await Value(snapshot, "ItemDate"));
        Assert.Equal("True", await Value(snapshot, "ItemActive", "IsChecked"));
        Assert.NotEqual("(null)", await Value(snapshot, "ItemPhoto", "Source"));
        Assert.Equal(PreviewDesignDataKinds.Untyped, Assert.Single(snapshot.DesignData!).Kind);
    }

    [Fact]
    public async Task OrdinaryMarkupInDesignAttributesIsEvaluatedByTheLoader()
    {
        string markup = $$"""
            <StackPanel {{Namespaces}} d:DataContext='{x:Static sample:DesignStatics.Customer}'>
              <StackPanel.Resources><SolidColorBrush x:Key='Accent' Color='#FF112233'/></StackPanel.Resources>
              <TextBox Name='Source' Text='Typed here'/>
              <TextBlock Name='Static' Text='{Binding FullName}'/>
              <TextBlock Name='Bound' Text='Runtime' d:Text='{Binding Text, ElementName=Source}'/>
              <Border Name='Resource' d:Background='{StaticResource Accent}'/>
            </StackPanel>
            """;
        var snapshot = await Render(markup);
        Assert.Equal("Static Customer", await Value(snapshot, "Static"));
        Assert.Equal("Typed here", await Value(snapshot, "Bound"));
        Assert.Equal("#FF112233", await Value(snapshot, "Resource", "Background"));
        AssertNoBindingFailures(snapshot);
        Assert.Equal("Runtime", await Value(await Render(markup, useDesignValues: false), "Bound"));
    }

    [Fact]
    public async Task ADesignDataShapeInsideATemplateDescribesItemsAndNeverReplacesThem()
    {
        string markup = $$"""
            <ListBox {{Namespaces}} xmlns:sys='clr-namespace:System;assembly=mscorlib'>
              <ListBox.ItemTemplate>
                <DataTemplate d:DataContext='{d:DesignInstance sample:DesignOrder}'>
                  <Grid d:DataContext='{d:DesignInstance sample:DesignOrder}'><TextBlock Name='Item' Text='{Binding}'/></Grid>
                </DataTemplate>
              </ListBox.ItemTemplate>
              <sys:String>Real item</sys:String>
            </ListBox>
            """;
        var snapshot = await Render(markup);
        Assert.Equal("Real item", await Value(snapshot, "Item"));
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Warning");
        Assert.Null(snapshot.DesignData);
    }

    [Fact]
    public async Task DesignerBookkeepingIsSilentAndTheEyeToggleHidesTheElement()
    {
        string markup = $"<StackPanel {Namespaces}><Button Name='Locked' d:IsLocked='True' d:LayoutOverrides='Width, Height' Content='A'/><Button Name='Hidden' d:IsHidden='True' Content='B'/><Button Name='Shown' d:IsHidden='False' Content='C'/></StackPanel>";
        var snapshot = await Render(markup);
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Warning");
        Assert.Equal("Hidden", await Value(snapshot, "Hidden", "Visibility"));
        Assert.Equal("Visible", await Value(snapshot, "Shown", "Visibility"));
        Assert.Equal("Visible", await Value(await Render(markup, useDesignValues: false), "Hidden", "Visibility"));
    }

    [Fact]
    public async Task DesignSourceFillsACollectionViewSourceAndADisabledEffectIsRemoved()
    {
        string markup = $$"""
            <Grid {{Namespaces}}>
              <Grid.Resources><CollectionViewSource x:Key='Orders' d:DesignSource='{d:DesignInstance sample:DesignOrder, CreateList=True}'/></Grid.Resources>
              <ListBox ItemsSource='{Binding Source={StaticResource Orders} }' DisplayMemberPath='Product'/>
              <Border Name='Blurred' d:IsEffectDisabled='True'><Border.Effect><BlurEffect Radius='4'/></Border.Effect></Border>
            </Grid>
            """;
        var snapshot = await Render(markup);
        Assert.True(snapshot.Success, Messages(snapshot));
        Assert.Equal(5, snapshot.Nodes.Count(node => node.Type == "System.Windows.Controls.ListBoxItem"));
        Assert.Equal("(null)", await Value(snapshot, "Blurred", "Effect"));
        AssertNoBindingFailures(snapshot);
        var runtime = await Render(markup, useDesignValues: false);
        Assert.DoesNotContain(runtime.Nodes, node => node.Type == "System.Windows.Controls.ListBoxItem");
        Assert.NotEqual("(null)", await Value(runtime, "Blurred", "Effect"));
    }

    [Fact]
    public async Task ARenderThatFailsOnDesignMarkupIsShownWithoutIt()
    {
        string markup = $"<StackPanel {Namespaces}><TextBlock Name='Subject' Text='Runtime' d:Text='Design' d:Tag='{{StaticResource NoSuchResource}}'/></StackPanel>";
        var snapshot = await Render(markup);
        Assert.Equal("Runtime", await Value(snapshot, "Subject"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Warning" && diagnostic.Message.Contains("shown without them", StringComparison.Ordinal)
            && diagnostic.Message.Contains("NoSuchResource", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Error");

        // A view that is broken by itself still fails, with its own error.
        var broken = await Render($"<StackPanel {Namespaces}><TextBlock Text='{{StaticResource NoSuchResource}}' d:Tag='{{Binding}}'/></StackPanel>");
        Assert.False(broken.Success);
        Assert.Contains(broken.Diagnostics, diagnostic => diagnostic.Severity == "Error" && diagnostic.Message.Contains("NoSuchResource", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{d:DesignInstance sample:NoSuchType}", "could not be loaded")]
    [InlineData("{d:DesignInstance sample:DesignOrder, Frobnicate=True}", "takes a Type")]
    [InlineData("{d:DesignData Source=MissingSample.xaml}", "could not be loaded")]
    [InlineData("{d:DesignData Source=../outside/Sample.xaml}", "inside the project")]
    [InlineData("{d:SampleData ItemCount=0}", "ItemCount")]
    [InlineData("{d:Unknown}", "not supported")]
    [InlineData("{Binding Source={d:DesignInstance sample:DesignOrder}}", "nests a design extension")]
    public async Task DeclarationsThatCannotBeHonoredAreExplainedAndLeaveTheViewAlone(string declaration, string expected)
    {
        string markup = $"<Grid {Namespaces} Name='Root' DataContext='Runtime context' d:DataContext='{declaration}'><TextBlock Name='Subject' Text='{{Binding}}'/></Grid>";
        var snapshot = await Render(markup);
        Assert.Equal("Runtime context", await Value(snapshot, "Subject"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Warning" && diagnostic.Line == 1 && diagnostic.Message.Contains(expected, StringComparison.Ordinal));
        Assert.Null(snapshot.DesignData);
    }

    [Fact]
    public async Task DesignInstanceIsRefusedForAPropertyThatTakesNeitherAnObjectNorAList()
    {
        var snapshot = await Render($"<TextBlock {Namespaces} Name='Subject' Text='Runtime' d:Text='{{d:DesignInstance sample:DesignOrder}}'/>");
        Assert.Equal("Runtime", await Value(snapshot, "Subject"));
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Severity == "Warning" && diagnostic.Message.Contains("does not take", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DesignDataFileLoadsItsObjectsFromInsideTheProject()
    {
        string directory = Directory.CreateTempSubdirectory("WpfStudio.DesignData.").FullName;
        try
        {
            Directory.CreateDirectory(System.IO.Path.Combine(directory, "SampleData"));
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "SampleData", "Customer.xaml"),
                "<sample:DesignCustomer xmlns:sample='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests' FullName='From the sample file' Email='file@example.com'/>");
            string markup = $"<StackPanel {Namespaces} d:DataContext='{{d:DesignData Source=/SampleData/Customer.xaml}}'><TextBlock Name='Subject' Text='{{Binding FullName}}'/></StackPanel>";
            var snapshot = await fixture.Engine.RenderAsync(new(System.IO.Path.Combine(directory, "Views", "CustomerView.xaml"), markup, 1, 320, 200, ProjectDirectory: directory), default);
            Assert.Equal("From the sample file", await Value(snapshot, "Subject"));
            var data = Assert.Single(snapshot.DesignData!);
            Assert.Equal((PreviewDesignDataKinds.File, typeof(DesignCustomer).FullName), (data.Kind, data.TypeName));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DesignDataAndItsProvenanceCrossTheHostProcessBoundary()
    {
        // The isolated host has no test assembly to load, so the data here has no declared type.
        string markup = $"<ListBox {Namespaces.Replace(" xmlns:sample='clr-namespace:WpfStudio.Preview.Tests;assembly=WpfStudio.Preview.Tests'", "", StringComparison.Ordinal)} d:ItemsSource='{{d:SampleData ItemCount=3}}' DisplayMemberPath='Name'/>";
        await using var client = new WpfStudio.Runtime.Design.PreviewClient(PreviewHostUnderTest.ExecutablePath);
        var snapshot = await client.RenderAsync(new(Path, markup, 41, 320, 200));
        Assert.True(snapshot.Success, Messages(snapshot));
        Assert.Equal(3, snapshot.Nodes.Count(node => node.Type == "System.Windows.Controls.ListBoxItem"));
        var data = Assert.Single(snapshot.DesignData!);
        Assert.Equal(("ItemsSource", PreviewDesignDataKinds.Untyped, 1), (data.Property, data.Kind, data.Line));
        Assert.Contains("3 sample items", data.Summary, StringComparison.Ordinal);

        var runtime = await client.RenderAsync(new(Path, markup, 42, 320, 200, UseDesignTimeValues: false, InferDesignData: false));
        Assert.True(runtime.Success, Messages(runtime));
        Assert.Null(runtime.DesignData);
        Assert.DoesNotContain(runtime.Nodes, node => node.Type == "System.Windows.Controls.ListBoxItem");
    }

    [Fact]
    public void AViewWithNoDataContextGetsSampleDataFromTheViewModelItsNamePairsItWith()
    {
        const string Markup = "<UserControl xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='WpfStudio.Preview.Tests.Views.DesignOrdersView'><TextBlock Text='{Binding Title}'/></UserControl>";
        var assembly = typeof(DesignOrdersViewModel).Assembly;
        DesignOrdersViewModel.ConstructorCalls = 0;
        var (context, kinds, information) = fixture.OnDispatcher(() =>
        {
            var document = PreviewDocument.Parse(new(Path, Markup, 1), assembly);
            using var reader = document.CreateSourceReader();
            var root = (FrameworkElement)XamlReader.Load(reader);
            return (root.DataContext, document.DesignData.Select(data => data.Kind).ToArray(), document.Diagnostics.Where(diagnostic => diagnostic.Severity == "Information").Select(diagnostic => diagnostic.Message).ToArray());
        });
        var model = Assert.IsType<DesignOrdersViewModel>(context);
        Assert.Equal(0, DesignOrdersViewModel.ConstructorCalls);
        Assert.Equal(5, model.Orders.Count);
        Assert.Equal([PreviewDesignDataKinds.Inferred], kinds);
        Assert.Contains(information, message => message.Contains("matched to this view by name", StringComparison.Ordinal));

        // Nothing is inferred when the author declared a context, turned the feature off, or a scenario supplies the data.
        static object? Context(PreviewRequest request, System.Reflection.Assembly assembly)
        {
            var document = PreviewDocument.Parse(request, assembly);
            using var reader = document.CreateSourceReader();
            return ((FrameworkElement)XamlReader.Load(reader)).DataContext;
        }
        Assert.Null(fixture.OnDispatcher(() => Context(new(Path, Markup, 1, InferDesignData: false), assembly)));
        Assert.Null(fixture.OnDispatcher(() => Context(new(Path, Markup, 1, UseDesignTimeValues: false), assembly)));
        Assert.Null(fixture.OnDispatcher(() => Context(new(Path, Markup, 1, Scenario: new("Loaded", DataContextFactory: new("T", "M"))), assembly)));
        Assert.Equal("Declared", fixture.OnDispatcher(() => Context(new(Path, Markup.Replace("x:Class", "DataContext='Declared' x:Class", StringComparison.Ordinal), 1), assembly)));
    }

    [Theory]
    [InlineData("App.Views.OrdersView", null)]
    [InlineData("WpfStudio.Preview.Tests.Views.DesignOrdersView", "DesignOrdersViewModel")]
    [InlineData("WpfStudio.Preview.Tests.DesignOrdersPage", "DesignOrdersViewModel")]
    [InlineData("WpfStudio.Preview.Tests.DesignCreatableWindow", "DesignCreatableViewModel")]
    [InlineData("WpfStudio.Preview.Tests.DesignShell", "DesignShellVM")]
    public void ViewModelsAreMatchedByTheUsualNamingConventions(string viewClass, string? expected)
    {
        var (viewModel, ambiguity) = DesignDataConventions.FindViewModel(viewClass, typeof(DesignOrdersViewModel).Assembly);
        Assert.Null(ambiguity);
        Assert.Equal(expected, viewModel?.Name);
    }

    [Theory]
    [InlineData("{d:DesignInstance Type=vm:Orders, IsDesignTimeCreatable=True}", "d", "DesignInstance", null, "Type=vm:Orders|IsDesignTimeCreatable=True")]
    [InlineData("{ d:DesignInstance vm:Orders , CreateList = True }", null, null, null, null)]
    [InlineData("{d:DesignInstance vm:Orders, CreateList=True}", "d", "DesignInstance", "vm:Orders", "CreateList=True")]
    [InlineData("{d:DesignInstance Type={x:Type vm:Orders}}", "d", "DesignInstance", null, "Type={x:Type vm:Orders}")]
    [InlineData("{Binding Path=Name, Converter={StaticResource C}, ConverterParameter='a, b'}", null, "Binding", null, "Path=Name|Converter={StaticResource C}|ConverterParameter=a, b")]
    [InlineData("{x:Null}", "x", "Null", null, "")]
    [InlineData("{d:DesignDataExtension Source=/Sample/Data.xaml}", "d", "DesignData", null, "Source=/Sample/Data.xaml")]
    [InlineData("{}Literal", null, null, null, null)]
    [InlineData("{Binding", null, null, null, null)]
    [InlineData("{Binding A}}", null, null, null, null)]
    [InlineData("Plain", null, null, null, null)]
    public void MarkupIsReadIntoItsNameAndArguments(string text, string? prefix, string? name, string? positional, string? named)
    {
        var markup = DesignMarkup.Parse(text);
        if (name is null) { Assert.Null(markup); return; }
        Assert.NotNull(markup);
        Assert.Equal((prefix, name), (markup.Prefix, markup.Name));
        Assert.Equal(positional, markup.Positional.SingleOrDefault());
        Assert.Equal(named, string.Join("|", markup.Named.Select(pair => pair.Key + "=" + pair.Value)));
    }

    [Theory]
    [InlineData("CustomerEmailAddress", "customer email address")]
    [InlineData("OrderID", "order id")]
    [InlineData("IsHTMLEnabled", "is html enabled")]
    [InlineData("address2", "address 2")]
    [InlineData("_firstName", "first name")]
    [InlineData("photo_url", "photo url")]
    public void MemberNamesAreSplitIntoWords(string member, string expected) => Assert.Equal(expected, string.Join(" ", DesignSampleValues.Words(member)));

    [Fact]
    public void SampleValuesFollowTheNounANameEndsIn()
    {
        Assert.Equal("ada.lovelace@example.com", DesignSampleValues.Text("EmailAddress", null, 0));
        Assert.Equal("Grace Hopper", DesignSampleValues.Text("CreatedBy", null, 1));
        Assert.Equal("Ada Lovelace", DesignSampleValues.Text("Name", "Customer", 0));
        Assert.Equal("Aurora", DesignSampleValues.Text("Name", "Product", 0));
        Assert.Equal("Active", DesignSampleValues.Text("Status", null, 0));
        Assert.Equal("Contoso", DesignSampleValues.Text("Company", null, 0));
        Assert.StartsWith("pack://application:,,,/WpfStudio.PreviewHost;component/designdata/", DesignSampleValues.Text("PhotoUrl", null, 0), StringComparison.Ordinal);
        Assert.Equal(3, DesignSampleValues.Number("ItemCount", 0));
        Assert.Equal(19.99, DesignSampleValues.Number("UnitPrice", 0));
        Assert.Equal(3, DesignSampleValues.Number("TotalItems", 0));
        Assert.False(DesignSampleValues.Flag("IsLoading", 0));
        Assert.False(DesignSampleValues.Flag("HasErrors", 0));
        Assert.True(DesignSampleValues.Flag("HasItems", 0));
        Assert.True(DesignSampleValues.Flag("IsNotBusy", 0));
        Assert.True(DesignSampleValues.Flag("CanSave", 0));
        Assert.True(DesignSampleValues.Flag("IsSelected", 0));
        Assert.False(DesignSampleValues.Flag("IsSelected", 1));
        // The same name and position always give the same value.
        Assert.Equal(DesignSampleValues.Text("Subject", null, 2), DesignSampleValues.Text("Subject", null, 2));
        Assert.True(DesignSampleValues.Date("DueDate", 0) > DateTime.Today);
        Assert.True(DesignSampleValues.Date("CreatedAt", 0) < DateTime.Today);
    }

    [Fact]
    public void SampleFactoryFillsCollectionsRecordsAndNestedModelsToABoundedDepth()
    {
        // A collection view belongs to the thread that made it, so it is counted there.
        var (model, viewItems) = fixture.OnDispatcher(() =>
        {
            var catalog = (DesignCatalog)new DesignSampleFactory(typeof(DesignCatalog).Assembly).Create(typeof(DesignCatalog))!;
            return (catalog, catalog.View?.Cast<object>().Count());
        });
        Assert.Equal(5, model.Products.Length);
        Assert.Equal(5, model.Tags.Count);
        Assert.Equal(5, model.ReadOnlyNames.Count);
        Assert.Equal(5, model.Immutable.Length);
        Assert.Equal(5, viewItems);
        Assert.All(model.Products, product => Assert.False(string.IsNullOrEmpty(product.Name)));
        Assert.NotEqual(model.Products[0].Name, model.Products[1].Name);
        Assert.True(model.Products[0].Price > 0);
        Assert.Equal(System.Windows.Visibility.Visible, model.PanelVisibility);
        Assert.NotNull(model.Accent);
        Assert.NotNull(model.Cover);
        Assert.Null(model.Service);
        Assert.Null(model.Callback);
        Assert.Null(model.Anything);
        Assert.Null(model.Blob);
        // A type that refers to itself becomes a small tree, not an endless one.
        Assert.NotNull(model.Root);
        int Depth(DesignNode? node) => node is null ? 0 : 1 + (node.Children.Count == 0 ? 0 : node.Children.Max(Depth));
        Assert.InRange(Depth(model.Root), 2, 6);
        // Written through the setter when no field follows a convention; a setter that throws leaves the default.
        Assert.False(string.IsNullOrEmpty(model.StoredElsewhere));
        Assert.Null(model.ThrowsOnSet);
    }

    [Fact]
    public void ACollectionTypeThatHoldsItselfIsFilledToABoundedDepth()
    {
        // Each of these recursed without end before lists counted as a level: the element of the list is the list's own type.
        var factory = new DesignSampleFactory(typeof(DesignNest).Assembly);
        var (holder, direct, items, abstractNest) = fixture.OnDispatcher(() => (
            (DesignNestHolder)factory.Create(typeof(DesignNestHolder))!,
            (DesignNest?)factory.CreateSequence(typeof(DesignNest)),
            factory.CreateList(typeof(DesignNest), 5),
            factory.CreateSequence(typeof(DesignAbstractNest))));
        static int Depth(DesignNest nest) => 1 + (nest.Count == 0 ? 0 : nest.Max(Depth));
        Assert.NotNull(holder.Nest);
        Assert.InRange(Depth(holder.Nest!), 2, 4);
        Assert.NotNull(direct);
        Assert.Equal(5, direct!.Count);
        Assert.InRange(Depth(direct), 2, 4);
        Assert.All(items.Cast<DesignNest>(), nest => Assert.InRange(Depth(nest), 1, 4));
        // An abstract collection of itself has nothing to construct; it ends empty-handed instead of overflowing.
        Assert.Null(abstractNest);
        // Ordinary nested lists still receive their items, fewer at each level.
        Assert.Equal(5, holder.Grid.Count);
        Assert.All(holder.Grid, row => Assert.Equal(3, row.Count));
        Assert.Equal(5, holder.Jagged.Length);
        Assert.All(holder.Jagged, row => Assert.Equal(3, row.Length));
    }
}

public interface IDesignService { string Load(); }

public enum DesignOrderStatus { None, Shipped, Delivered }

public sealed class DesignCustomer
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
}

public sealed record DesignOrder(string Product, decimal Amount);

/// <summary>A view model as applications write them: a service from the container, hand-written change notification, a get-only collection and computed members.</summary>
public sealed class DesignOrdersViewModel : INotifyPropertyChanged
{
    internal static int ConstructorCalls;
    private readonly IDesignService _service;
    private string _title = "";
    private bool _isBusy;

    public DesignOrdersViewModel()
    {
        ConstructorCalls++;
        throw new InvalidOperationException("This view model needs its services.");
    }

    public DesignOrdersViewModel(IDesignService service)
    {
        _service = service;
        Orders = [];
        RefreshCommand = null!;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title { get => _title; set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); } }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; PropertyChanged?.Invoke(this, new(nameof(IsBusy))); } }
    public DesignCustomer? Customer { get; set; }
    public ObservableCollection<DesignOrder> Orders { get; }
    public DesignOrderStatus Status { get; set; }
    public ICommand RefreshCommand { get; }
    public IDesignService Service => _service;
    public int OrderCount => Orders.Count;
    public decimal Total => Orders.Sum(order => order.Amount);
    public bool HasOrders => Orders.Count > 0;
}

public sealed class DesignCreatableViewModel
{
    public string Title { get; set; } = "Set by the constructor";
}

public sealed class DesignShellVM
{
    public string Title { get; set; } = "";
}

public interface IDesignShape
{
    string Kind { get; }
    int Sides { get; }
}

public sealed class DesignShape : IDesignShape
{
    public string Kind { get; set; } = "";
    public int Sides { get; set; }
}

public static class DesignStatics
{
    public static DesignCustomer Customer { get; } = new() { FullName = "Static Customer", Email = "static@example.com" };
}

public sealed class DesignProduct
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public sealed class DesignNode
{
    public string Title { get; set; } = "";
    public List<DesignNode> Children { get; set; } = [];
}

/// <summary>A tree written as a collection of its own type.</summary>
public sealed class DesignNest : List<DesignNest>;

public abstract class DesignAbstractNest : IEnumerable<DesignAbstractNest>
{
    public abstract IEnumerator<DesignAbstractNest> GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class DesignNestHolder
{
    public DesignNest? Nest { get; set; }
    public List<List<int>> Grid { get; set; } = [];
    public string[][] Jagged { get; set; } = [];
}

public sealed class DesignCatalog
{
    private string? _kept;
    public DesignProduct[] Products { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public ReadOnlyCollection<string> ReadOnlyNames { get; set; } = new([]);
    public System.Collections.Immutable.ImmutableArray<int> Immutable { get; set; }
    public ICollectionView? View { get; set; }
    public System.Windows.Visibility PanelVisibility { get; set; } = System.Windows.Visibility.Collapsed;
    public System.Windows.Media.Brush? Accent { get; set; }
    public System.Windows.Media.ImageSource? Cover { get; set; }
    public IDesignService? Service { get; set; }
    public Action? Callback { get; set; }
    public object? Anything { get; set; }
    public byte[]? Blob { get; set; }
    public DesignNode? Root { get; set; }
    public string? StoredElsewhere { get => _kept; set => _kept = value; }
    public string? ThrowsOnSet { get => null; set => throw new InvalidOperationException("Never settable."); }
}
