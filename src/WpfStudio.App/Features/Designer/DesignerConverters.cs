using System.Globalization;
using System.Windows.Data;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

/// <summary>True when a preview property's value is set on the element rather than defaulted.</summary>
public sealed class PropertyIsSetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PreviewProperty property && DesignerViewModel.IsSetHere(property);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
