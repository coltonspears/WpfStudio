using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Api.Models;

namespace WpfStudio.App.Features.Api;

public enum ApiNodeKind { Collection, Folder, Request }

/// <summary>A collection, folder or saved request in the sidebar.</summary>
public sealed partial class ApiTreeNode : ObservableObject
{
    public ApiTreeNode(ApiNodeKind kind, ApiCollection collection, ApiFolder? folder = null, ApiRequest? request = null, ApiTreeNode? parent = null)
    { Kind = kind; Collection = collection; Folder = folder; Request = request; Parent = parent; }

    public ApiNodeKind Kind { get; }
    public ApiCollection Collection { get; }
    public ApiFolder? Folder { get; }
    public ApiRequest? Request { get; }
    public ApiTreeNode? Parent { get; }
    public ObservableCollection<ApiTreeNode> Children { get; } = [];

    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    public string Id => Request?.Id ?? Folder?.Id ?? Collection.Id;
    public string Name => Request?.Name ?? Folder?.Name ?? Collection.Name;
    public string Method => Request?.Method ?? "";
    public bool IsCollection => Kind == ApiNodeKind.Collection;
    public bool IsFolder => Kind == ApiNodeKind.Folder;
    public bool IsRequest => Kind == ApiNodeKind.Request;
    /// <summary>A collection or folder: something requests and folders can be added to.</summary>
    public bool CanContain => Kind != ApiNodeKind.Request;
    public bool CanGenerateModels => Collection.HasSpecification;
    public string Detail => Kind switch
    {
        ApiNodeKind.Request => Request!.Url,
        ApiNodeKind.Collection => (Collection.Source?.Format is { Length: > 0 } format ? format + " · " : "") + Count(Collection.AllRequests().Count()),
        _ => Count(Folder!.Requests.Count),
    };
    public string ToolTip => Kind == ApiNodeKind.Request ? Request!.Method + " " + Request.Url + (Request.Description.Length > 0 ? Environment.NewLine + Request.Description : "") : Name + (Detail.Length > 0 ? " · " + Detail : "");

    private static string Count(int requests) => requests == 1 ? "1 request" : requests + " requests";

    /// <summary>Shows this node when it or anything inside it matches; expands what leads to a match.</summary>
    public bool ApplyFilter(string filter)
    {
        var own = filter.Length == 0 || Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || Request?.Url.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
        var any = false;
        foreach (var child in Children) any |= child.ApplyFilter(own && filter.Length > 0 ? "" : filter);
        IsVisible = own || any;
        if (filter.Length > 0 && any) IsExpanded = true;
        return IsVisible;
    }

    public IEnumerable<ApiTreeNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants()) yield return descendant;
        }
    }
}
