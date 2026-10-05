using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfStudio.App.Controls;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>One right-click menu for every profiler list. Set <c>MemoryMenus.Target</c> on a row to the item it shows
/// (an object, type, dominator node, finding item, field, group or comparison row); the menu offers the actions that
/// make sense for it: inspect, why alive, what it keeps alive, show in graph, open the type, group by retention, copy.
/// Built in code so menus stay consistent and need no per-view XAML.</summary>
public static class MemoryMenus
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached("Target", typeof(object), typeof(MemoryMenus),
        new PropertyMetadata(null, OnTargetChanged));
    public static object? GetTarget(DependencyObject element) => element.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject element, object? value) => element.SetValue(TargetProperty, value);

    private sealed record MenuTarget(int? ObjectId, string? Address, string? TypeKey, string? TypeName, string? Value = null, string? Path = null);

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.ContextMenuOpening -= Opening;
        if (e.NewValue is null) return;
        element.ContextMenuOpening += Opening;
        // The context-menu service only opens a menu that exists; items are rebuilt on every opening.
        element.ContextMenu ??= new ContextMenu();
    }

    private static void Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element || !Populate(element)) e.Handled = true;
    }

    /// <summary>Fills the element's context menu for its target. False when nothing applies (the menu is then suppressed).</summary>
    public static bool Populate(FrameworkElement element)
    {
        if (element.ContextMenu is not { } menu) return false;
        menu.Items.Clear();
        var model = FindModel(element);
        if (model is null || Resolve(GetTarget(element)) is not { } target) return false;
        if (target.ObjectId is int id)
        {
            menu.Items.Add(Item("Inspect", "Search", model.ShowInBrowserCommand, id));
            menu.Items.Add(Item("Why is it alive?", "Anchor", model.ShowWhyAliveCommand, id));
            menu.Items.Add(Item("What does it keep alive?", "Tree", model.ShowKeepsAliveCommand, id));
            menu.Items.Add(Item("Show in graph", "Nodes", model.ShowInGraphCommand, id));
        }
        if (target.TypeKey is { } key && model.HasType(key))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var name = MemoryLabels.ShortType(model.TypeNameOf(key) ?? target.TypeName ?? key);
            menu.Items.Add(Item($"Open {Trim(name)}", "List", model.OpenTypeCommand, key));
            menu.Items.Add(Item("Retention paths of this type", "Flow", model.ShowRetentionPathsCommand, key));
            menu.Items.Add(Item("Group instances by retention", "Group", model.GroupByRetentionCommand, key));
        }
        var copies = new List<MenuItem>();
        if (target.Address is { Length: > 0 } address) copies.Add(Item("Copy address", "Copy", model.CopyTextCommand, address));
        var fullName = (target.TypeKey is { } typeKey ? model.TypeNameOf(typeKey) : null) ?? target.TypeName;
        if (!string.IsNullOrEmpty(fullName)) copies.Add(Item("Copy type name", "Copy", model.CopyTextCommand, fullName));
        if (target.Value is { Length: > 0 } value) copies.Add(Item("Copy value", "Copy", model.CopyTextCommand, value));
        if (target.Path is { Length: > 0 } path) copies.Add(Item("Copy retention path", "Copy", model.CopyTextCommand, path));
        if (copies.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            foreach (var copy in copies) menu.Items.Add(copy);
        }
        return menu.Items.Count > 0;
    }

    private static string Trim(string text) => text.Length > 40 ? text[..39] + "…" : text;

    private static MenuItem Item(string header, string icon, ICommand command, object parameter)
    {
        var item = new MenuItem { Header = header, Command = command, CommandParameter = parameter };
        Ui.SetIcon(item, icon);
        return item;
    }

    private static MemoryProfilerViewModel? FindModel(DependencyObject element)
    {
        for (DependencyObject? at = element; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is FrameworkElement { DataContext: MemoryProfilerViewModel model }) return model;
        return null;
    }

    private static MenuTarget? Resolve(object? item) => item switch
    {
        MemoryObjectInfo o => new(o.Id, o.Address, o.TypeKey, o.Type),
        RetainerRow r => new(r.Object.Id, r.Object.Address, r.Object.TypeKey, r.Object.Type),
        MemoryTypeRow t => new(null, null, t.Key, t.Name),
        ComparisonRow c => new(null, null, c.Key, c.Name),
        GrowthRow g => new(null, null, g.Type.Key, g.Type.Name),
        RetainedTypeRow rt => new(null, null, rt.Type.TypeKey, rt.Type.Type),
        DominatorNodeViewModel { IsPlaceholder: false, IsMoreRow: false } n => new(n.Object?.Id, n.Object?.Address, n.Node.TypeKey, n.Node.Type),
        FindingItemRow f => new(f.Item.ObjectId, null, f.Item.TypeKey, f.Item.TypeKey is null ? null : f.Item.Label),
        ObjectNodeViewModel { IsPlaceholder: false, IsMore: false } node => new(node.ObjectId, null, null, node.Item.Type.Length > 0 ? node.Item.Type : null, node.IsReference ? null : node.Value),
        InstanceGroupViewModel group => new(group.Samples.FirstOrDefault()?.Id, null, null, null, group.Kind is "Value" ? group.Title : null,
            group.Group.Steps.Count > 0 ? string.Join(" → ", group.Group.Steps) : null),
        PathStep step => new(step.TargetId, null, null, null),
        MemoryReferenceInfo reference => new(reference.FromId ?? reference.ToId, null, null, null),
        _ => null
    };
}
