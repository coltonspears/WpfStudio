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
    [GeneratedRegex(@"(?:Path\s*=\s*|\{Binding\s+)([\w.]*)$")]
    private static partial Regex BindingPattern();
    [GeneratedRegex(@"<Binding\b[^<>]*\bPath\s*=\s*[""']([^""']*)$")]
    private static partial Regex BindingElementPattern();
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
        else if (BindingPattern().IsMatch(prefix) || BindingElementPattern().IsMatch(prefix) || IsWithinBinding(prefix))
        {
            var binding = BindingPattern().Match(prefix).Groups[1];
            if (binding.Success) start = binding.Index + binding.Value.LastIndexOf('.') + 1;
            else
            {
                var value = BindingElementPattern().Match(prefix).Groups[1];
                if (value.Success) start = value.Index + value.Value.LastIndexOf('.') + 1;
            }
            // Binding members require a resolved Roslyn source type from the worker.
            words = [];
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
    private static bool IsWithinBinding(string prefix)
    {
        var binding = prefix.LastIndexOf("{Binding", StringComparison.Ordinal);
        if (binding < 0 || (prefix.Length > binding + 8 && !char.IsWhiteSpace(prefix[binding + 8]) && prefix[binding + 8] != '}')) return false;
        var depth = 0;
        for (var i = binding; i < prefix.Length; i++)
        {
            if (prefix[i] == '{') depth++;
            else if (prefix[i] == '}' && --depth == 0) return false;
        }
        return depth > 0;
    }
}
