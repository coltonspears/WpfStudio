namespace WpfStudio.App.Features.Designer;

/// <summary>Maps element types to the outline and canvas glyphs.</summary>
public static class DesignerElementKinds
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["Window"] = "Window", ["Page"] = "Window", ["NavigationWindow"] = "Window", ["UserControl"] = "Window",
        ["Grid"] = "Grid", ["UniformGrid"] = "Grid",
        ["StackPanel"] = "Stack", ["VirtualizingStackPanel"] = "Stack", ["WrapPanel"] = "Stack", ["DockPanel"] = "Stack", ["TabPanel"] = "Stack", ["ToolBarPanel"] = "Stack",
        ["Canvas"] = "Artboard", ["InkCanvas"] = "Artboard",
        ["TextBlock"] = "Text", ["Label"] = "Text", ["AccessText"] = "Text", ["Run"] = "Text", ["Span"] = "Text", ["Paragraph"] = "Text", ["Hyperlink"] = "Text", ["FlowDocument"] = "Text",
        ["CheckBox"] = "Toggle", ["RadioButton"] = "Toggle",
        ["TextBox"] = "Input", ["PasswordBox"] = "Input", ["RichTextBox"] = "Input", ["DatePicker"] = "Input",
        ["ListBox"] = "List", ["ListView"] = "List", ["ItemsControl"] = "List", ["ItemsPresenter"] = "List", ["TreeView"] = "List", ["DataGrid"] = "List",
        ["ComboBox"] = "List", ["TabControl"] = "List", ["Menu"] = "List", ["ContextMenu"] = "List", ["ToolBar"] = "List", ["StatusBar"] = "List",
        ["Image"] = "Image", ["MediaElement"] = "Image",
        ["Border"] = "Frame", ["Rectangle"] = "Frame", ["GroupBox"] = "Frame", ["Expander"] = "Frame", ["ScrollViewer"] = "Frame", ["Viewbox"] = "Frame",
        ["Ellipse"] = "Shape", ["Path"] = "Shape", ["Polygon"] = "Shape", ["Polyline"] = "Shape", ["Line"] = "Shape", ["Glyphs"] = "Shape",
        ["Slider"] = "Slider", ["ProgressBar"] = "Slider", ["ScrollBar"] = "Slider", ["Track"] = "Slider", ["Thumb"] = "Slider",
        ["ContentPresenter"] = "Slot", ["ContentControl"] = "Slot", ["AdornerDecorator"] = "Slot", ["AdornerLayer"] = "Slot", ["Decorator"] = "Slot",
        ["ScrollContentPresenter"] = "Slot", ["Frame"] = "Slot", ["Popup"] = "Slot"
    };

    public static string IconFor(string typeName)
    {
        if (Known.TryGetValue(typeName, out var kind)) return kind;
        if (typeName.EndsWith("Window", StringComparison.Ordinal) || typeName.EndsWith("View", StringComparison.Ordinal)
            || typeName.EndsWith("Page", StringComparison.Ordinal)) return "Window";
        if (typeName.EndsWith("Button", StringComparison.Ordinal)) return "Button";
        if (typeName.EndsWith("Grid", StringComparison.Ordinal)) return "Grid";
        if (typeName.EndsWith("Panel", StringComparison.Ordinal)) return "Stack";
        if (typeName.EndsWith("Box", StringComparison.Ordinal)) return "Input";
        if (typeName.EndsWith("Presenter", StringComparison.Ordinal) || typeName.EndsWith("Decorator", StringComparison.Ordinal)) return "Slot";
        if (typeName.EndsWith("List", StringComparison.Ordinal) || typeName.EndsWith("Items", StringComparison.Ordinal)) return "List";
        if (typeName.EndsWith("Text", StringComparison.Ordinal) || typeName.EndsWith("Block", StringComparison.Ordinal)) return "Text";
        return "Frame";
    }
}
