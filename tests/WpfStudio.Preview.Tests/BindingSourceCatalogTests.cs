using System.Windows.Controls;
using System.Windows.Data;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Wpf.Diagnostics;

namespace WpfStudio.Preview.Tests;

[Collection("WPF preview")]
public sealed class BindingSourceCatalogTests(PreviewFixture fixture)
{
    [Fact]
    public void CaptureRetainsActualIdentityAndDoesNotEvaluateCachedSourceAgain() => fixture.OnDispatcher(() =>
    {
        var source = new CountedSource();
        var target = new TextBlock();
        var expression = target.SetBinding(TextBlock.TextProperty, new Binding(nameof(CountedSource.Value)) { Source = source });
        int before = source.GetterCalls;
        var catalog = new BindingSourceCatalog();
        var captured = catalog.Capture(expression, _ => "known-expression-id");
        var again = catalog.Capture(expression);
        Assert.Equal("known-expression-id", captured.BindingId);
        Assert.Equal(captured.BindingId, again.BindingId);
        var declaration = Assert.Single(captured.Declarations);
        Assert.Equal(declaration, Assert.Single(again.Declarations));
        Assert.Null(declaration.Source);
        var request = new BindingSourceRequest(1, "node", "Text", captured.BindingId,
            declaration.ExpressionId, declaration.DeclarationId);
        var unavailable = catalog.Validate(expression, request);
        Assert.False(unavailable.Available); // Code-created declaration has no loader hint.
        Assert.Equal(request, unavailable.Request);
        Assert.Equal(before, source.GetterCalls);
        return true;
    });

    [Fact]
    public void CompositeRowsAndAggregateCharactersAreIndependentlyBounded() => fixture.OnDispatcher(() =>
    {
        var target = new TextBlock();
        var multi = new MultiBinding { StringFormat = "{0}" };
        for (int i = 0; i < 100; i++) multi.Bindings.Add(new Binding { Source = "value" });
        var expression = target.SetBinding(TextBlock.TextProperty, multi);
        var catalog = new BindingSourceCatalog();
        var countLimited = catalog.Capture(expression, maximumDeclarations: 3);
        Assert.True(countLimited.Truncated);
        Assert.Equal(3, countLimited.Declarations.Count);
        Assert.Equal(countLimited.BindingId, countLimited.Declarations[1].ParentExpressionId);
        Assert.Equal(0, countLimited.Declarations[1].ChildIndex);
        Assert.Equal(1, countLimited.Declarations[2].ChildIndex);
        var textLimited = catalog.Capture(expression, maximumCharacters: 1024);
        Assert.True(textLimited.Truncated);
        Assert.InRange(BindingSourceCatalog.GetCharacterCount(textLimited), 0, 1024);
        var omitted = catalog.Capture(expression, maximumDeclarations: 0, maximumCharacters: 0);
        Assert.Empty(omitted.Declarations);
        Assert.True(omitted.Truncated);
        Assert.Equal(countLimited.BindingId, omitted.BindingId);
        return true;
    });

    [Fact]
    public void OversizedPathIsOmittedInsteadOfTruncatedIntoAnotherDeclaration() => fixture.OnDispatcher(() =>
    {
        var target = new TextBlock();
        var expression = target.SetBinding(TextBlock.TextProperty, new Binding(new string('x', 2049)) { Source = new object() });
        var declaration = Assert.Single(new BindingSourceCatalog().Capture(expression).Declarations);
        Assert.Null(declaration.Path);
        Assert.Null(declaration.Source);
        Assert.Contains("omitted", declaration.UnavailableReason!);
        return true;
    });

    public sealed class CountedSource
    {
        public int GetterCalls;
        public string Value { get { GetterCalls++; return "Observed"; } }
    }
}
