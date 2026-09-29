using System.Globalization;
using System.Text;
using System.Xml.Linq;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;

namespace WpfStudio.Core.Tests;

public sealed class XamlLayoutEditTests
{
    private const string PathName = @"C:\Source\Layout.xaml";
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string Language = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(XamlLayoutHandle.TopLeft, 60, 65, 70, 45)]
    [InlineData(XamlLayoutHandle.Top, 50, 65, 80, 45)]
    [InlineData(XamlLayoutHandle.TopRight, 50, 65, 90, 45)]
    [InlineData(XamlLayoutHandle.Right, 50, 60, 90, 50)]
    [InlineData(XamlLayoutHandle.BottomRight, 50, 60, 90, 55)]
    [InlineData(XamlLayoutHandle.Bottom, 50, 60, 80, 55)]
    [InlineData(XamlLayoutHandle.BottomLeft, 60, 60, 70, 55)]
    [InlineData(XamlLayoutHandle.Left, 60, 60, 70, 50)]
    [InlineData(XamlLayoutHandle.Move, 60, 65, 80, 50)]
    public void EightHandlesAndMoveKeepTheirOppositeAnchors(XamlLayoutHandle handle, double x, double y, double width, double height)
    {
        var result = XamlLayoutEditService.Calculate(Context(), new(handle, 10, 5, BypassSnapping: true));
        Assert.True(result.Success, result.Error);
        Assert.Equal(new PreviewBounds(x, y, width, height), result.Bounds);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void ResizeClampsToObservedDimensionsAndNeverFlipsTheElement()
    {
        var context = Context() with { MinWidth = 30, MaxWidth = 100, MinHeight = 20, MaxHeight = 60 };
        var minimum = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.TopLeft, 500, 500, BypassSnapping: true));
        Assert.Equal(new PreviewBounds(100, 90, 30, 20), minimum.Bounds);
        var maximum = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.BottomRight, 500, 500, BypassSnapping: true));
        Assert.Equal(new PreviewBounds(50, 60, 100, 60), maximum.Bounds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1_000_001)]
    public void InvalidGestureCoordinatesAreRejected(double value)
    {
        Assert.False(XamlLayoutEditService.Calculate(Context(), new(XamlLayoutHandle.Move, value, 1)).Success);
    }

    [Fact]
    public void CanvasMoveAndResizePreserveRightBottomAnchorsAndMargins()
    {
        var context = Context() with { CanvasLeft = null, CanvasTop = null, CanvasRight = 20, CanvasBottom = 30 };
        var move = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Move, 10, 5, BypassSnapping: true));
        Assert.Equal(10, move.Values!.CanvasRight);
        Assert.Equal(25, move.Values.CanvasBottom);
        Assert.Null(move.Values.CanvasLeft); Assert.Null(move.Values.CanvasTop); Assert.Null(move.Values.Margin);
        Assert.Null(move.Values.Width); Assert.Null(move.Values.Height);
        var east = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.BottomRight, 10, 5, BypassSnapping: true));
        Assert.Equal(90, east.Values!.Width); Assert.Equal(55, east.Values.Height);
        Assert.Equal(10, east.Values.CanvasRight); Assert.Equal(25, east.Values.CanvasBottom);
        var west = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.TopLeft, 10, 5, BypassSnapping: true));
        Assert.Null(west.Values!.CanvasRight); Assert.Null(west.Values.CanvasBottom);
        Assert.Equal(70, west.Values.Width); Assert.Equal(45, west.Values.Height);
    }

    [Fact]
    public void CanvasLeadingAnchorsWinWhenBothOpposingValuesExist()
    {
        var context = Context() with { CanvasLeft = 10, CanvasTop = 20, CanvasRight = 30, CanvasBottom = 40 };
        var result = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Move, 7, -3, BypassSnapping: true));
        Assert.Equal(17, result.Values!.CanvasLeft); Assert.Equal(17, result.Values.CanvasTop);
        Assert.Null(result.Values.CanvasRight); Assert.Null(result.Values.CanvasBottom);
    }

    [Theory]
    [InlineData("Left", "Top")]
    [InlineData("Center", "Center")]
    [InlineData("Right", "Bottom")]
    [InlineData("Stretch", "Stretch")]
    public void GridMoveKeepsAlignmentAutoDimensionsAndCellWhileAdjustingOppositeMargins(string horizontal, string vertical)
    {
        var context = Context("Grid") with { Margin = new(10, 20, 30, 40), HorizontalAlignment = horizontal, VerticalAlignment = vertical,
            GridRow = 2, GridColumn = 3, GridRowSpan = 2, GridColumnSpan = 3 };
        var result = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Move, 12, -4, BypassSnapping: true));
        Assert.Equal(new PreviewLayoutInsets(22, 16, 18, 44), result.Values!.Margin);
        Assert.Null(result.Values.Width); Assert.Null(result.Values.Height);
        Assert.Null(result.Values.HorizontalAlignment); Assert.Null(result.Values.VerticalAlignment);
        Assert.Null(result.Values.GridRow); Assert.Null(result.Values.GridColumn);
        Assert.Null(result.Values.GridRowSpan); Assert.Null(result.Values.GridColumnSpan);
    }

    [Fact]
    public void GridResizeWritesOnlyChangedResizedAxes()
    {
        var context = Context("Grid") with { Margin = new(10, 20, 30, 40), MaxWidth = 80 };
        var result = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.BottomRight, 20, 15, BypassSnapping: true));
        Assert.Null(result.Values!.Width); Assert.Equal(65, result.Values.Height);
        Assert.Equal(new PreviewLayoutInsets(10, 20, 30, 25), result.Values.Margin);
    }

    [Fact]
    public void SnappingUsesScreenToleranceAndCanBeBypassed()
    {
        var context = Context() with { Siblings = [new("sibling", new(142, 200, 40, 20))] };
        var snapped = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Move, 10, 0));
        Assert.Equal(62, snapped.Bounds!.X); // right edge 140 snaps to sibling left 142
        Assert.Contains(snapped.Guides, guide => guide.Vertical && guide.Position == 142);
        var bypassed = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Move, 10, 0, BypassSnapping: true));
        Assert.Equal(60, bypassed.Bounds!.X); Assert.Empty(bypassed.Guides);
        var zoomContext = context with { Siblings = [new("sibling", new(144, 200, 40, 20))] };
        Assert.Equal(64, XamlLayoutEditService.Calculate(zoomContext, new(XamlLayoutHandle.Move, 10, 0, Zoom: 1)).Bounds!.X);
        Assert.Equal(60, XamlLayoutEditService.Calculate(zoomContext, new(XamlLayoutHandle.Move, 10, 0, Zoom: 2)).Bounds!.X);
    }

    [Fact]
    public void SnappingIncludesParentSlotAndSiblingCentersWithDeterministicTies()
    {
        var context = Context() with { ParentBounds = new(0, 0, 400, 400), SlotBounds = new(30, 30, 180, 150) };
        var slot = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Move, -17, 0));
        Assert.Equal(30, slot.Bounds!.X);
        var parent = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Move, -47, 0));
        Assert.Equal(0, parent.Bounds!.X);
        var siblings = context with { SlotBounds = new(0, 0, 400, 400), Siblings = [new("B", new(144, 170, 0, 10)), new("A", new(136, 170, 0, 10))] };
        var first = XamlLayoutEditService.Calculate(siblings, new(XamlLayoutHandle.Move, 10, 0));
        var second = XamlLayoutEditService.Calculate(siblings with { Siblings = siblings.Siblings!.Reverse().ToArray() }, new(XamlLayoutHandle.Move, 10, 0));
        Assert.Equal(first.Bounds, second.Bounds); Assert.Equal(56, first.Bounds!.X);
        var center = XamlLayoutEditService.Calculate(Context() with { Siblings = [new("S", new(93, 200, 20, 20))] }, new(XamlLayoutHandle.Move, 10, 0));
        Assert.Equal(63, center.Bounds!.X); // selected center 100 snaps to sibling center 103
    }

    [Fact]
    public void ResizeSnappingNeverViolatesMinimumOrMovesTheFixedEdge()
    {
        var context = Context() with { MinWidth = 75, Siblings = [new("S", new(58, 200, 20, 20))] };
        var result = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Left, 4, 0));
        Assert.Equal(54, result.Bounds!.X); Assert.Equal(76, result.Bounds.Width);
        Assert.Equal(130, result.Bounds.X + result.Bounds.Width);
        Assert.DoesNotContain(result.Guides, guide => guide.Vertical && guide.Position == 58);
    }

    [Fact]
    public void CanvasSourceProposalKeepsUnrelatedBytesAndExistingTrailingAnchors()
    {
        string text = Wrap("Canvas", "<!--before--><Border Tag='keep &amp; intact' Canvas.Right='20' Canvas.Bottom='30' Margin='1,2,3,4'/><!--after-->");
        var context = Context("Canvas", text) with { CanvasLeft = null, CanvasTop = null, CanvasRight = 20, CanvasBottom = 30 };
        var result = Plan(text, context, XamlLayoutHandle.Move, 10, 5);
        Assert.Equal(text.Replace("Canvas.Right='20'", "Canvas.Right='10'").Replace("Canvas.Bottom='30'", "Canvas.Bottom='25'"), Apply(text, result));
        Assert.Equal(2, result.Edit!.Edits.Count);
    }

    [Fact]
    public void GridMultiPropertyResizeCoalescesInsertionsIntoOneAtomicDocumentEdit()
    {
        string text = Wrap("Grid", "<Border\r\n    Tag='unchanged'\r\n    Grid.Row='2' HorizontalAlignment='Right'\r\n    />");
        var context = Context("Grid", text);
        var result = Plan(text, context, XamlLayoutHandle.BottomRight, 10, 5);
        string after = Apply(text, result);
        var border = XDocument.Parse(after).Descendants().Single(element => element.Name.LocalName == "Border");
        Assert.Equal("90", border.Attribute("Width")!.Value); Assert.Equal("55", border.Attribute("Height")!.Value);
        Assert.Equal("0,0,-10,-5", border.Attribute("Margin")!.Value);
        Assert.Contains("Grid.Row='2' HorizontalAlignment='Right'", after);
        Assert.Contains("Tag='unchanged'", after);
        Assert.Single(result.Edit!.Edits); // Width, Height and Margin share one insertion point.
        Assert.Equal(7, result.Edit.Version);
        Assert.Equal(Hash(text), result.Edit.ExpectedTextHash);
    }

    [Theory]
    [InlineData("{Binding Offset}")]
    [InlineData("{DynamicResource Position}")]
    [InlineData("{StaticResource Position}")]
    public void ProtectedAuthoredExpressionsRequireExplicitReplacement(string expression)
    {
        string text = Wrap("Canvas", $"<Border Canvas.Left='{expression}' Canvas.Top='0'/>");
        var context = Context("Canvas", text);
        var blocked = Plan(text, context, XamlLayoutHandle.Move, 10, 5);
        Assert.False(blocked.Success); Assert.Null(blocked.Edit); Assert.True(blocked.ReplacesExpression);
        var accepted = Plan(text, context, XamlLayoutHandle.Move, 10, 5, replace: true);
        Assert.True(accepted.ReplacesExpression);
        Assert.DoesNotContain(expression, Apply(text, accepted));
    }

    [Fact]
    public void ObservedStyleBindingIsProtectedEvenWithoutALocalSourceDeclaration()
    {
        string text = Wrap("Grid", "<Border/>");
        var context = Context("Grid", text);
        context = context with { EditProperties = context.EditProperties!.Select(property => property.Name == "Margin"
            ? property with { IsExpression = true, ValueSource = "Style" } : property).ToArray() };
        var blocked = Plan(text, context, XamlLayoutHandle.Move, 10, 5);
        Assert.False(blocked.Success); Assert.True(blocked.ReplacesExpression);
        var accepted = Plan(text, context, XamlLayoutHandle.Move, 10, 5, replace: true);
        Assert.Contains("override", accepted.Explanation);
        Assert.Contains("Margin=", Apply(text, accepted));
    }

    [Fact]
    public void PropertyElementLiteralReplacementPreservesCommentsAndContent()
    {
        string text = Wrap("Grid", "<Border><Border.Width><!--keep-->80</Border.Width><TextBlock Text='keep'/></Border>");
        var context = Context("Grid", text);
        string after = Apply(text, Plan(text, context, XamlLayoutHandle.Right, 10, 0));
        Assert.Contains("<!--keep-->", after); Assert.Contains("<TextBlock Text='keep'/>", after);
        Assert.DoesNotContain("Border.Width", after); Assert.Contains("Width=", after);
    }

    [Fact]
    public void ExplicitPanelNamespaceAliasIsUsedForAttachedPropertyInsertion()
    {
        string text = $"<p:Canvas xmlns:p='{Presentation}' xmlns='{Presentation}'><Border/></p:Canvas>";
        var context = Context("Canvas", text, parentTag: "p:Canvas");
        string after = Apply(text, Plan(text, context, XamlLayoutHandle.Move, 10, 5));
        Assert.Contains("Canvas.Left=", after); Assert.Contains("Canvas.Top=", after);
        Assert.StartsWith("<p:Canvas", after);
    }

    [Fact]
    public void ASharedTemplateGestureCarriesTheEveryInstanceWarning()
    {
        string text = $"<ControlTemplate xmlns='{Presentation}' xmlns:x='{Language}'><Grid><Border/></Grid></ControlTemplate>";
        var result = Plan(text, Context("Grid", text), XamlLayoutHandle.Move, 10, 5);
        Assert.True(result.AffectsTemplate); Assert.Contains("every instance", result.Explanation);
    }

    [Fact]
    public void StaleSourceParentAndCanonicalPropertyIdentityAreRejected()
    {
        string text = Wrap("Canvas", "<Border/>");
        var context = Context("Canvas", text);
        Assert.False(Plan(text + " ", context, XamlLayoutHandle.Move, 10, 5).Success);
        Assert.False(Plan(text, context with { SourceHash = "stale" }, XamlLayoutHandle.Move, 10, 5).Success);
        Assert.False(Plan(text, context with { Parent = context.Element }, XamlLayoutHandle.Move, 10, 5).Success);
        Assert.False(Plan(text, context with { EditProperties = context.EditProperties!.Select(property => property with { OwnerAssembly = "UserAssembly" }).ToArray() }, XamlLayoutHandle.Move, 10, 5).Success);
        Assert.False(Plan(text, context with { EditProperties = context.EditProperties!.Concat(context.EditProperties!).ToArray() }, XamlLayoutHandle.Move, 10, 5).Success);
        var gesture = XamlLayoutEditService.Calculate(context, new(XamlLayoutHandle.Move, 10, 5, BypassSnapping: true));
        Assert.False(XamlLayoutEditService.CreateEdit(new(PathName, text, 8, 7, Hash(text), context, gesture.Bounds!, XamlLayoutHandle.Move)).Success);
    }

    [Fact]
    public void ForgedAxisChangesAndUnavailableMetadataProduceNoPartialEdits()
    {
        string text = Wrap("Grid", "<Border/>");
        var context = Context("Grid", text);
        var forged = XamlLayoutEditService.CreateEdit(new(PathName, text, 7, 7, Hash(text), context,
            context.Bounds! with { Width = 100 }, XamlLayoutHandle.Move));
        Assert.False(forged.Success);
        var missingMargin = context with { EditProperties = context.EditProperties!.Where(property => property.Name != "Margin").ToArray() };
        var rejected = Plan(text, missingMargin, XamlLayoutHandle.BottomRight, 10, 5);
        Assert.False(rejected.Success); Assert.Null(rejected.Edit);
    }

    [Fact]
    public void NumericSourceUsesInvariantCultureAndNoGestureDoesNotMaterializeAutoDimensions()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string text = Wrap("Canvas", "<Border/>");
            var context = Context("Canvas", text);
            string after = Apply(text, Plan(text, context, XamlLayoutHandle.BottomRight, 10.25, 5.5));
            Assert.Contains("90.25", after); Assert.Contains("55.5", after);
            var unchanged = Plan(text, context, XamlLayoutHandle.BottomRight, 0, 0);
            Assert.Empty(unchanged.Edit!.Edits); Assert.Equal(text, Apply(text, unchanged));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static XamlPropertyEditResult Plan(string text, PreviewLayoutEditContext context, XamlLayoutHandle handle, double dx, double dy, bool replace = false)
    {
        var gesture = XamlLayoutEditService.Calculate(context, new(handle, dx, dy, BypassSnapping: true));
        Assert.True(gesture.Success, gesture.Error);
        return XamlLayoutEditService.CreateEdit(new(PathName, text, 7, 7, Hash(text), context, gesture.Bounds!, handle, replace));
    }
    private static string Apply(string text, XamlPropertyEditResult result)
    {
        Assert.True(result.Success, result.Error);
        return WorkspaceEditTransaction.ApplyTextEdits(text, result.Edit!.Edits);
    }
    private static string Hash(string text) => DocumentStore.Hash(Encoding.UTF8.GetBytes(text));
    private static string Wrap(string parent, string child) => $"<{parent} xmlns='{Presentation}' xmlns:x='{Language}'>{child}</{parent}>";
    private static PreviewLayoutEditContext Context(string kind = "Canvas", string? text = null, string? parentTag = null)
    {
        text ??= Wrap(kind, "<Border/>");
        var properties = new List<PreviewProperty>();
        foreach (string name in new[] { "Width", "Height", "Margin" })
            properties.Add(new(name, name == "Margin" ? "Thickness" : "Double", "", "Local", false, false, false, true,
                OwnerType: "System.Windows.FrameworkElement", OwnerAssembly: "PresentationFramework", CanWriteSource: true, ContentProperty: "Child"));
        foreach (string name in new[] { "Left", "Top", "Right", "Bottom" })
            properties.Add(new("Canvas." + name, "Double", "", "Local", false, false, false, true,
                OwnerType: "System.Windows.Controls.Canvas", OwnerAssembly: "PresentationFramework", IsAttached: true, CanWriteSource: true, ContentProperty: "Child"));
        return new(true, 99, "selected", "observation", Location(text, "Border"), Location(text, parentTag ?? kind), kind,
            new(50, 60, 80, 50), new(0, 0, 500, 400), new(0, 0, 500, 400), new(0, 0, 0, 0),
            HorizontalAlignment: "Left", VerticalAlignment: "Top", CanvasLeft: 0, CanvasTop: 0,
            EditProperties: properties, Siblings: [], SourceHash: Hash(text));
    }
    private static SourceLocation Location(string text, string tag)
    {
        int start = text.IndexOf("<" + tag, StringComparison.Ordinal), end = start;
        char quote = '\0';
        for (; end < text.Length; end++)
        {
            char value = text[end];
            if (quote != '\0') { if (value == quote) quote = '\0'; }
            else if (value is '\'' or '"') quote = value;
            else if (value == '>') { end++; break; }
        }
        int line = 1, lineStart = 0;
        for (int index = 0; index < start; index++)
        {
            if (text[index] == '\r') { if (index + 1 < start && text[index + 1] == '\n') index++; line++; lineStart = index + 1; }
            else if (text[index] == '\n') { line++; lineStart = index + 1; }
        }
        return new(PathName, start, end - start, line, start - lineStart + 1, tag);
    }
}
