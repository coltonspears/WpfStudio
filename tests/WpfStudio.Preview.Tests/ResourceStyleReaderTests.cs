using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class ResourceStyleReaderTests(PreviewFixture fixture)
{
    [Fact]
    public void KeysOnlyCaptureDoesNotCallResourceLookupOrCustomFormatting() => fixture.OnDispatcher(() =>
    {
        var dictionary = new CountingDictionary();
        var key = new ExplosiveKey();
        dictionary.Add(key, new ExplosiveValue());
        dictionary.Add("Unused", new ExplosiveValue());
        var target = new Border { Background = System.Windows.Media.Brushes.Red, Resources = dictionary };
        var snapshot = ResourceStyleReader.Capture(target, Border.BackgroundProperty);
        Assert.True(snapshot.Available, snapshot.Status);
        Assert.Contains(snapshot.ResourceScopes, scope => scope.Keys.Contains("\"Unused\""));
        Assert.Contains(snapshot.ResourceScopes, scope => scope.Keys.Contains("(" + typeof(ExplosiveKey).FullName + ")"));
        Assert.Equal(0, dictionary.Lookups);
        Assert.Equal(0, key.FormattingCalls);
        return true;
    });

    [Fact]
    public void OversizedDictionaryOmitsTheEntireKeyCopyAndReportsIncompleteCoverage() => fixture.OnDispatcher(() =>
    {
        var target = new Border();
        for (int i = 0; i < 1025; i++) target.Resources.Add("Key" + i, "Value");
        var snapshot = ResourceStyleReader.Capture(target, Border.BackgroundProperty);
        Assert.True(snapshot.Available);
        Assert.True(snapshot.Truncated);
        var scope = Assert.Single(snapshot.ResourceScopes, item => item.Label == "Selected element");
        Assert.True(scope.KeysTruncated);
        Assert.Empty(scope.Keys);
        return true;
    });

    [Fact]
    public void CustomAmbientAccessorsAreNotInvokedByPassiveCapture() => fixture.OnDispatcher(() =>
    {
        var target = new CustomAmbientBorder();
        target.Resources.Add("Private", "Value");
        var snapshot = ResourceStyleReader.Capture(target, Border.BackgroundProperty);
        Assert.True(snapshot.Available);
        Assert.Equal(0, target.AmbientCalls);
        Assert.Contains(snapshot.Notices, notice => notice.Contains("custom ambient", StringComparison.OrdinalIgnoreCase));
        return true;
    });

    [Fact]
    public void LocalResourceExpressionCanExposeAKeyEvenWhenItsValueIsMissing() => fixture.OnDispatcher(() =>
    {
        var target = new Border();
        target.SetResourceReference(Border.BackgroundProperty, "MissingResource");
        var snapshot = ResourceStyleReader.Capture(target, Border.BackgroundProperty);
        Assert.True(snapshot.Available);
        Assert.Contains(snapshot.Facts, fact => fact.Name == "Local DynamicResource key" && fact.Value == "\"MissingResource\"");
        Assert.Empty(snapshot.ResourceEvents);
        return true;
    });

    [Fact]
    public void CaptureRejectsTheWrongDispatcher()
    {
        var target = fixture.OnDispatcher(() => new Border());
        Assert.False(ResourceStyleReader.Capture(target, Border.BackgroundProperty).Available);
    }

    [Fact]
    public void TemplateTargetNamesFilterDeclarationsWithoutClaimingAWinningTrigger() => fixture.OnDispatcher(() =>
    {
        var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <StackPanel><Border x:Name="First"/><Border x:Name="Second"/></StackPanel>
              <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter TargetName="First" Property="Background" Value="Red"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        var owner = new Button { Template = template };
        owner.ApplyTemplate();
        var first = (Border)template.FindName("First", owner);
        var second = (Border)template.FindName("Second", owner);
        var included = ResourceStyleReader.Capture(first, Border.BackgroundProperty);
        Assert.Contains(included.Declarations, declaration => declaration.Kind == "Setter" && declaration.TargetName == "First");
        Assert.Contains(included.Declarations, declaration => declaration.Kind == "Trigger" && declaration.Description.Contains("not evaluated", StringComparison.Ordinal));
        Assert.DoesNotContain(ResourceStyleReader.Capture(second, Border.BackgroundProperty).Declarations, declaration => declaration.Kind == "Setter");
        return true;
    });

    [Fact]
    public void ControlChildrenIncludeTheirOwnAndOuterTemplates() => fixture.OnDispatcher(() =>
    {
        var template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Button x:Name="Inner"><Button.Template><ControlTemplate TargetType="Button"><Border/></ControlTemplate></Button.Template></Button>
              <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Inner" Property="Background" Value="Red"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        var owner = new Button { Template = template };
        owner.ApplyTemplate();
        var inner = (Button)template.FindName("Inner", owner);
        Assert.NotNull(inner.Template);
        var result = ResourceStyleReader.Capture(inner, Control.BackgroundProperty);
        Assert.Contains(result.Declarations, item => item.Kind == "ControlTemplate" && item.Description == "Current ControlTemplate");
        Assert.Contains(result.Declarations, item => item.Kind == "ControlTemplate" && item.Description == "Templated parent's ControlTemplate");
        Assert.Contains(result.Declarations, item => item.Kind == "Setter" && item.TargetName == "Inner" && item.Property!.EndsWith(".Background"));
        return true;
    });

    [Fact]
    public void LongDependencyPropertyNamesAreBoundedWithoutEvaluatingMetadata() => fixture.OnDispatcher(() =>
    {
        string name = "Long" + Guid.NewGuid().ToString("N") + new string('x', 5000);
        var property = DependencyProperty.RegisterAttached(name, typeof(string), typeof(ResourceStyleReaderTests), new PropertyMetadata("value"));
        var result = ResourceStyleReader.Capture(new Border(), property);
        Assert.True(result.Available);
        Assert.All(result.Facts, fact => Assert.True(fact.Value.Length < 1200));
        return true;
    });

    [Fact]
    public void CustomStyleTargetTypeMetadataIsNotInvokedDuringCapture() => fixture.OnDispatcher(() =>
    {
        var type = new GuardedType(typeof(Border));
        var target = new Border { Style = new Style(type) };
        type.Armed = true;
        var result = ResourceStyleReader.Capture(target, Border.BackgroundProperty);
        Assert.True(result.Available, result.Status);
        Assert.Equal(0, type.MetadataReads);
        Assert.Contains(result.Declarations, item => item.Kind == "Style" && item.Description.Contains(nameof(GuardedType), StringComparison.Ordinal));
        return true;
    });

    private sealed class CountingDictionary : ResourceDictionary
    {
        internal int Lookups;
        protected override void OnGettingValue(object key, ref object value, out bool canCache)
        { Lookups++; base.OnGettingValue(key, ref value, out canCache); }
    }

    private sealed class ExplosiveKey
    {
        internal int FormattingCalls;
        public override string ToString() { FormattingCalls++; throw new InvalidOperationException("Do not format custom keys."); }
    }

    private sealed class ExplosiveValue
    {
        public override string ToString() => throw new InvalidOperationException("Do not format custom values.");
    }

    private sealed class CustomAmbientBorder : Border, IQueryAmbient
    {
        internal int AmbientCalls;
        bool IQueryAmbient.IsAmbientPropertyAvailable(string propertyName) { AmbientCalls++; throw new InvalidOperationException("Do not invoke custom ambient accessors."); }
    }

    private sealed class GuardedType(Type type) : TypeDelegator(type)
    {
        internal bool Armed;
        internal int MetadataReads;
        public override string? FullName { get { Guard(); return base.FullName; } }
        public override string Name { get { Guard(); return base.Name; } }
        private void Guard() { if (Armed) { MetadataReads++; throw new InvalidOperationException("Do not read custom type metadata."); } }
    }
}
