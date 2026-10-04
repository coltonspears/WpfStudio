using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Overview layout only: KPI tiles wrap to the available width, and the two dashboard columns stack in narrow
/// docked layouts.</summary>
public partial class MemoryOverviewView : UserControl
{
    private UniformGrid? _kpis;
    private bool? _wide;

    public MemoryOverviewView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    private void KpiGridLoaded(object sender, RoutedEventArgs e) { _kpis = (UniformGrid)sender; Arrange(ActualWidth); }

    private void Arrange(double width)
    {
        if (width <= 0) return;
        if (_kpis is not null) { _kpis.Rows = 0; _kpis.Columns = width >= 760 ? 5 : width >= 470 ? 3 : 2; }
        var wide = width >= 920;
        if (wide == _wide) return;
        _wide = wide;
        MainGrid.ColumnDefinitions[1].Width = wide ? new GridLength(6, GridUnitType.Star) : new GridLength(0);
        Place(FindingsPanel, 0, 0); Place(CompositionPanel, wide ? 0 : 1, wide ? 1 : 0);
        Place(RetainersPanel, wide ? 1 : 2, 0); Place(GrowthPanel, wide ? 1 : 3, wide ? 1 : 0);
        static void Place(UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); }
    }
}
