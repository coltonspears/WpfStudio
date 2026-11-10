using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Wpf;

namespace WpfStudio.App.Features.WpfExplorer;

/// <summary>
/// WPF Explorer: the components and resources of the indexed workspace, filtered by category and text, with the
/// selected item's description and resolved resource references. The shell supplies the index and the issue list.
/// </summary>
public sealed partial class WpfExplorerViewModel : ObservableObject
{
    public const string AllCategory = "All WPF items";
    public string[] Categories { get; } = [AllCategory, "Views and controls", "View models", "Dictionaries", "Styles and templates", "Keyed resources", "Converters", "Assets"];
    public ObservableCollection<WpfItem> Items { get; } = [];
    public ObservableCollection<NavigationResult> References { get; } = [];
    public BulkObservableCollection<WorkspaceDiagnostic> Issues { get; } = [];
    [ObservableProperty] public partial string Category { get; set; } = AllCategory;
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial WpfItem? SelectedItem { get; set; }
    /// <summary>The last index the shell computed; null before a workspace is indexed.</summary>
    public WpfIndexSnapshot? Index { get; private set; }

    public string Summary => Index == null ? "Open a WPF project to explore its components." : $"{Index.Items.Count} components and resources · {Index.Resources.Count} named keys";
    public string ResultSummary => $"{Items.Count} items shown";
    public string IssueTitle => $"Issues ({Issues.Count})";
    public bool HasSelection => SelectedItem != null;
    public bool HasAssetSelection => SelectedItem?.Kind == "Asset";
    public bool CanRenameResource => SelectedItem?.Key != null;
    public string SelectedDescription => SelectedItem?.Kind switch
    {
        null => "Select an item to see what it does and where it is used. Double-click an item to open its source.",
        "Window" or "Page" or "UserControl" => "A reusable view or screen. Open its XAML to edit the layout; switch to its code-behind or view model from the editor.",
        "ViewModel" => "Presentation state and commands used by a view. Use WPF > Insert observable property or relay command to add Toolkit members.",
        "ResourceDictionary" => "A collection of shared resources. Merge this dictionary into application or view resources to make its keys available there.",
        "Style" => "Property setters and triggers for a control type. A keyed style is referenced explicitly; an implicit style applies by type within its resource scope.",
        "DataTemplate" => "Defines how a data object is displayed, including view-model-to-view mappings.",
        "ControlTemplate" => "Defines the visual structure of a control.",
        "Converter" => "Transforms a binding value between the view model and the view.",
        "Asset" => "An image, font, or other project asset. Its build action determines how it is packaged and addressed.",
        _ => SelectedItem?.Key != null ? "A named XAML resource, referenced with StaticResource or DynamicResource. Lookup depends on the enclosing resource scope and merged dictionaries." : "A WPF component declared in this project. Open its source to inspect or edit it."
    };
    public string UsageSummary { get; private set; } = "";

    partial void OnCategoryChanged(string value) => RefreshItems();
    partial void OnFilterChanged(string value) => RefreshItems();
    partial void OnSelectedItemChanged(WpfItem? value) => RefreshSelection();

    /// <summary>Replaces the index and relists the items, keeping the selection when it still exists.</summary>
    public void SetIndex(WpfIndexSnapshot? index)
    {
        Index = index;
        RefreshItems();
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Replaces the issue list with the diagnostics the shell currently attributes to XAML.</summary>
    public void SetIssues(IEnumerable<WorkspaceDiagnostic> issues)
    {
        Issues.ReplaceAll(issues);
        OnPropertyChanged(nameof(IssueTitle));
    }

    /// <summary>True when the index was built from exactly this text, so its offsets can be trusted.</summary>
    public bool IsIndexCurrent(string path, string content) => Index?.Texts.TryGetValue(path, out var indexed) == true && indexed == content;

    /// <summary>The resource declared at, or referenced from, <paramref name="offset"/> in the file.</summary>
    public ResourceDeclaration? ResourceAt(string path, int offset)
    {
        if (Index is not { } index) return null;
        var direct = index.Resources.FirstOrDefault(r => r.Path == path && offset >= r.ValueStart && offset <= r.ValueStart + r.ValueLength);
        if (direct != null) return direct;
        var usage = index.Usages.FirstOrDefault(u => u.Path == path && offset >= u.Start && offset <= u.Start + u.Length);
        return usage?.ResolvedPath == null ? null : index.Resources.FirstOrDefault(r => r.Path == usage.ResolvedPath && r.ValueStart == usage.ResolvedDeclarationStart);
    }

    private bool MatchesCategory(WpfItem item) => Category switch
    {
        "Views and controls" => item.Kind is "Window" or "Page" or "UserControl",
        "View models" => item.Kind == "ViewModel", "Dictionaries" => item.Kind == "ResourceDictionary",
        "Styles and templates" => item.Kind is "Style" or "DataTemplate" or "ControlTemplate",
        "Keyed resources" => item.Key != null, "Converters" => item.Kind == "Converter", "Assets" => item.Kind == "Asset", _ => true
    };

    public void RefreshItems()
    {
        var selected = SelectedItem;
        Items.Clear();
        if (Index != null)
            foreach (var item in Index.Items.Where(i => MatchesCategory(i) && (i.Name + " " + i.Kind + " " + i.Path).Contains(Filter, StringComparison.OrdinalIgnoreCase))) Items.Add(item);
        SelectedItem = Items.FirstOrDefault(i => i == selected);
        OnPropertyChanged(nameof(ResultSummary));
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        References.Clear(); UsageSummary = "";
        if (SelectedItem is { Key: not null } item && Index is { } index)
        {
            var declaration = index.Resources.FirstOrDefault(r => r.Path == item.Path && r.Key == item.Key && r.Line == item.Line);
            if (declaration != null)
            {
                foreach (var usage in index.Usages.Where(u => u.ResolvedPath == declaration.Path && u.ResolvedDeclarationStart == declaration.ValueStart))
                    References.Add(new(usage.Path, usage.Line, 1, (usage.IsDynamic ? "DynamicResource " : "StaticResource ") + usage.Key, usage.Start));
                var uncertain = index.Usages.Count(u => u.Key == item.Key && u.ResolvedPath == null);
                UsageSummary = $"{References.Count} resolved XAML reference(s)" + (uncertain > 0 ? $"; {uncertain} same-key reference(s) need runtime context." : ". Code and runtime-created references are not indexed.");
            }
        }
        foreach (var name in new[] { nameof(HasSelection), nameof(HasAssetSelection), nameof(CanRenameResource), nameof(SelectedDescription), nameof(UsageSummary) }) OnPropertyChanged(name);
    }
}
