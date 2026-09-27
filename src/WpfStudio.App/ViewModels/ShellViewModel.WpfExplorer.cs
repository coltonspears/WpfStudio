using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Core.Wpf;
using WpfStudio.Contracts;

namespace WpfStudio.App.ViewModels;

public sealed partial class ShellViewModel
{
    public string[] WpfCategories { get; } = ["All WPF items", "Views and controls", "View models", "Dictionaries", "Styles and templates", "Keyed resources", "Converters", "Assets"];
    [ObservableProperty] public partial string WpfCategory { get; set; } = "All WPF items";
    public ObservableCollection<NavigationResult> WpfReferences { get; } = [];
    public ObservableCollection<WorkspaceDiagnostic> WpfIssues { get; } = [];
    public string WpfSummary => _wpfIndex == null ? "Open a WPF project to explore its components." : $"{_wpfIndex.Items.Count} components and resources · {_wpfIndex.Resources.Count} named keys";
    public string WpfResultSummary => $"{WpfItems.Count} items shown";
    public string WpfIssueTitle => $"Issues ({WpfIssues.Count})";
    public bool HasWpfSelection => SelectedWpfItem != null;
    public bool HasWpfAssetSelection => SelectedWpfItem?.Kind == "Asset";
    public bool CanRenameWpfResource => SelectedWpfItem?.Key != null;
    public string SelectedWpfDescription => SelectedWpfItem?.Kind switch
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
        _ => SelectedWpfItem?.Key != null ? "A named XAML resource, referenced with StaticResource or DynamicResource. Lookup depends on the enclosing resource scope and merged dictionaries." : "A WPF component declared in this project. Open its source to inspect or edit it."
    };
    public string WpfUsageSummary { get; private set; } = "";
    partial void OnWpfCategoryChanged(string value) => RefreshWpfItems();
    partial void OnSelectedWpfItemChanged(WpfItem? value) => RefreshWpfSelection();
    private ResourceDeclaration? ResourceAtCaret()
    {
        if (ActiveDocument is not { } document || _wpfIndex == null) return null;
        var state = document.State;
        // Never use old index offsets after edits. Refresh first so refactor previews
        // correspond to exactly the buffer being inspected.
        if (!_wpfIndex.Texts.TryGetValue(state.Path, out var indexed) || indexed != state.Content) { Status = "Refresh WPF Explorer after editing resource keys"; return null; }
        var direct = _wpfIndex.Resources.FirstOrDefault(r => r.Path == state.Path && state.CaretOffset >= r.ValueStart && state.CaretOffset <= r.ValueStart + r.ValueLength);
        if (direct != null) return direct;
        var usage = _wpfIndex.Usages.FirstOrDefault(u => u.Path == state.Path && state.CaretOffset >= u.Start && state.CaretOffset <= u.Start + u.Length);
        return usage?.ResolvedPath == null ? null : _wpfIndex.Resources.FirstOrDefault(r => r.Path == usage.ResolvedPath && r.ValueStart == usage.ResolvedDeclarationStart);
    }
    private bool MatchesWpfCategory(WpfItem item) => WpfCategory switch
    {
        "Views and controls" => item.Kind is "Window" or "Page" or "UserControl",
        "View models" => item.Kind == "ViewModel", "Dictionaries" => item.Kind == "ResourceDictionary",
        "Styles and templates" => item.Kind is "Style" or "DataTemplate" or "ControlTemplate",
        "Keyed resources" => item.Key != null, "Converters" => item.Kind == "Converter", "Assets" => item.Kind == "Asset", _ => true
    };
    private void RefreshWpfSelection()
    {
        WpfReferences.Clear(); WpfUsageSummary = "";
        if (SelectedWpfItem is { Key: not null } item && _wpfIndex is { } index)
        {
            var declaration = index.Resources.FirstOrDefault(r => r.Path == item.Path && r.Key == item.Key && r.Line == item.Line);
            if (declaration != null)
            {
                foreach (var usage in index.Usages.Where(u => u.ResolvedPath == declaration.Path && u.ResolvedDeclarationStart == declaration.ValueStart))
                    WpfReferences.Add(new(usage.Path, usage.Line, 1, (usage.IsDynamic ? "DynamicResource " : "StaticResource ") + usage.Key, usage.Start));
                var uncertain = index.Usages.Count(u => u.Key == item.Key && u.ResolvedPath == null);
                WpfUsageSummary = $"{WpfReferences.Count} resolved XAML reference(s)" + (uncertain > 0 ? $"; {uncertain} same-key reference(s) need runtime context." : ". Code and runtime-created references are not indexed.");
            }
        }
        foreach (var name in new[] { nameof(HasWpfSelection), nameof(HasWpfAssetSelection), nameof(CanRenameWpfResource), nameof(SelectedWpfDescription), nameof(WpfUsageSummary) }) OnPropertyChanged(name);
    }
}
