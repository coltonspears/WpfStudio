using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfStudio.App.Features.DevOps;

public partial class PipelinesView : UserControl
{
    public PipelinesView() => InitializeComponent();

    /// <summary>Visible when the list on screen (runs, pipelines or approvals, given as pairs of shown and count) is empty.</summary>
    public static IMultiValueConverter EmptyListVisibility { get; } = new EmptyListConverter();

    private sealed class EmptyListConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            for (var index = 0; index + 1 < values.Length; index += 2)
                if (values[index] is true) return values[index + 1] is 0 ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
