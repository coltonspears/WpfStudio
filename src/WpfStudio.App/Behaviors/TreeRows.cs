using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfStudio.App.Behaviors;

/// <summary>A row of a hierarchical list. A virtualized tree generates rows only as they scroll into view, so finding the way to a row
/// that has no container yet goes through the data.</summary>
public interface ITreeRow
{
    IEnumerable ChildRows { get; }
}

/// <summary>Generates the rows of a virtualized <see cref="TreeView"/> down to one item, so it can be selected and scrolled to.</summary>
internal static class TreeRows
{
    /// <summary>Returns the item's container, or null when the item is not in the tree.</summary>
    public static TreeViewItem? Reveal(TreeView tree, ITreeRow target)
    {
        var path = new List<object>();
        if (!Find(tree.Items, target, path)) return null;
        ItemsControl parent = tree;
        TreeViewItem? container = null;
        foreach (var item in path)
        {
            container = Realize(parent, item);
            if (container is null) return null;
            if (!ReferenceEquals(item, target) && !container.IsExpanded) container.IsExpanded = true;
            parent = container;
        }
        return container;
    }

    // Depth first through the data, iteratively: a deep visual tree must not overflow the stack.
    private static bool Find(IEnumerable roots, ITreeRow target, List<object> path)
    {
        var stack = new Stack<(object Item, int Depth)>();
        foreach (var root in roots.Cast<object>().Reverse()) stack.Push((root, 0));
        while (stack.TryPop(out var entry))
        {
            path.RemoveRange(entry.Depth, path.Count - entry.Depth);
            path.Add(entry.Item);
            if (ReferenceEquals(entry.Item, target)) return true;
            if (entry.Item is ITreeRow row)
                foreach (var child in row.ChildRows.Cast<object>().Reverse()) stack.Push((child, entry.Depth + 1));
        }
        path.Clear();
        return false;
    }

    private static TreeViewItem? Realize(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem existing) return existing;
        var index = parent.Items.IndexOf(item);
        if (index < 0) return null;
        parent.ApplyTemplate();
        if (FindItemsHost(parent) is not { } host)
        {
            // The parent has not been measured since it expanded, so it has no panel yet.
            parent.UpdateLayout();
            host = FindItemsHost(parent);
        }
        if (host is VirtualizingStackPanel panel) panel.BringIndexIntoViewPublic(index);
        else parent.UpdateLayout();
        return parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
    }

    private static Panel? FindItemsHost(DependencyObject root)
    {
        var pending = new Queue<DependencyObject>(); pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child is Panel { IsItemsHost: true } host) return host;
                // Stop at nested rows: their panels belong to them.
                if (child is not TreeViewItem) pending.Enqueue(child);
            }
        }
        return null;
    }
}
