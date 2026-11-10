using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Services;
using WpfStudio.Contracts;

namespace WpfStudio.App.Features.Workbench;

/// <summary>The Problems pane: the shell's diagnostics narrowed by severity, scope and search text.</summary>
public sealed partial class ProblemsViewModel : ObservableObject
{
    public const string AllFilesScope = "All files", OpenFilesScope = "Open files", CurrentFileScope = "Current file";
    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;
    private readonly IWorkbenchHost _host;
    private readonly ObservableCollection<WorkspaceDiagnostic> _all;

    /// <param name="all">Every current diagnostic; the owner calls <see cref="Refresh"/> after rebuilding it.</param>
    public ProblemsViewModel(IWorkbenchHost host, ObservableCollection<WorkspaceDiagnostic> all)
    {
        _host = host; _all = all;
        host.DocumentsChanged += () => { if (Scope != AllFilesScope) Refresh(); };
    }

    /// <summary>The rows the pane shows.</summary>
    public BulkObservableCollection<WorkspaceDiagnostic> Visible { get; } = [];
    public IReadOnlyList<string> Scopes { get; } = [AllFilesScope, OpenFilesScope, CurrentFileScope];
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial string Scope { get; set; } = AllFilesScope;
    [ObservableProperty] public partial bool ShowErrors { get; set; } = true;
    [ObservableProperty] public partial bool ShowWarnings { get; set; } = true;
    [ObservableProperty] public partial bool ShowMessages { get; set; } = true;
    [ObservableProperty] public partial WorkspaceDiagnostic? Selected { get; set; }
    partial void OnFilterChanged(string value) => Refresh();
    partial void OnScopeChanged(string value) => Refresh();
    partial void OnShowErrorsChanged(bool value) => Refresh();
    partial void OnShowWarningsChanged(bool value) => Refresh();
    partial void OnShowMessagesChanged(bool value) => Refresh();

    public int ErrorCount => _all.Count(d => d.Severity.Equals("Error", IgnoreCase));
    public int WarningCount => _all.Count(d => d.Severity.Equals("Warning", IgnoreCase));
    public int MessageCount => _all.Count - ErrorCount - WarningCount;
    public bool IsFilterActive => Filter.Trim().Length > 0 || Scope != AllFilesScope || !ShowErrors || !ShowWarnings || !ShowMessages;
    /// <summary>"12 of 40 shown" while a filter hides rows; empty otherwise.</summary>
    public string FilterSummary => IsFilterActive && _all.Count > 0 ? $"{Visible.Count} of {_all.Count} shown" : "";
    [RelayCommand] private void ClearFilter() { Filter = ""; Scope = AllFilesScope; ShowErrors = ShowWarnings = ShowMessages = true; }

    /// <summary>Rebuilds <see cref="Visible"/> and the counts from the current diagnostics.</summary>
    public void Refresh()
    {
        var selected = Selected;
        var terms = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var open = Scope == OpenFilesScope ? _host.OpenDocumentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var current = Scope == CurrentFileScope ? _host.ActiveLocation?.Path : null;
        var visible = new List<WorkspaceDiagnostic>();
        foreach (var diagnostic in _all)
        {
            if (!SeverityShown(diagnostic.Severity)) continue;
            if (open != null && (diagnostic.Path == null || !open.Contains(diagnostic.Path))) continue;
            if (Scope == CurrentFileScope && (current == null || !string.Equals(diagnostic.Path, current, IgnoreCase))) continue;
            if (!terms.All(term => Matches(diagnostic, term))) continue;
            visible.Add(diagnostic);
        }
        // One notification for the bound list instead of one per row; this runs on every diagnostics change.
        Visible.ReplaceAll(visible);
        if (selected != null && !Visible.Contains(selected)) Selected = null;
        OnPropertyChanged(nameof(ErrorCount)); OnPropertyChanged(nameof(WarningCount)); OnPropertyChanged(nameof(MessageCount));
        OnPropertyChanged(nameof(IsFilterActive)); OnPropertyChanged(nameof(FilterSummary));
    }
    private bool SeverityShown(string severity) =>
        severity.Equals("Error", IgnoreCase) ? ShowErrors : severity.Equals("Warning", IgnoreCase) ? ShowWarnings : ShowMessages;
    /// <summary>A term matches the code, message, file name or project; <c>-term</c> excludes rows that match it.</summary>
    internal static bool Matches(WorkspaceDiagnostic diagnostic, string term)
    {
        if (term.Length > 1 && term[0] == '-') return !Matches(diagnostic, term[1..]);
        return diagnostic.Id.Contains(term, IgnoreCase) || diagnostic.Message.Contains(term, IgnoreCase)
            || (diagnostic.Path != null && Path.GetFileName(diagnostic.Path).Contains(term, IgnoreCase))
            || (diagnostic.ProjectName?.Contains(term, IgnoreCase) ?? false);
    }
}
