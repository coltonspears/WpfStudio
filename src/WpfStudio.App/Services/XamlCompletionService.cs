using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Services;

public sealed partial class XamlCompletionService
{
    private readonly Lazy<Type[]> _types = new(() => new[] { typeof(FrameworkElement).Assembly, typeof(DependencyObject).Assembly, typeof(System.Windows.Media.Brush).Assembly }.Distinct().SelectMany(a => a.GetExportedTypes()).Where(t => !t.IsGenericType && t.IsPublic).ToArray());
    public WpfIndexSnapshot? Index { get; set; }
    [GeneratedRegex(@"<([\w:]+)([^<>]*)$")]
    private static partial Regex ElementPattern();
    [GeneratedRegex(@"(?:Path=|\{Binding\s+)([\w.]*)$")]
    private static partial Regex BindingPattern();
    [GeneratedRegex(@"(xmlns(?::[\w]+)?)\s*=\s*[""']([^""']*)$")]
    private static partial Regex NamespaceValuePattern();
    public CompletionResult Complete(string path, string text, int position, long version)
    {
        position = Math.Clamp(position, 0, text.Length);
        var prefix = text[..position];
        var start = position;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or ':' or '.')) start--;
        IEnumerable<string> words;
        var namespaceValue = NamespaceValuePattern().Match(prefix);
        var lastBrace = prefix.LastIndexOf('{');
        var lastEndBrace = prefix.LastIndexOf('}');
        if (namespaceValue.Success)
        {
            start = namespaceValue.Groups[2].Index;
            words = new[] { "http://schemas.microsoft.com/winfx/2006/xaml/presentation", "http://schemas.microsoft.com/winfx/2006/xaml", "http://schemas.microsoft.com/expression/blend/2008", "http://schemas.openxmlformats.org/markup-compatibility/2006" }
                .Concat(Index?.Texts.Where(p => p.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).SelectMany(p => Regex.Matches(p.Value, @"\bnamespace\s+([\w.]+)").Select(m => "clr-namespace:" + m.Groups[1].Value)) ?? []);
        }
        else if (lastBrace > lastEndBrace && (prefix[lastBrace..].Contains("StaticResource") || prefix[lastBrace..].Contains("DynamicResource")))
            words = Index?.Resources.Select(r => r.Key) ?? [];
        else if (BindingPattern().IsMatch(prefix))
        {
            var binding = BindingPattern().Match(prefix).Groups[1];
            var lastDot = binding.Value.LastIndexOf('.');
            start = binding.Index + lastDot + 1;
            words = BindingProperties(prefix, lastDot < 0 ? "" : binding.Value[..lastDot]);
        }
        else
        {
            var element = ElementPattern().Match(prefix);
            if (element.Success && element.Groups[2].Length > 0)
            {
                var name = element.Groups[1].Value.Split(':').Last();
                var type = _types.Value.FirstOrDefault(t => t.Name == name);
                words = (type?.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name) ?? []).Concat(new[] { "x:Name", "x:Key", "x:Class", "xmlns", "xmlns:x", "xmlns:d", "xmlns:mc", "mc:Ignorable", "d:DataContext", "Grid.Row", "Grid.Column", "DockPanel.Dock", "Canvas.Left", "Canvas.Top" });
            }
            else words = _types.Value.Select(t => t.Name).Concat(CustomTypes(text)).Concat(new[] { "ResourceDictionary", "ResourceDictionary.MergedDictionaries", "Grid.RowDefinitions", "Grid.ColumnDefinitions", "Application.Resources", "UserControl.Resources", "Window.Resources" });
        }
        var fragment = text[start..position];
        return new(version, start, position - start, words.Distinct().Where(w => w.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)).Order().Take(250).Select(w => new CompletionEntry(w, w, w, "WPF / XAML", [])).ToArray());
    }
    private IEnumerable<string> CustomTypes(string xaml)
    {
        if (Index == null) yield break;
        var mappings = Regex.Matches(xaml, @"xmlns:(\w+)\s*=\s*[""']clr-namespace:([\w.]+)(?:;assembly=[^""']*)?[""']");
        foreach (Match mapping in mappings)
        foreach (var item in Index.Items.Where(i => i.Kind is "UserControl" or "Window" or "ViewModel" or "Converter"))
        {
            var namespaceName = mapping.Groups[2].Value;
            if (item.Name.StartsWith(namespaceName + ".", StringComparison.Ordinal) && !item.Name[(namespaceName.Length + 1)..].Contains('.'))
                yield return mapping.Groups[1].Value + ":" + item.Name[(namespaceName.Length + 1)..];
            else if (!item.Name.Contains('.') && Index.Texts.TryGetValue(item.Path, out var source) && Regex.IsMatch(source, @"\bnamespace\s+" + Regex.Escape(namespaceName) + @"\b"))
                yield return mapping.Groups[1].Value + ":" + item.Name;
        }
    }
    private IEnumerable<string> BindingProperties(string xaml, string memberPath)
    {
        if (Index == null) return [];
        var context = Regex.Match(xaml, @"d:DataContext\s*=\s*[""']\{d:DesignInstance\s+(?:Type\s*=\s*)?(?<type>(?:\w+:)?\w+)");
        if (!context.Success)
            context = Regex.Match(xaml, @"<\w+\.DataContext\s*>\s*<(?<type>\w+:\w+)\b");
        if (!context.Success) return [];
        var typeName = context.Groups["type"].Value;
        var parts = typeName.Split(':');
        string? sourceNamespace = null;
        if (parts.Length == 2)
        {
            var mapping = Regex.Match(xaml, @"xmlns:" + Regex.Escape(parts[0]) + @"\s*=\s*[""']clr-namespace:([\w.]+)");
            if (!mapping.Success) return [];
            sourceNamespace = mapping.Groups[1].Value;
        }
        var source = FindType(parts[^1], sourceNamespace);
        foreach (var segment in memberPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (source == null) return [];
            var property = Members(source).FirstOrDefault(m => m.Name == segment);
            if (property == default) return [];
            source = FindType(property.Type.TrimEnd('?'), sourceNamespace);
        }
        return source == null ? [] : Members(source).Select(m => m.Name);
    }
    private string? FindType(string typeName, string? sourceNamespace)
    {
        if (Index == null) return null;
        // Ambiguous names are intentionally omitted rather than guessed across projects.
        var candidates = Index.Texts.Where(p => p.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value).Where(source => Regex.IsMatch(source, @"\b(?:class|record)\s+" + Regex.Escape(typeName) + @"\b")
                && (sourceNamespace == null || Regex.IsMatch(source, @"\bnamespace\s+" + Regex.Escape(sourceNamespace) + @"\s*[;{]"))).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
    private static IEnumerable<(string Name, string Type)> Members(string source)
    {
        foreach (Match property in Regex.Matches(source, @"\bpublic\s+(?:(?:partial|virtual|override|required|new)\s+)*(?<type>[\w.<>?]+)\s+(?<name>\w+)\s*(?:\{|=>)"))
            yield return (property.Groups["name"].Value, property.Groups["type"].Value);
        foreach (Match field in Regex.Matches(source, @"\[(?:CommunityToolkit\.Mvvm\.ComponentModel\.)?ObservableProperty(?:Attribute)?(?:\([^\]]*\))?\]\s*(?:private|protected|internal)\s+(?<type>[\w.<>?]+)\s+(?<name>\w+)\s*[;=]"))
        {
            var name = field.Groups["name"].Value;
            name = name.StartsWith("m_", StringComparison.Ordinal) ? name[2..] : name.TrimStart('_');
            if (name.Length > 0) yield return (char.ToUpperInvariant(name[0]) + name[1..], field.Groups["type"].Value);
        }
        foreach (Match command in Regex.Matches(source, @"\[(?:CommunityToolkit\.Mvvm\.Input\.)?RelayCommand(?:Attribute)?[^\]]*\]\s*(?:private|public|protected|internal)\s+(?:async\s+)?[\w.<>]+\s+(?<name>\w+)"))
            yield return (Regex.Replace(command.Groups["name"].Value, "Async$", "") + "Command", "ICommand");
    }
}
