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
            manager.Theme = name.Equals("Light", StringComparison.OrdinalIgnoreCase) ? new Vs2013LightTheme() : new Vs2013DarkTheme();
        }
        private void PropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ShellViewModel.ActiveDocument) && shell.ActiveDocument is { } active && !ReferenceEquals(manager.ActiveContent, active)) manager.ActiveContent = active;
        }
        private void ActiveChanged(object? sender, EventArgs args)
        {
            if (manager.ActiveContent is EditorViewModel editor && !ReferenceEquals(shell.ActiveDocument, editor)) shell.ActiveDocument = editor;
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
            foreach (var editor in shell.Documents.Where(editor => !manager.Layout.Descendents().OfType<LayoutDocument>().Any(x => ReferenceEquals(x.Content, editor))))
                pane.Children.Add(new LayoutDocument { Content = editor, ContentId = editor.ContentId, Title = editor.Title });
            var welcome = manager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(x => x.ContentId == "Welcome");
            if (shell.Documents.Count > 0 && welcome is not null) welcome.Parent?.RemoveChild(welcome);
            else if (shell.Documents.Count == 0 && welcome is null && welcomeContent is not null) pane.Children.Add(new LayoutDocument { Content = welcomeContent, ContentId = "Welcome", Title = "Start", CanClose = false });
            if (shell.ActiveDocument is not null) manager.ActiveContent = shell.ActiveDocument;
        }
        private async void DocumentClosing(object? sender, DocumentClosingEventArgs args)
        {
            if (args.Document.Content is not EditorViewModel document) return;
            args.Cancel = true;
            if (!closing.Add(document)) return;
            try { await shell.CloseDocumentAsync(document); }
            catch (Exception ex) { shell.Status = "Could not close document: " + ex.Message; }
            finally { closing.Remove(document); }
        }
        private void ShowTool(string name)
        {
            var aliases = name is "WPF" or "Resources" ? "WpfTools" : name is "Database" or "SQL Server" ? "Database" : name;
            var tool = manager.Layout.Descendents().OfType<LayoutAnchorable>().Concat(manager.Layout.Hidden).FirstOrDefault(x => x.ContentId == aliases);
            if (tool is null && staticContent.TryGetValue(aliases, out var content))
            {
                tool = new LayoutAnchorable { ContentId = aliases, Content = content, Title = staticTitles[aliases], CanClose = false };
                tool.AddToLayout(manager, AnchorableShowStrategy.Bottom | AnchorableShowStrategy.Most);
            }
            if (tool is null) return;
            if (tool.IsHidden) tool.Show();
            if (tool.IsAutoHidden) tool.ToggleAutoHide();
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
        }
        public void Dispose()
        {
            manager.Loaded -= Loaded; manager.DocumentClosing -= DocumentClosing; manager.ActiveContentChanged -= ActiveChanged;
            shell.PropertyChanged -= PropertyChanged; shell.Documents.CollectionChanged -= DocumentsChanged;
            shell.ToolRequested -= ShowTool; shell.LayoutSaveRequested -= Save; shell.LayoutRestoreRequested -= Restore; shell.LayoutResetRequested -= Reset; shell.ThemeChanged -= ApplyTheme;
        }
    }
}

/// <summary>Document model bindings apply exclusively to documents; tool headers retain their literal titles.</summary>
public sealed class DocumentDockStyleSelector : StyleSelector
{
    public Style? DocumentStyle { get; set; }
    public override Style? SelectStyle(object item, DependencyObject container) => container is LayoutDocumentItem && item is EditorViewModel ? DocumentStyle : null;
}
