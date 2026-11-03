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
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;

namespace WpfStudio.App.Behaviors;

/// <summary>View-only docking lifecycle, serialization and document close coordination.</summary>
public static class DockingBehavior
{
    public static readonly DependencyProperty ShellProperty = DependencyProperty.RegisterAttached("Shell", typeof(ShellViewModel), typeof(DockingBehavior), new PropertyMetadata(null, ShellChanged));
    private static readonly DependencyProperty ContextProperty = DependencyProperty.RegisterAttached("Context", typeof(Context), typeof(DockingBehavior));
    public static void SetShell(DependencyObject target, ShellViewModel value) => target.SetValue(ShellProperty, value);
    public static ShellViewModel? GetShell(DependencyObject target) => (ShellViewModel?)target.GetValue(ShellProperty);
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
        private string LayoutPath => Path.Combine(shell.DataFolder, "layout.xml");
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
            shell.Theme.Applied += ApplyTheme;
            if (manager.IsLoaded) Capture();
        }
        private void Loaded(object sender, RoutedEventArgs args) => Capture();
        private void Capture()
        {
            if (defaultLayout is not null) return;
            // Package and Git workbenches need document-sized space. Retain their
            // views here so closing/reopening tabs preserves feature state.
            staticContent[Workbenches.Packages] = new Features.Packages.PackagesPane { DataContext = shell.Features.Packages };
            staticContent[Workbenches.Git] = new Features.Git.GitPane { DataContext = shell.Features.Git };
            staticContent[Workbenches.Profiler] = new Features.Profiling.MemoryProfilerPane { DataContext = shell.MemoryProfiler };
            staticTitles[Workbenches.Profiler] = "Memory profiler";
            staticContent[Workbenches.Designer] = new Features.Designer.DesignerPane { DataContext = shell.Designer };
            staticTitles[Workbenches.Designer] = "XAML Designer";
            staticContent[Workbenches.LiveInspection] = new Features.Inspection.InspectionPane { DataContext = shell.LiveInspection };
            staticTitles[Workbenches.LiveInspection] = "Live XAML";
            staticTitles[Workbenches.Packages] = "NuGet packages"; staticTitles[Workbenches.Git] = "Git changes";
            foreach (var content in manager.Layout.Descendents().OfType<LayoutContent>().Where(x => x.Content is not EditorViewModel && !string.IsNullOrEmpty(x.ContentId)))
            {
                if (content.Content is null) continue;
                staticContent[content.ContentId] = content.Content;
                staticTitles[content.ContentId] = content.Title;
                if (content.ContentId == Workbenches.Welcome) welcomeContent = content.Content;
            }
            using var stream = new MemoryStream(); new XmlLayoutSerializer(manager).Serialize(stream); defaultLayout = stream.ToArray();
            ApplyTheme(shell.Theme.Palette);
            SynchronizeDocuments();
        }
        private void ApplyTheme(Palette palette)
        {
            if (manager.Theme is not Controls.StudioDockTheme current || current.Palette != palette) manager.Theme = new Controls.StudioDockTheme(palette);
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
                shell.ActivateWorkbench(Workbenches.IsDocument(document.ContentId) ? document.ContentId : null);
        }
        private void DocumentsChanged(object? sender, NotifyCollectionChangedEventArgs args) => _ = manager.Dispatcher.BeginInvoke(SynchronizeDocuments);
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
            var welcome = manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(x => x.ContentId == Workbenches.Welcome);
            if (shell.Documents.Count > 0 && welcome is not null) welcome.Parent?.RemoveChild(welcome);
            else if (shell.Documents.Count == 0 && welcome is null && welcomeContent is not null) pane.Children.Add(new LayoutDocument { Content = welcomeContent, ContentId = Workbenches.Welcome, Title = "Start", CanClose = false });
            if (requestedEditor is not null && (activateNewEditor || manager.ActiveContent is null)) manager.ActiveContent = requestedEditor;
        }
#pragma warning disable VSTHRD100 // A WPF event must cancel synchronously; the awaits below are guarded.
        private async void DocumentClosing(object? sender, DocumentClosingEventArgs args)
#pragma warning restore VSTHRD100
        {
            if (args.Document.Content is not EditorViewModel document)
            {
                if (Workbenches.IsDocument(args.Document.ContentId))
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
            else shell.ActivateWorkbench(Workbenches.IsDocument(selected?.ContentId) ? selected!.ContentId : null);
        }
        private void ShowTool(string name)
        {
            var id = Workbenches.Canonical(name);
            if (Workbenches.IsDocument(id))
            {
                Capture();
                var document = manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => d.ContentId == id);
                if (document == null && manager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault() is { } pane)
                {
                    document = new LayoutDocument { ContentId = id, Title = staticTitles[id], Content = staticContent[id], CanClose = true };
                    pane.Children.Add(document);
                }
                if (document != null) { document.IsSelected = true; document.IsActive = true; }
                return;
            }
            var tool = manager.Layout.Descendents().OfType<LayoutAnchorable>().Concat(manager.Layout.Hidden).FirstOrDefault(x => x.ContentId == id);
            if (tool is null && staticContent.TryGetValue(id, out var content))
            {
                tool = new LayoutAnchorable { ContentId = id, Content = content, Title = staticTitles[id], CanClose = false };
                if (id is Workbenches.Designer or Workbenches.LiveInspection && manager.Layout.Descendents().OfType<LayoutAnchorable>()
                    .FirstOrDefault(candidate => candidate.ContentId == Workbenches.Output)?.Parent is LayoutAnchorablePane bottomPane)
                    bottomPane.Children.Add(tool);
                else tool.AddToLayout(manager, AnchorableShowStrategy.Bottom | AnchorableShowStrategy.Most);
            }
            if (tool is null) return;
            if (tool.IsHidden) tool.Show();
            if (tool.IsAutoHidden) tool.ToggleAutoHide();
            if (id == Workbenches.Debugger && tool.Parent is LayoutAnchorablePane debugPane && debugPane.DockHeight.IsAbsolute && debugPane.DockHeight.Value < 320)
                debugPane.DockHeight = new GridLength(320);
            if (id == Workbenches.Designer && tool.Parent is LayoutAnchorablePane designerPane && (!designerPane.DockHeight.IsAbsolute || designerPane.DockHeight.Value < 500))
                designerPane.DockHeight = new GridLength(500);
            if (id == Workbenches.LiveInspection && tool.Parent is LayoutAnchorablePane inspectionPane && (!inspectionPane.DockHeight.IsAbsolute || inspectionPane.DockHeight.Value < 420))
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
            try { using var stream = new MemoryStream(defaultLayout); RestoreFrom(stream); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { shell.Status = "The default docking layout could not be applied: " + ex.Message; }
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
                    if (id == Workbenches.Welcome && shell.Documents.Count > 0) { args.Cancel = true; return; }
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
            shell.ToolRequested -= ShowTool; shell.WorkbenchCloseRequested -= CloseWorkbench; shell.LayoutSaveRequested -= Save; shell.LayoutRestoreRequested -= Restore; shell.LayoutResetRequested -= Reset; shell.Theme.Applied -= ApplyTheme;
        }
    }
}

/// <summary>Document model bindings apply exclusively to documents; tool headers retain their literal titles.</summary>
public sealed class DocumentDockStyleSelector : StyleSelector
{
    public Style? DocumentStyle { get; set; }
    public override Style? SelectStyle(object item, DependencyObject container) => container is LayoutDocumentItem && item is EditorViewModel ? DocumentStyle : null;
}
