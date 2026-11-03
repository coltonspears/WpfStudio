using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;

namespace WpfStudio.Core.Documents;

public sealed partial class DocumentState : ObservableObject
{
    public DocumentState(string path, string text, Encoding? encoding = null, string? diskHash = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        SavedText = text;
        Content = text;
        Encoding = encoding ?? new UTF8Encoding(false);
        DiskHash = diskHash;
        Version = 1;
    }
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    /// <summary>The tab title: • marks unsaved edits, ⚠ a disk version changed outside WpfStudio that awaits a choice.</summary>
    public string Title => Name + (IsDirty ? " •" : "") + (HasExternalChange ? " ⚠" : "");
    public Encoding Encoding { get; internal set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty), nameof(Title))] public partial string? DiskHash { get; private set; }
    public long Version { get; private set; }
    public bool IsDirty => DiskHash == null || Content != SavedText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty), nameof(Title))] private partial string SavedText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirty), nameof(Title))] public partial string Content { get; set; } = "";
    [ObservableProperty] public partial int CaretOffset { get; set; }
    [ObservableProperty] public partial int CaretLine { get; set; } = 1;
    [ObservableProperty] public partial int CaretColumn { get; set; } = 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial bool HasExternalChange { get; set; }
    public event EventHandler? ContentChanged;
    partial void OnContentChanged(string value)
    {
        Version++;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
    public void MarkSaved(string hash)
    {
        SavedText = Content;
        DiskHash = hash;
        HasExternalChange = false;
    }
    /// <summary>
    /// Records the disk version without changing the editor text, after the user chose to keep their version of a
    /// file changed outside WpfStudio. The document becomes dirty when the texts differ, and saving replaces the disk
    /// version without another conflict. A null <paramref name="hash"/> means the file was deleted.
    /// </summary>
    public void KeepEditorVersion(string? diskText, string? hash)
    {
        SavedText = diskText ?? "";
        DiskHash = hash;
        HasExternalChange = false;
    }
    public void MarkSavedSnapshot(string text, string hash)
    {
        SavedText = text;
        DiskHash = hash;
        HasExternalChange = false;
    }
}
