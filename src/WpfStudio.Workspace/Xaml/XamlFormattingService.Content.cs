using Microsoft.CodeAnalysis;
using static WpfStudio.Workspace.Xaml.XamlSyntax;

namespace WpfStudio.Workspace.Xaml;

public sealed partial class XamlFormattingService
{
    private enum ContentPolicy { Structural, Preserve, Opaque }

    // This deliberately excludes text/inline content models. In particular, adding a
    // newline between adjacent Run/Span children can create a visible space even when
    // xml:space is default (InlineCollection is whitespace-significant).
    private static readonly Dictionary<string, string> StructuralTypes = KnownTypes();
    private static readonly HashSet<string> OfflinePropertyOwners = new(StructuralTypes.Keys.Concat([
        "TextBlock", "TextBox", "Run", "Span", "Bold", "Italic", "Underline", "Hyperlink", "Paragraph", "FlowDocument", "Section"
    ]), StringComparer.Ordinal);
    private static readonly HashSet<string> OfflineProperties = new([
        "Resources", "MergedDictionaries", "RowDefinitions", "ColumnDefinitions", "Children", "Setters", "Triggers",
        "EnterActions", "ExitActions", "Bindings", "Items", "VisualTree", "Content", "Child", "Template", "DataContext",
        "Style", "ItemTemplate", "ContentTemplate", "Header", "HeaderTemplate", "Blocks"
    ], StringComparer.Ordinal);

    private static ContentPolicy ContentPolicyFor(Element element, SchemaTypeResolver? resolver, bool useKnownFrameworkContent)
    {
        if (element.Namespace == Language) return ContentPolicy.Opaque;
        int dot = element.LocalName.LastIndexOf('.');
        if (resolver is null)
        {
            if (!useKnownFrameworkContent) return ContentPolicy.Opaque;
            if (element.Namespace != Presentation) return ContentPolicy.Opaque;
            if (dot >= 0)
                return OfflinePropertyOwners.Contains(element.LocalName[..dot]) && OfflineProperties.Contains(element.LocalName[(dot + 1)..])
                    ? ContentPolicy.Structural : ContentPolicy.Opaque;
            return StructuralTypes.ContainsKey(element.LocalName) ? ContentPolicy.Structural
                : OfflinePropertyOwners.Contains(element.LocalName) ? ContentPolicy.Preserve : ContentPolicy.Opaque;
        }

        // A supplied compilation is authoritative, including ambiguous namespace mappings
        // and missing framework references. Never fall back to the offline name table here.
        if (dot < 0)
        {
            var type = resolver.Resolve(element, element.Name);
            if (type is null || !CompleteType(type) || RawContent(type)) return ContentPolicy.Opaque;
            if (WhitespaceSignificant(type)) return ContentPolicy.Preserve;
            if (FrameworkType(type) && StructuralTypes.TryGetValue(type.Name, out var expected) && type.ToDisplayString() == expected)
                return ContentPolicy.Structural;
            if (HasConverter(type) || CustomAddChild(type)) return ContentPolicy.Opaque;
            for (var current = type; current is not null; current = current.BaseType)
            {
                var content = current.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Windows.Markup.ContentPropertyAttribute");
                if (content is null) continue;
                if (content.ConstructorArguments.FirstOrDefault().Value is not string name || string.IsNullOrWhiteSpace(name)) return ContentPolicy.Opaque;
                return PropertyPolicy(SchemaMembers.Find(type, name));
            }
            if (CollectionType(type)) return ContentPolicy.Structural;
            return FrameworkType(type) ? ContentPolicy.Preserve : ContentPolicy.Opaque;
        }
        int qualifiedDot = element.Name.LastIndexOf('.');
        var owner = resolver.Resolve(element, element.Name[..qualifiedDot]);
        if (owner is null || !CompleteType(owner) || RawContent(owner) || HasConverter(owner)) return ContentPolicy.Opaque;
        var member = SchemaMembers.Find(owner, element.Name[(qualifiedDot + 1)..], attached: true);
        return PropertyPolicy(member);
    }

    private static ContentPolicy PropertyPolicy(SchemaMember? member)
    {
        if (member is null || member.IsEvent || HasConverter(member.Symbol) || member.ValueType is not INamedTypeSymbol valueType
            || !CompleteType(valueType) || RawContent(valueType) || HasConverter(valueType)) return ContentPolicy.Opaque;
        if (valueType.SpecialType is SpecialType.System_String or SpecialType.System_Char || WhitespaceSignificant(valueType)) return ContentPolicy.Preserve;
        // A declared object/element property or ordinary collection can contain object
        // children. Actual text or CDATA is separately detected before changing any gap.
        return ContentPolicy.Structural;
    }

    private static bool CompleteType(INamedTypeSymbol type) => SchemaMembers.IsComplete(type)
        && !type.TypeArguments.Any(argument => argument.TypeKind == TypeKind.Error || argument is INamedTypeSymbol named && !SchemaMembers.IsComplete(named));

    private static bool CollectionType(INamedTypeSymbol type) => type.AllInterfaces.Prepend(type).Any(candidate =>
        candidate.ToDisplayString() is "System.Collections.IList" or "System.Collections.IDictionary"
        || candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_ICollection_T
        || candidate.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IDictionary<TKey, TValue>");

    private static bool RawContent(INamedTypeSymbol type)
    {
        if (type.AllInterfaces.Any(@interface => @interface.ToDisplayString() is "System.Xml.Serialization.IXmlSerializable" or "System.Windows.Markup.IXamlSerializable")) return true;
        for (var current = type; current is not null; current = current.BaseType)
            if (current.ToDisplayString() is "System.Windows.Markup.XData" or "System.Xml.XmlNode" or "System.Xml.Linq.XNode") return true;
        return false;
    }

    private static bool HasConverter(ISymbol symbol)
    {
        for (ISymbol? current = symbol; current is not null; current = (current as INamedTypeSymbol)?.BaseType)
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString()
                is "System.ComponentModel.TypeConverterAttribute" or "System.Windows.Markup.ValueSerializerAttribute")) return true;
        return false;
    }

    private static bool CustomAddChild(INamedTypeSymbol type)
    {
        var contract = type.AllInterfaces.FirstOrDefault(@interface => @interface.ToDisplayString() == "System.Windows.Markup.IAddChild");
        return contract is not null && contract.GetMembers().Any(member => type.FindImplementationForInterfaceMember(member)?.ContainingType is { } implementation && !FrameworkType(implementation));
    }

    private static bool FrameworkType(INamedTypeSymbol type) => SchemaMembers.IsComplete(type)
        && type.ContainingAssembly.Identity.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase";

    private static bool WhitespaceSignificant(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Windows.Markup.WhitespaceSignificantCollectionAttribute")) return true;
        return false;
    }

    private static Dictionary<string, string> KnownTypes()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string ns, string names)
        { foreach (string name in names.Split(' ')) result[name] = ns + "." + name; }
        Add("System.Windows", "Window ResourceDictionary Style Setter Trigger MultiTrigger DataTrigger MultiDataTrigger EventTrigger Condition DataTemplate HierarchicalDataTemplate");
        Add("System.Windows.Controls", "Grid StackPanel Canvas WrapPanel DockPanel Border Viewbox ScrollViewer ContentControl ContentPresenter ItemsControl ItemsPresenter UserControl Button CheckBox RadioButton GroupBox Expander TabControl TabItem ListBox ListBoxItem ListView ListViewItem ComboBox ComboBoxItem Menu MenuItem ContextMenu TreeView TreeViewItem ToolBar StatusBar ToolTip ControlTemplate");
        Add("System.Windows.Controls.Primitives", "UniformGrid ToggleButton RepeatButton Popup StatusBarItem ToolBarPanel TabPanel BulletDecorator");
        Add("System.Windows.Media", "DrawingGroup TransformGroup GeometryGroup");
        Add("System.Windows.Media.Animation", "Storyboard DoubleAnimationUsingKeyFrames ColorAnimationUsingKeyFrames ObjectAnimationUsingKeyFrames PointAnimationUsingKeyFrames");
        Add("System.Windows.Data", "Binding MultiBinding PriorityBinding");
        return result;
    }
}
