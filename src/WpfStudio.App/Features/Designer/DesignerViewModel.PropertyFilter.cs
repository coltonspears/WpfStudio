using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Designer;

/// <summary>Search and "set values only" filtering for the property grid.</summary>
public sealed partial class DesignerViewModel
{
    private static readonly HashSet<string> UnsetValueSources = new(StringComparer.Ordinal)
    {
        "Default", "Inherited", "DefaultStyle", "DefaultStyleTrigger", "Unknown"
    };
    /// <summary>Raised when the view should re-evaluate <see cref="MatchesPropertyFilter"/> for its items.</summary>
    public event Action? PropertyFilterChanged;

    [ObservableProperty] public partial string PropertyFilter { get; set; } = "";
    [ObservableProperty] public partial bool ShowSetPropertiesOnly { get; set; }
    public int VisiblePropertyCount { get; private set; }
    public string PropertyCountText => Properties.Count == 0 ? ""
        : VisiblePropertyCount == Properties.Count ? $"{Properties.Count} properties" : $"{VisiblePropertyCount} of {Properties.Count}";

    /// <summary>True when the value comes from this element, its style, a template or data, rather than a default.</summary>
    public static bool IsSetHere(PreviewProperty property) => property.IsExpression || property.IsOverridden || property.IsAnimated
        || !UnsetValueSources.Contains(property.ValueSource);

    private void InitializePropertyFilter() => Properties.CollectionChanged += (_, _) => UpdatePropertyCount();

    /// <summary>The grid binds to Properties directly and filters its view with this predicate, so
    /// selection, keyboard navigation and automation keep using the same item instances.</summary>
    public bool MatchesPropertyFilter(PreviewProperty property)
    {
        if (ReferenceEquals(property, SelectedProperty)) return true;
        if (ShowSetPropertiesOnly && !IsSetHere(property)) return false;
        var filter = PropertyFilter.Trim();
        return filter.Length == 0
            || property.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || property.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || property.BindingPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
    }

    partial void OnPropertyFilterChanged(string value) => RefreshPropertyFilter();
    partial void OnShowSetPropertiesOnlyChanged(bool value) => RefreshPropertyFilter();

    private void RefreshPropertyFilter()
    {
        if (PropertyFilter.Length == 0 && !ShowSetPropertiesOnly && VisiblePropertyCount == Properties.Count) return;
        PropertyFilterChanged?.Invoke();
        UpdatePropertyCount();
    }

    private void UpdatePropertyCount()
    {
        VisiblePropertyCount = PropertyFilter.Length == 0 && !ShowSetPropertiesOnly ? Properties.Count : Properties.Count(MatchesPropertyFilter);
        OnPropertyChanged(nameof(VisiblePropertyCount));
        OnPropertyChanged(nameof(PropertyCountText));
    }

    [RelayCommand] private void ClearPropertyFilter() { PropertyFilter = ""; ShowSetPropertiesOnly = false; }
}
