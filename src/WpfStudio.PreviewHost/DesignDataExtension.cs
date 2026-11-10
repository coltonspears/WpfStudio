using System.Windows;
using System.Windows.Markup;

namespace WpfStudio.PreviewHost;

/// <summary>
/// Stands in the loaded markup for a design-time data declaration (<c>d:DesignInstance</c>, <c>d:DesignData</c>,
/// <c>d:SampleData</c>). The preview document resolved the declaration while reading the source; this hands the
/// resolved value to the property being set, including each time a template that contains it is instantiated.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class DesignDataExtension : MarkupExtension
{
    public DesignDataExtension() { }
    public DesignDataExtension(string key) => Key = key;

    /// <summary>Identifies the document and the declaration within it.</summary>
    [ConstructorArgument("key")]
    public string? Key { get; set; }

    public override object? ProvideValue(IServiceProvider serviceProvider) =>
        PreviewDocument.TryGetDesignValue(Key, out object? value) ? value : DependencyProperty.UnsetValue;
}
