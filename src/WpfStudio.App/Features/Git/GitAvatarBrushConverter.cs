using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WpfStudio.App.Features.Git;

/// <summary>Maps a Git record's <see cref="WpfStudio.Core.Git.GitCommit.AvatarIndex"/> to its brush, so the records stay free of WPF.</summary>
public sealed class GitAvatarBrushConverter : IValueConverter
{
    // Mid-tone avatar colours that keep white initials readable in both themes.
    private static readonly Brush[] Avatars = new[]
    {
        "#5B7FE0", "#D46F4D", "#3F9E78", "#9A6BD6", "#C08A2E", "#3B98AD", "#C95A8A", "#7A8A45"
    }.Select(hex =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return (Brush)brush;
    }).ToArray();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && index >= 0 && index < Avatars.Length ? Avatars[index] : Brushes.Gray;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
