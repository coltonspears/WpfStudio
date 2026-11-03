using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Contracts;

namespace WpfStudio.Runtime.Debugging;

public sealed partial class DebugVariableViewModel : ObservableObject
{
    private readonly Func<int, Task<IReadOnlyList<DebugVariableViewModel>>>? loader;
    private bool loaded;
    public DebugVariableViewModel(string name, string value, string? type, int reference, Func<int, Task<IReadOnlyList<DebugVariableViewModel>>>? loader = null)
    {
        Name = name; Value = value; Type = type; Reference = reference; this.loader = loader;
        if (reference > 0) Children.Add(new DebugVariableViewModel("Expand to load", "", null, 0));
    }
    public string Name { get; }
    public string Value { get; }
    public string? Type { get; }
    public int Reference { get; }
    public ObservableCollection<DebugVariableViewModel> Children { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    partial void OnIsExpandedChanged(bool value) { if (value && !loaded && Reference > 0) LoadAsync().Forget(); }
    private async Task LoadAsync()
    {
        loaded = true;
        try
        {
            var values = loader is null ? [] : await loader(Reference);
            Children.Clear(); foreach (var value in values) Children.Add(value);
        }
        catch (Exception ex) { Children.Clear(); Children.Add(new DebugVariableViewModel("Error", ex.Message, null, 0)); loaded = false; }
    }
}
