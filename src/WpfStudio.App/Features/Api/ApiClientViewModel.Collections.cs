using System.IO;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.Api.Import;
using WpfStudio.Api.Models;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Api;

/// <summary>Collections in the sidebar: creating, renaming, importing, exporting and saving requests into them.</summary>
public sealed partial class ApiClientViewModel
{
    private const string ImportFilter = "API descriptions and collections (*.json;*.yaml;*.yml)|*.json;*.yaml;*.yml|All files (*.*)|*.*";

    internal ApiCollection? FindCollection(string? id) => id is null ? null : _collections.FirstOrDefault(collection => collection.Id == id);

    /// <summary>"Collection › Folder" for a saved request; empty for a draft.</summary>
    internal string LocationOf(string? collectionId, string? requestId)
    {
        var collection = FindCollection(collectionId);
        if (collection is null) return "";
        var entry = requestId is null ? default : collection.AllRequests().FirstOrDefault(item => item.Request.Id == requestId);
        return string.Join(" › ", new[] { collection.Name }.Concat((entry.Path ?? []).Select(folder => folder.Name)));
    }

    internal void RebuildTree()
    {
        var expanded = Tree.SelectMany(node => node.Descendants().Prepend(node)).Where(node => node.IsExpanded).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var selected = SelectedNode?.Id;
        Tree.Clear();
        foreach (var collection in _collections.OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var root = new ApiTreeNode(ApiNodeKind.Collection, collection) { IsExpanded = expanded.Contains(collection.Id) };
            Fill(root, collection.Folders, collection.Requests);
            Tree.Add(root);
        }
        foreach (var node in Tree.SelectMany(node => node.Descendants().Prepend(node))) if (node.Id == selected) { node.IsSelected = true; SelectedNode = node; }
        if (Filter.Trim().Length > 0) foreach (var node in Tree) node.ApplyFilter(Filter.Trim());
        OnPropertyChanged(nameof(HasCollections));
        foreach (var tab in Tabs) tab.Refresh();

        void Fill(ApiTreeNode parent, List<ApiFolder> folders, List<ApiRequest> requests)
        {
            foreach (var folder in folders)
            {
                var node = new ApiTreeNode(ApiNodeKind.Folder, parent.Collection, folder, parent: parent) { IsExpanded = expanded.Contains(folder.Id) };
                Fill(node, folder.Folders, folder.Requests);
                parent.Children.Add(node);
            }
            foreach (var request in requests) parent.Children.Add(new ApiTreeNode(ApiNodeKind.Request, parent.Collection, request: request, parent: parent));
        }
    }

    private Task<bool> SaveCollectionAsync(ApiCollection collection) => SaveQuietlyAsync(() => _store.SaveCollectionAsync(collection, _lifetime.Token), collection.Name);

    // ------------------------------------------------------------------ opening

    [RelayCommand]
    private void OpenNode(ApiTreeNode? node)
    {
        if (node is null) return;
        if (node.Request is null) { node.IsExpanded = !node.IsExpanded; return; }
        var open = Tabs.FirstOrDefault(tab => tab.SavedRequestId == node.Request.Id && tab.CollectionId == node.Collection.Id);
        if (open is not null) { SelectedTab = open; return; }
        Open(new ApiRequestTabViewModel(this, ApiJson.Clone(node.Request), node.Collection.Id, node.Request.Id, isDirty: false));
    }

    // ------------------------------------------------------------------ creating

    [RelayCommand]
    private async Task NewCollectionAsync()
    {
        var name = await _dialogs.PromptAsync("New collection", "Name the collection:", "My API");
        if (string.IsNullOrWhiteSpace(name)) return;
        var collection = new ApiCollection { Name = name.Trim(), Variables = [new KeyValue("baseUrl", "https://localhost:5001") { Description = "The server every request goes to" }] };
        _collections.Add(collection);
        await SaveCollectionAsync(collection);
        RebuildTree();
        Status = $"Created {collection.Name}. Its baseUrl variable is ready for {{{{baseUrl}}}}/path addresses.";
    }

    [RelayCommand]
    private async Task NewFolderAsync(ApiTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is null || node.IsRequest) node = node?.Parent;
        if (node is null) return;
        var name = await _dialogs.PromptAsync("New folder", $"Name the folder in {node.Name}:", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        var folder = new ApiFolder { Name = name.Trim() };
        (node.Folder?.Folders ?? node.Collection.Folders).Add(folder);
        node.IsExpanded = true;
        await SaveCollectionAsync(node.Collection);
        RebuildTree();
    }

    [RelayCommand]
    private async Task NewRequestInAsync(ApiTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is null || node.IsRequest) node = node?.Parent;
        if (node is null) { NewRequest(); return; }
        var request = new ApiRequest { Name = "New request", Url = node.Collection.Variables.Any(variable => variable.Key == "baseUrl") ? "{{baseUrl}}/" : "" };
        (node.Folder?.Requests ?? node.Collection.Requests).Add(request);
        node.IsExpanded = true;
        await SaveCollectionAsync(node.Collection);
        RebuildTree();
        Open(new ApiRequestTabViewModel(this, ApiJson.Clone(request), node.Collection.Id, request.Id, isDirty: false));
    }

    // ------------------------------------------------------------------ editing the tree

    [RelayCommand]
    private async Task RenameNodeAsync(ApiTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is null) return;
        var name = await _dialogs.PromptAsync("Rename", $"New name for {node.Name}:", node.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == node.Name) return;
        if (node.Request is { } request)
        {
            request.Name = name.Trim();
            foreach (var tab in Tabs.Where(tab => tab.SavedRequestId == request.Id && tab.CollectionId == node.Collection.Id)) { var dirty = tab.IsDirty; tab.Name = request.Name; tab.IsDirty = dirty; }
        }
        else if (node.Folder is { } folder) folder.Name = name.Trim();
        else node.Collection.Name = name.Trim();
        await SaveCollectionAsync(node.Collection);
        RebuildTree();
    }

    [RelayCommand]
    private async Task DuplicateNodeAsync(ApiTreeNode? node)
    {
        node ??= SelectedNode;
        if (node?.Request is not { } request) return;
        var copy = ApiJson.Clone(request);
        copy.Id = ApiRequest.NewId();
        copy.Name = request.Name + " (copy)";
        var list = node.Parent?.Folder?.Requests ?? node.Collection.Requests;
        list.Insert(list.IndexOf(request) + 1, copy);
        await SaveCollectionAsync(node.Collection);
        RebuildTree();
    }

    [RelayCommand]
    private async Task DeleteNodeAsync(ApiTreeNode? node)
    {
        node ??= SelectedNode;
        if (node is null) return;
        var what = node.Kind switch { ApiNodeKind.Collection => "the collection", ApiNodeKind.Folder => "the folder", _ => "the request" };
        if (!await _dialogs.ConfirmAsync("Delete", $"Delete {what} '{node.Name}'{(node.IsRequest ? "" : " and everything in it")}? This cannot be undone.")) return;
        if (node.IsCollection)
        {
            _collections.Remove(node.Collection);
            try { await _store.DeleteCollectionAsync(node.Collection.Id); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Status = "The collection's file could not be deleted: " + exception.Message; }
            foreach (var tab in Tabs.Where(tab => tab.CollectionId == node.Collection.Id)) { tab.CollectionId = null; tab.SavedRequestId = null; tab.IsDirty = true; }
        }
        else
        {
            if (node.Request is { } request) (node.Parent?.Folder?.Requests ?? node.Collection.Requests).Remove(request);
            else if (node.Folder is { } folder) (node.Parent?.Folder?.Folders ?? node.Collection.Folders).Remove(folder);
            var removed = node.Descendants().Prepend(node).Where(item => item.Request is not null).Select(item => item.Request!.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var tab in Tabs.Where(tab => tab.CollectionId == node.Collection.Id && tab.SavedRequestId is not null && removed.Contains(tab.SavedRequestId))) { tab.SavedRequestId = null; tab.IsDirty = true; }
            await SaveCollectionAsync(node.Collection);
        }
        SelectedNode = null;
        RebuildTree();
        ScheduleSessionSave();
        Status = $"Deleted {node.Name}";
    }

    // ------------------------------------------------------------------ saving requests

    /// <summary>Saves a tab into its saved request, or asks where to put it. False when nothing was saved.</summary>
    internal async Task<bool> SaveTabAsync(ApiRequestTabViewModel tab, bool saveAs)
    {
        var collection = FindCollection(tab.CollectionId);
        if (!saveAs && collection is not null && tab.SavedRequestId is { } id && Replace(collection, id, tab.ToModel()))
        {
            if (!await SaveCollectionAsync(collection)) return false;
            tab.MarkSaved(collection.Id, id);
            RebuildTree();
            ScheduleSessionSave();
            Status = $"Saved {tab.Name} to {collection.Name}";
            return true;
        }
        var dialog = new SaveRequestViewModel(_collections, collection, tab.Name);
        SaveDialog = dialog;
        var target = await dialog.Completion;
        SaveDialog = null;
        if (target is null) return false;
        var destination = target.Collection;
        if (destination is null)
        {
            destination = new ApiCollection { Name = target.NewCollectionName };
            _collections.Add(destination);
        }
        var request = tab.ToModel();
        request.Id = ApiRequest.NewId();
        request.Name = target.Name;
        (target.Folder?.Requests ?? destination.Requests).Add(request);
        if (!await SaveCollectionAsync(destination)) return false;
        var dirty = tab.IsDirty;
        tab.Name = request.Name;
        tab.IsDirty = dirty;
        tab.MarkSaved(destination.Id, request.Id);
        RebuildTree();
        ScheduleSessionSave();
        Status = $"Saved {request.Name} to {LocationOf(destination.Id, request.Id)}";
        return true;
    }

    private static bool Replace(ApiCollection collection, string id, ApiRequest updated)
    {
        updated.Id = id;
        if (Swap(collection.Requests)) return true;
        return collection.Folders.Any(SwapIn);

        bool SwapIn(ApiFolder folder) => Swap(folder.Requests) || folder.Folders.Any(SwapIn);
        bool Swap(List<ApiRequest> requests)
        {
            var index = requests.FindIndex(request => request.Id == id);
            if (index < 0) return false;
            requests[index] = updated;
            return true;
        }
    }

    // ------------------------------------------------------------------ import and export

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        var path = await _files.OpenFileAsync("Import an OpenAPI document, Postman collection or environment", ImportFilter);
        if (path is null) return;
        string text;
        try { text = await File.ReadAllTextAsync(path, _lifetime.Token); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Status = "The file could not be read: " + exception.Message; return; }
        await ImportAsync(text, path);
    }

    [RelayCommand]
    private async Task ImportUrlAsync()
    {
        var address = await _dialogs.PromptAsync("Import from a URL", "The address of an OpenAPI or Swagger document (JSON or YAML), such as https://petstore3.swagger.io/api/v3/openapi.json. It is fetched through the proxy in Settings › Network.", "https://");
        if (string.IsNullOrWhiteSpace(address) || address.Trim() == "https://") return;
        Status = "Fetching " + address.Trim() + "…";
        try
        {
            var response = await Sender.SendAsync(new ApiRequest { Url = address.Trim(), Headers = [new KeyValue("Accept", "application/json, application/yaml, text/yaml, */*")] }, null, null, _lifetime.Token);
            if (!response.IsSuccess) { Status = $"The server answered {response.StatusCode} {response.ReasonPhrase}."; return; }
            await ImportAsync(response.Text, address.Trim());
        }
        catch (WpfStudio.Api.Requests.ApiRequestException exception) { Status = "The document could not be fetched: " + exception.Message; }
    }

    private async Task ImportAsync(string text, string location)
    {
        ImportResult result;
        try { result = ApiImporter.Import(text, location); }
        catch (FormatException exception) { await _dialogs.ShowErrorAsync("Import", exception.Message); return; }
        if (result.Environment is { } environment)
        {
            Environments.Add(environment);
            await SaveQuietlyAsync(SaveEnvironmentsAsync, "Environments");
            Status = $"Imported the environment {environment.Name}. Choose it at the top right to use its variables.";
            return;
        }
        var collection = result.Collection!;
        _collections.Add(collection);
        if (!await SaveCollectionAsync(collection)) return;
        if (result.Specification is { } specification) await SaveQuietlyAsync(() => _store.SaveSpecificationAsync(collection.Id, specification, _lifetime.Token), "The OpenAPI document");
        RebuildTree();
        var node = Tree.FirstOrDefault(item => item.Collection == collection);
        if (node is not null) { node.IsExpanded = true; node.IsSelected = true; SelectedNode = node; }
        var count = collection.AllRequests().Count();
        Status = $"Imported {collection.Name} from {result.Format}: {count} request{(count == 1 ? "" : "s")}." + (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "");
    }

    [RelayCommand]
    private async Task ExportCollectionAsync(ApiTreeNode? node)
    {
        var collection = (node ?? SelectedNode)?.Collection;
        if (collection is null) return;
        var path = await _files.SaveFileAsync("Export the collection", "WpfStudio collection (*.json)|*.json", SafeFileName(collection.Name) + ".api.json");
        if (path is null) return;
        var specification = collection.HasSpecification ? await _store.LoadSpecificationAsync(collection.Id, _lifetime.Token) : null;
        await File.WriteAllTextAsync(path, ApiImporter.Export(collection, specification), _lifetime.Token);
        Status = $"Exported {collection.Name}. Secret variables and typed-in credentials were left out.";
    }

    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ------------------------------------------------------------------ settings, environments and models

    [RelayCommand]
    private async Task EditCollectionAsync(ApiTreeNode? node)
    {
        var collection = (node ?? SelectedNode)?.Collection;
        if (collection is null) return;
        var editor = new CollectionSettingsViewModel(collection, _proxy.Current);
        CollectionSettings = editor;
        var saved = await editor.Completion;
        CollectionSettings = null;
        if (!saved) return;
        await SaveCollectionAsync(collection);
        RebuildTree();
        Status = $"Saved the settings of {collection.Name}";
    }

    [RelayCommand]
    private async Task ManageEnvironmentsAsync()
    {
        var editor = new EnvironmentsEditorViewModel(Environments.Where(environment => environment.Id.Length > 0).ToList(), ActiveEnvironment, _dialogs);
        EnvironmentsEditor = editor;
        var result = await editor.Completion;
        EnvironmentsEditor = null;
        if (result is null) return;
        var active = ActiveEnvironment.Id;
        _restoring = true;
        try
        {
            while (Environments.Count > 1) Environments.RemoveAt(1);
            foreach (var environment in result) Environments.Add(environment);
            ActiveEnvironment = Environments.FirstOrDefault(environment => environment.Id == (editor.Selected?.Id ?? active)) ?? NoEnvironment;
        }
        finally { _restoring = false; }
        await SaveQuietlyAsync(SaveEnvironmentsAsync, "Environments");
        foreach (var tab in Tabs) tab.Refresh();
        Status = ActiveEnvironment.Id.Length == 0 ? "Environments saved" : $"Environments saved. Requests use {ActiveEnvironment.Name}.";
    }

    [RelayCommand]
    private async Task GenerateFromCollectionAsync(ApiTreeNode? node)
    {
        var collection = (node ?? SelectedNode)?.Collection;
        if (collection is null) { await CodeGenerator.OpenAsync(null, null, null); return; }
        var specification = collection.HasSpecification ? await _store.LoadSpecificationAsync(collection.Id, _lifetime.Token) : null;
        await CodeGenerator.OpenAsync(collection, specification, null);
    }

    /// <summary>Opens the generator for the selected collection's schemas, or for a JSON sample when nothing with a schema is selected.</summary>
    public Task OpenModelGeneratorAsync() => GenerateFromCollectionAsync(SelectedNode);

    internal void OpenCodeGenerator(ApiRequestTabViewModel tab) => OpenCodeGeneratorAsync(tab).Forget(fault => Report(fault.Message));

    private async Task OpenCodeGeneratorAsync(ApiRequestTabViewModel tab)
    {
        var collection = FindCollection(tab.CollectionId);
        var specification = collection?.HasSpecification == true ? await _store.LoadSpecificationAsync(collection.Id, _lifetime.Token) : null;
        await CodeGenerator.OpenAsync(collection, specification, tab);
    }
}
