using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;

namespace WpfStudio.Core.Documents;

public sealed partial class DocumentState : ObservableObject
{
    private string _savedText;
    public DocumentState(string path, string text, Encoding? encoding = null, string? diskHash = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _savedText = text;
        Content = text;
        Encoding = encoding ?? new UTF8Encoding(false);
        DiskHash = diskHash;
        Version = 1;
    }
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public string Title => Name + (IsDirty ? " •" : "");
    public Encoding Encoding { get; internal set; }
    public string? DiskHash { get; internal set; }
    public long Version { get; private set; }
    public bool IsDirty => DiskHash == null || Content != _savedText;
    [ObservableProperty] public partial string Content { get; set; } = "";
    [ObservableProperty] public partial int CaretOffset { get; set; }
    [ObservableProperty] public partial int CaretLine { get; set; } = 1;
    [ObservableProperty] public partial int CaretColumn { get; set; } = 1;
    [ObservableProperty] public partial bool HasExternalChange { get; set; }
    public event EventHandler? ContentChanged;
    partial void OnContentChanged(string value)
    {
        Version++;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
    public void MarkSaved(string hash)
    {
        _savedText = Content;
        DiskHash = hash;
        HasExternalChange = false;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }
    public void MarkSavedSnapshot(string text, string hash)
    {
        _savedText = text;
        DiskHash = hash;
        HasExternalChange = false;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }
}
