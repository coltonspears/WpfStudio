using System.Collections;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace WpfStudio.App.Controls;

/// <summary>Maps a file name or path to a <see cref="StudioIcon"/> kind.</summary>
public sealed class FileIconKindConverter : IValueConverter
{
    public static string KindFor(string? path)
    {
        var name = (path ?? "").Replace(" •", "").Trim();
        if (name.EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase)) return "ViewModel";
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".csproj" or ".sln" or ".slnx" => "Project",
            ".cs" => "CSharp",
            ".xaml" => "Xaml",
            ".sql" => "Sql",
            ".png" or ".jpg" or ".jpeg" or ".ico" or ".svg" or ".ttf" or ".otf" => "Asset",
            _ => "File"
        };
    }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => KindFor(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the text looks like a file name (it has an extension).</summary>
public sealed class FileNameVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Path.HasExtension((value as string ?? "").Replace(" •", "").Trim()) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Glyph kind for dock content ids: tool panes and document workbenches.</summary>
public sealed class ContentGlyphConverter : IValueConverter
{
    public static string KindFor(string? contentId) => contentId switch
    {
        "Explorer" => "Folder",
        "WpfTools" => "Layers",
        "ColtonGPT" => "Chat",
        "Output" => "Output",
        "Problems" => "Warning",
        "Search" => "Search",
        "Terminal" => "Terminal",
        "Debugger" => "Bug",
        "Database" => "Database",
        "Packages" => "Package",
        "Git" => "Git",
        "Welcome" => "Window",
        _ => ""
    };
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => KindFor(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>File name without extension, e.g. for recent workspace cards.</summary>
public sealed class FileStemConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string path ? Path.GetFileNameWithoutExtension(path) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Containing folder of a path.</summary>
public sealed class DirectoryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string path ? Path.GetDirectoryName(path) ?? "" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Upper-case extension without the dot (SLN, SLNX, CSPROJ).</summary>
public sealed class ExtensionLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string path ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant() : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible for non-zero counts, non-empty strings, true, or non-null values. Pass "Invert" to flip.</summary>
public sealed class PresenceVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool present = value switch
        {
            null => false,
            bool flag => flag,
            int count => count != 0,
            string text => text.Length > 0,
            ICollection collection => collection.Count > 0,
            _ => true
        };
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) present = !present;
        return present ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the bound value equals the parameter (string comparison).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Splits a shortcut such as "Ctrl+Shift+P" into key caps.</summary>
public sealed class KeyCapsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && text.Length > 0 ? text.Split(", ").SelectMany(chord => chord.Split('+')).Where(k => k.Length > 0).ToArray() : Array.Empty<string>();
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Friendly title or description (ConverterParameter="Detail") for scaffold kinds and similar enum values.</summary>
public sealed class ScaffoldKindTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var detail = string.Equals(parameter as string, "Detail", StringComparison.OrdinalIgnoreCase);
        var icon = string.Equals(parameter as string, "Icon", StringComparison.OrdinalIgnoreCase);
        return value?.ToString() switch
        {
            "ViewAndViewModel" => icon ? "Window" : detail ? "XAML view with code-behind and a paired ObservableObject view model" : "View + view model",
            "UserControl" => icon ? "Markup" : detail ? "Reusable XAML control with code-behind" : "User control",
            "ViewModel" => icon ? "Braces" : detail ? "ObservableObject using CommunityToolkit source generators" : "View model",
            "ResourceDictionary" => icon ? "Layers" : detail ? "Shared brushes, styles and templates" : "Resource dictionary",
            "Converter" => icon ? "Swap" : detail ? "IValueConverter for bindings" : "Value converter",
            var other => icon ? "File" : detail ? "" : other ?? ""
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Brush key lookup for diagnostic severities.</summary>
public sealed class SeverityGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var severity = value?.ToString() ?? "";
        if (string.Equals(parameter as string, "Brush", StringComparison.OrdinalIgnoreCase))
            return Application.Current?.TryFindResource(severity.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "DangerBrush" : severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "WarningBrush" : "AccentBrush") ?? DependencyProperty.UnsetValue;
        return severity.Equals("Error", StringComparison.OrdinalIgnoreCase) ? "Error" : severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Info";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
