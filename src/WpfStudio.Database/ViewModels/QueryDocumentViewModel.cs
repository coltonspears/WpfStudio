using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfStudio.Database.Models;

namespace WpfStudio.Database.ViewModels;

public partial class QueryDocumentViewModel(string title, string sql) : ObservableObject
{
    private string _savedText = sql;
    internal string SavedText => _savedText;
    public Guid RecoveryId { get; init; } = Guid.NewGuid();
    public string Title { get; } = title;
    public string DisplayTitle => Title + (IsDirty ? " *" : "");
    public bool IsDirty => SqlText != _savedText;
    public string? FilePath { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] [NotifyPropertyChangedFor(nameof(DisplayTitle))] public partial string SqlText { get; set; } = sql;
    [ObservableProperty] public partial string SelectedText { get; set; } = "";
    [ObservableProperty] public partial int SelectionStartLine { get; set; } = 1;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Ready";
    [ObservableProperty] public partial string Target { get; set; } = "No query executed";
    [ObservableProperty] public partial string Messages { get; set; } = "";
    [ObservableProperty] public partial QueryResultSet? SelectedResult { get; set; }
    public ObservableCollection<QueryResultSet> Results { get; } = [];
    public void MarkSaved(string? savedText = null) { _savedText = savedText ?? SqlText; OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(DisplayTitle)); }
}
