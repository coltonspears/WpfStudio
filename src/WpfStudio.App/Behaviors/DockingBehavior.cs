using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Core;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Core;

namespace WpfStudio.App.Behaviors;

/// <summary>View-only docking lifecycle, serialization and document close coordination.</summary>
public static class DockingBehavior
{
    public static readonly DependencyProperty ShellProperty = DependencyProperty.RegisterAttached("Shell", typeof(ShellViewModel), typeof(DockingBehavior), new PropertyMetadata(null, ShellChanged));
    private static readonly DependencyProperty ContextProperty = DependencyProperty.RegisterAttached("Context", typeof(Context), typeof(DockingBehavior));
    public static readonly DependencyProperty LayoutPathProperty = DependencyProperty.RegisterAttached("LayoutPath", typeof(string), typeof(DockingBehavior), new PropertyMetadata(null));
    public static void SetShell(DependencyObject target, ShellViewModel value) => target.SetValue(ShellProperty, value);
    public static ShellViewModel? GetShell(DependencyObject target) => (ShellViewModel?)target.GetValue(ShellProperty);
    public static void SetLayoutPath(DependencyObject target, string value) => target.SetValue(LayoutPathProperty, value);
    public static string? GetLayoutPath(DependencyObject target) => (string?)target.GetValue(LayoutPathProperty);
    public static void RestoreLayout(DockingManager manager) => (manager.GetValue(ContextProperty) as Context)?.Restore();
    private static void ShellChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not DockingManager manager) return;
        if (manager.GetValue(ContextProperty) is Context previous) previous.Dispose();
        manager.SetValue(ContextProperty, args.NewValue is ShellViewModel shell ? new Context(manager, shell) : null);
    }
    private sealed class Context : IDisposable
    {
        private readonly DockingManager manager;
        private readonly ShellViewModel shell;
        private readonly Dictionary<string, object> staticContent = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> staticTitles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<EditorViewModel> closing = [];
        private byte[]? defaultLayout;
        private object? welcomeContent;
        private bool restoring;
        private string LayoutPath => GetLayoutPath(manager) ?? Path.Combine(AppPaths.DataDirectory, "layout.xml");
        public Context(DockingManager manager, ShellViewModel shell)
        {
            this.manager = manager; this.shell = shell;
            manager.Loaded += Loaded;
            manager.DocumentClosing += DocumentClosing;
            manager.ActiveContentChanged += ActiveChanged;
            shell.PropertyChanged += PropertyChanged;
            shell.Documents.CollectionChanged += DocumentsChanged;
            shell.ToolRequested += ShowTool;
            shell.WorkbenchCloseRequested += CloseWorkbench;
            shell.LayoutSaveRequested += Save;
            shell.LayoutRestoreRequested += Restore;
            shell.LayoutResetRequested += Reset;
            shell.ThemeChanged += ApplyTheme;
            if (manager.IsLoaded) Capture();
        }
        private void Loaded(object sender, RoutedEventArgs args) => Capture();
        private void Capture()
        {
            if (defaultLayout is not null) return;
            // Package and Git workbenches need document-sized space. Retain their
            // views here so closing/reopening tabs preserves feature state.
            staticContent["Packages"] = new Features.Packages.PackagesPane { DataContext = shell.Features?.Packages };
            staticContent["Git"] = new Features.Git.GitPane { DataContext = shell.Features?.Git };
            staticContent["Profiler"] = new Features.Profiling.MemoryProfilerPane { DataContext = shell.MemoryProfiler };
            staticTitles["Profiler"] = "Memory profiler";
            staticContent["Designer"] = new Features.Designer.DesignerPane { DataContext = shell.Designer };
            staticTitles["Designer"] = "XAML Designer";
            staticContent["LiveInspection"] = new Features.Inspection.InspectionPane { DataContext = shell.LiveInspection };
            staticTitles["LiveInspection"] = "Live XAML";
            staticTitles["Packages"] = "NuGet packages"; staticTitles["Git"] = "Git changes";
            foreach (var content in manager.Layout.Descendents().OfType<LayoutContent>().Where(x => x.Content is not EditorViewModel && !string.IsNullOrEmpty(x.ContentId)))
            {
                if (content.Content is null) continue;
                staticContent[content.ContentId] = content.Content;
                staticTitles[content.ContentId] = content.Title;
                if (content.ContentId == "Welcome") welcomeContent = content.Content;
            }
            using var stream = new MemoryStream(); new XmlLayoutSerializer(manager).Serialize(stream); defaultLayout = stream.ToArray();
            ApplyTheme(shell.ThemeName);
            SynchronizeDocuments();
        }
        private void ApplyTheme(string name)
        {
            ThemeService.Apply(name);
            var light = name.Equals("Light", StringComparison.OrdinalIgnoreCase);
            if (manager.Theme is not Controls.StudioDockTheme current || current.IsLight != light) manager.Theme = new Controls.StudioDockTheme(light);
        }
        private void PropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ShellViewModel.ActiveDocument) && shell.ActiveDocument is { } active && !ReferenceEquals(manager.ActiveContent, active)) manager.ActiveContent = active;
        }
        private void ActiveChanged(object? sender, EventArgs args)
        {
            if (restoring) return;
            if (manager.ActiveContent is EditorViewModel editor)
            {
                if (!ReferenceEquals(shell.ActiveDocument, editor)) shell.ActiveDocument = editor;
            }
            else if (manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => ReferenceEquals(d.Content, manager.ActiveContent)) is { } document)
                shell.ActivateWorkbench(document.ContentId is "Packages" or "Git" or "Profiler" ? document.ContentId : null);
        }
        private void DocumentsChanged(object? sender, NotifyCollectionChangedEventArgs args) => manager.Dispatcher.BeginInvoke(SynchronizeDocuments);
        private void SynchronizeDocuments()
        {
            if (restoring || defaultLayout is null) return;
            var documents = manager.Layout.Descendents().OfType<LayoutDocument>().ToArray();
            foreach (var document in documents.Where(x => x.Content is EditorViewModel))
            {
                var editor = (EditorViewModel)document.Content;
                if (!shell.Documents.Contains(editor)) { document.Parent?.RemoveChild(document); continue; }
                document.ContentId = editor.ContentId;
                document.Title = editor.Title;
            }
            var pane = manager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
            if (pane is null) return;
            var requestedEditor = shell.ActiveDocument;
            var activateNewEditor = false;
            foreach (var editor in shell.Documents.Where(editor => !manager.Layout.Descendents().OfType<LayoutDocument>().Any(x => ReferenceEquals(x.Content, editor))))
            {
                pane.Children.Add(new LayoutDocument { Content = editor, ContentId = editor.ContentId, Title = editor.Title });
                activateNewEditor |= ReferenceEquals(requestedEditor, editor);
            }
            var welcome = manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(x => x.ContentId == "Welcome");
            if (shell.Documents.Count > 0 && welcome is not null) welcome.Parent?.RemoveChild(welcome);
            else if (shell.Documents.Count == 0 && welcome is null && welcomeContent is not null) pane.Children.Add(new LayoutDocument { Content = welcomeContent, ContentId = "Welcome", Title = "Start", CanClose = false });
            if (requestedEditor is not null && (activateNewEditor || manager.ActiveContent is null)) manager.ActiveContent = requestedEditor;
        }
        private async void DocumentClosing(object? sender, DocumentClosingEventArgs args)
        {
            if (args.Document.Content is not EditorViewModel document)
            {
                if (args.Document.ContentId is "Packages" or "Git" or "Profiler")
                {
                    var workbench = args.Document;
                    var pane = workbench.Parent as LayoutDocumentPane;
                    _ = manager.Dispatcher.BeginInvoke(() => UpdateAfterWorkbenchClose(workbench, pane));
                }
                return;
            }
            args.Cancel = true;
            if (!closing.Add(document)) return;
            try { await shell.CloseDocumentAsync(document); }
            catch (Exception ex) { shell.Status = "Could not close document: " + ex.Message; }
            finally { closing.Remove(document); }
        }
        private void CloseWorkbench(string name)
        {
            var document = manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => d.ContentId == name);
            if (document?.CanClose == true)
            {
                var pane = document.Parent as LayoutDocumentPane;
                document.Close();
                UpdateAfterWorkbenchClose(document, pane);
            }
        }
        private void UpdateAfterWorkbenchClose(LayoutDocument closed, LayoutDocumentPane? pane)
        {
            if (manager.Layout.Descendents().OfType<LayoutDocument>().Contains(closed) || shell.ActiveWorkbench != closed.ContentId) return;
            var selected = pane?.Children.FirstOrDefault(d => d.IsSelected)
                ?? manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => d.IsSelected);
            if (selected?.Content is EditorViewModel editor) shell.ActiveDocument = editor;
            else shell.ActivateWorkbench(selected?.ContentId is "Packages" or "Git" or "Profiler" ? selected.ContentId : null);
        }
        private void ShowTool(string name)
        {
            var aliases = name is "WPF" or "Resources" ? "WpfTools" : name is "Database" or "SQL Server" ? "Database" : name;
            if (aliases is "Packages" or "Git" or "Profiler")
            {
                Capture();
                var document = manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => d.ContentId == aliases);
                if (document == null && manager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault() is { } pane)
                {
                    document = new LayoutDocument { ContentId = aliases, Title = staticTitles[aliases], Content = staticContent[aliases], CanClose = true };
                    pane.Children.Add(document);
                }
                if (document != null) { document.IsSelected = true; document.IsActive = true; }
                return;
            }
            var tool = manager.Layout.Descendents().OfType<LayoutAnchorable>().Concat(manager.Layout.Hidden).FirstOrDefault(x => x.ContentId == aliases);
            if (tool is null && staticContent.TryGetValue(aliases, out var content))
            {
                tool = new LayoutAnchorable { ContentId = aliases, Content = content, Title = staticTitles[aliases], CanClose = false };
                if (aliases is "Designer" or "LiveInspection" && manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .FirstOrDefault(candidate => candidate.ContentId == "Output")?.Parent is LayoutAnchorablePane bottomPane)
                    bottomPane.Children.Add(tool);
                else tool.AddToLayout(manager, AnchorableShowStrategy.Bottom | AnchorableShowStrategy.Most);
            }
            if (tool is null) return;
            if (tool.IsHidden) tool.Show();
            if (tool.IsAutoHidden) tool.ToggleAutoHide();
            if (aliases == "Debugger" && tool.Parent is LayoutAnchorablePane debugPane && debugPane.DockHeight.IsAbsolute && debugPane.DockHeight.Value < 320)
                debugPane.DockHeight = new GridLength(320);
            if (aliases == "Designer" && tool.Parent is LayoutAnchorablePane designerPane && (!designerPane.DockHeight.IsAbsolute || designerPane.DockHeight.Value < 500))
                designerPane.DockHeight = new GridLength(500);
            if (aliases == "LiveInspection" && tool.Parent is LayoutAnchorablePane inspectionPane && (!inspectionPane.DockHeight.IsAbsolute || inspectionPane.DockHeight.Value < 420))
                inspectionPane.DockHeight = new GridLength(420);
            tool.IsSelected = true; tool.IsActive = true;
        }
        private void Save()
        {
            try
            {
                Capture(); SynchronizeDocuments();
                Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
                var temporary = LayoutPath + ".tmp";
                new XmlLayoutSerializer(manager).Serialize(temporary);
                File.Move(temporary, LayoutPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { shell.Status = "Could not save docking layout: " + ex.Message; }
        }
        public void Restore()
        {
            Capture();
            if (!File.Exists(LayoutPath)) return;
            try { using var stream = File.OpenRead(LayoutPath); RestoreFrom(stream); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException) { shell.Status = "Saved layout could not be restored. Using the default layout."; Reset(); }
        }
        private void Reset()
        {
            Capture(); if (defaultLayout is null) return;
            using var stream = new MemoryStream(defaultLayout); RestoreFrom(stream);
        }
        private void RestoreFrom(Stream stream)
        {
            restoring = true;
            try
            {
                var serializer = new XmlLayoutSerializer(manager) { UnresolvedContentHandling = UnresolvedContentHandling.Remove };
                serializer.LayoutSerializationCallback += (_, args) =>
                {
                    var id = args.Model.ContentId;
                    if (id == "Welcome" && shell.Documents.Count > 0) { args.Cancel = true; return; }
                    var editor = shell.Documents.FirstOrDefault(x => string.Equals(x.ContentId, id, StringComparison.OrdinalIgnoreCase));
                    if (editor is not null) args.Content = editor;
                    else if (id is not null && staticContent.TryGetValue(id, out var content)) args.Content = content;
                    else { args.Content = null!; args.Cancel = true; }
                };
                serializer.Deserialize(stream);
            }
            finally { restoring = false; }
            SynchronizeDocuments();
            ActiveChanged(manager, EventArgs.Empty);
        }
        public void Dispose()
        {
            manager.Loaded -= Loaded; manager.DocumentClosing -= DocumentClosing; manager.ActiveContentChanged -= ActiveChanged;
            shell.PropertyChanged -= PropertyChanged; shell.Documents.CollectionChanged -= DocumentsChanged;
            shell.ToolRequested -= ShowTool; shell.WorkbenchCloseRequested -= CloseWorkbench; shell.LayoutSaveRequested -= Save; shell.LayoutRestoreRequested -= Restore; shell.LayoutResetRequested -= Reset; shell.ThemeChanged -= ApplyTheme;
        }
    }
}

/// <summary>Document model bindings apply exclusively to documents; tool headers retain their literal titles.</summary>
public sealed class DocumentDockStyleSelector : StyleSelector
{
    public Style? DocumentStyle { get; set; }
    public override Style? SelectStyle(object item, DependencyObject container) => container is LayoutDocumentItem && item is EditorViewModel ? DocumentStyle : null;
}
