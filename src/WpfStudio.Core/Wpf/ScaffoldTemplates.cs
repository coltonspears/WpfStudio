namespace WpfStudio.Core.Wpf;

/// <summary>The text of each file scaffolding creates. Lines end in \n; the caller converts them when it writes the change.</summary>
public static class ScaffoldTemplates
{
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public static string UserControlXaml(string ns, string name) =>
        $"<UserControl x:Class=\"{ns}.{name}\"\n    xmlns=\"{PresentationNamespace}\"\n    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n    xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"\n    xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"\n    mc:Ignorable=\"d\" d:DesignWidth=\"800\" d:DesignHeight=\"450\">\n    <Grid>\n    </Grid>\n</UserControl>\n";
    public static string UserControlCode(string ns, string name) =>
        $"using System.Windows.Controls;\n\nnamespace {ns};\n\npublic partial class {name} : UserControl\n{{\n    public {name}() => InitializeComponent();\n}}\n";
    public static string ViewModel(string ns, string name, bool partialProperties) =>
        $"using System.Threading;\nusing System.Threading.Tasks;\nusing CommunityToolkit.Mvvm.ComponentModel;\nusing CommunityToolkit.Mvvm.Input;\n\nnamespace {ns};\n\npublic partial class {name} : ObservableObject\n{{\n{Property("Title", partialProperties)}\n    [RelayCommand]\n    private async Task RefreshAsync(CancellationToken cancellationToken)\n    {{\n        await Task.CompletedTask;\n    }}\n}}\n";
    public static string ResourceDictionary(string name) =>
        $"<ResourceDictionary xmlns=\"{PresentationNamespace}\"\n    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n    <SolidColorBrush x:Key=\"{name}Brush\" Color=\"#407FA3\" />\n</ResourceDictionary>\n";
    public static string Converter(string ns, string name) =>
        $"using System;\nusing System.Globalization;\nusing System.Windows;\nusing System.Windows.Data;\n\nnamespace {ns};\n\npublic sealed class {name} : IValueConverter\n{{\n    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value;\n    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;\n}}\n";
    public static string DataTemplates(string ns, string viewName, string viewModelName) =>
        $"<ResourceDictionary xmlns=\"{PresentationNamespace}\"\n    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n    xmlns:local=\"clr-namespace:{ns}\">\n    <DataTemplate DataType=\"{{x:Type local:{viewModelName}}}\">\n        <local:{viewName} />\n    </DataTemplate>\n</ResourceDictionary>\n";
    /// <summary>A partial property needs C# 14 and toolkit 8.4; anything older gets the classic backing field and <c>SetProperty</c>.</summary>
    public static string Property(string name, bool partialProperties) => partialProperties
        ? $"    [ObservableProperty]\n    public partial string {name} {{ get; set; }} = string.Empty;\n"
        : $"    private string _{char.ToLowerInvariant(name[0])}{name[1..]} = string.Empty;\n    public string {name}\n    {{\n        get => _{char.ToLowerInvariant(name[0])}{name[1..]};\n        set => SetProperty(ref _{char.ToLowerInvariant(name[0])}{name[1..]}, value);\n    }}\n";
}
