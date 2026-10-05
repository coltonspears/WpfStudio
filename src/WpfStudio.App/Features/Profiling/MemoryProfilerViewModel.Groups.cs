using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>Instance grouping for the selected type: thousands of instances become a handful of groups that are alive for
/// the same reason (same retention path, same owner, same generation or the same value).</summary>
public sealed partial class MemoryProfilerViewModel
{
    private CancellationTokenSource? _groups;
    private long _groupsRevision;
    public IReadOnlyList<string> InstanceGroupings { get; } = ["None", "Retention", "Owner", "Generation", "Value"];
    public ObservableCollection<InstanceGroupViewModel> GroupRows { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsGrouped))] public partial string InstanceGrouping { get; set; } = "None";
    [ObservableProperty] public partial MemoryInstanceGroups? InstanceGroups { get; set; }
    [ObservableProperty] public partial bool IsLoadingGroups { get; set; }
    [ObservableProperty] public partial string GroupDescription { get; set; } = "";
    public bool IsGrouped => InstanceGrouping != "None";
    public string GroupingHint => InstanceGrouping switch
    {
        "Retention" => "Instances that reach a GC root the same way. A path holding thousands of instances is where a leak accumulates.",
        "Owner" => "The object that exclusively owns each instance, and the field it uses.",
        "Generation" => "Leaked objects survive collections and end up in generation 2.",
        "Value" => "Instances with identical contents. Every copy after the first could be shared.",
        _ => ""
    };

    partial void OnInstanceGroupingChanged(string value)
    {
        OnPropertyChanged(nameof(GroupingHint));
        if (value == "None") ResetGroups(); else _ = LoadGroupsAsync(_lifetime.Token);
    }

    internal void ResetGroups()
    {
        _groupsRevision++; _groups?.Cancel(); _groups?.Dispose(); _groups = null;
        GroupRows.Clear(); InstanceGroups = null; GroupDescription = ""; IsLoadingGroups = false;
    }

    private async Task LoadGroupsAsync(CancellationToken token)
    {
        if (_session is not { } session || _disposed || !IsGrouped) return;
        if (SelectedType is not { } type) { ResetGroups(); GroupDescription = "Choose a type to group its instances."; return; }
        ResetGroups();
        _groups = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var groupsToken = _groups.Token; var revision = _revision; var groupsRevision = _groupsRevision;
        IsLoadingGroups = true; GroupDescription = $"Grouping {type.Count:N0} instances by {InstanceGrouping.ToLowerInvariant()}…";
        try
        {
            var groups = await session.GetInstanceGroupsAsync(new(type.Key, InstanceGrouping, HiddenRootKinds: HiddenRootKinds), groupsToken);
            if (_disposed || revision != _revision || groupsRevision != _groupsRevision) return;
            InstanceGroups = groups;
            var max = groups.Groups.Count == 0 ? 1 : Math.Max(1, groups.Groups.Max(g => g.RetainedBytes));
            foreach (var group in groups.Groups) GroupRows.Add(new(group, (double)group.RetainedBytes / max, groups.By));
            if (GroupRows.Count > 0 && GroupRows[0].Kind != "Unique") GroupRows[0].IsExpanded = GroupRows.Count <= 3;
            GroupDescription = groups.Description + "." + (groups.OtherCount > 0 ? $" {groups.OtherCount:N0} instances in smaller groups are not listed." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && groupsRevision == _groupsRevision) GroupDescription = "Grouping failed: " + ex.Message; }
        finally { if (groupsRevision == _groupsRevision) IsLoadingGroups = false; }
    }

    [RelayCommand] private void SetInstanceGrouping(string? by) { if (by is not null && InstanceGroupings.Contains(by)) InstanceGrouping = by; }
    [RelayCommand] private void OpenGroupSample(MemoryObjectInfo? sample) { if (sample is not null) ShowObject(sample.Id); }

    [RelayCommand]
    private void InspectGroup(InstanceGroupViewModel? group)
    {
        if (group?.Group.Samples.FirstOrDefault() is { } largest) ShowObject(largest.Id);
    }

    /// <summary>Opens a type's instances grouped by retention: the fastest way to see which path accumulates instances.</summary>
    [RelayCommand]
    private void GroupByRetention(string? typeKey)
    {
        if (typeKey is null) return;
        OpenType(typeKey);
        TypeDetailTab = 1;
        if (InstanceGrouping == "Retention") _ = LoadGroupsAsync(_lifetime.Token); else InstanceGrouping = "Retention";
    }
}

/// <summary>One instance group: a retention path, owner, generation or value, with its largest instances.</summary>
public sealed partial class InstanceGroupViewModel(MemoryInstanceGroup group, double ratio, string by) : ObservableObject
{
    public MemoryInstanceGroup Group => group;
    public string Kind => group.Kind;
    public double Ratio => ratio;
    public string Title => group.Title;
    public string Detail => group.Detail;
    public IReadOnlyList<string> Steps => group.Steps.Count > 1 ? group.Steps.Skip(1).ToArray() : [];
    public bool HasSteps => by == "Retention" && group.Steps.Count > 1;
    public bool ShowDetail => !HasSteps && group.Detail.Length > 0;
    public string CountText => group.Count == 1 ? "1 object" : $"{group.Count:N0} objects";
    public string RetainedText => MemorySize.Format(group.RetainedBytes);
    public string WastedText => group.WastedBytes > 0 ? "wastes " + MemorySize.Format(group.WastedBytes) : "";
    public bool HasWaste => group.WastedBytes > 0;
    public IReadOnlyList<MemoryObjectInfo> Samples => group.Samples;
    public string SamplesText => group.Samples.Count < group.Count ? $"Largest {group.Samples.Count:N0} of {group.Count:N0}" : "";
    public bool IsRoot => group.Kind is "Static" or "Root";
    public bool IsCollectible => group.Kind == "Unrooted";
    public string KindText => group.Kind switch
    {
        "Static" => "Static field", "Root" => "GC root", "Unrooted" => "Collectible", "Hidden" => "Hidden roots", "Owner" => "Owner",
        "Generation" => "Generation", "Value" => "Duplicate", "Unique" => "Unique", _ => group.Kind
    };
    public string ToolTipText => group.Title + (group.Steps.Count > 1 ? "\n" + string.Join("\n  → ", group.Steps) : group.Detail.Length > 0 ? "\n" + group.Detail : "") +
        $"\n{CountText} · retain {RetainedText}" + (HasWaste ? " · " + WastedText : "");
    [ObservableProperty] public partial bool IsExpanded { get; set; }
}
