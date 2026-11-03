using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Profiling;
using WpfStudio.Contracts;
using WpfStudio.Core;
using WpfStudio.Core.Documents;
using WpfStudio.Core.Wpf;
using WpfStudio.Database.ViewModels;
using WpfStudio.Runtime.Debugging;
using WpfStudio.Runtime.Terminal;
using WpfStudio.Workspace;
using WpfStudio.Workspace.Xaml;

namespace WpfStudio.App.Services;

/// <summary>The feature panes the container builds and the shell shows.</summary>
public sealed record ShellPanes(DebuggerViewModel Debugger, TerminalViewModel Terminal, DatabasePaneViewModel Database, DesignerViewModel Designer, MemoryProfilerViewModel MemoryProfiler);

/// <summary>The language and project services behind the shell's editing and build commands.</summary>
public sealed record ShellLanguageServices(WorkspaceClient Workspace, BuildService Build, WpfIndexService Indexer, ScaffoldingService Scaffolding,
    WorkspaceEditTransaction Edits, XamlCompletionService Xaml, XamlFormattingService XamlFormatter);

/// <summary>How the shell reaches the user's desktop: dialogs, the shell and clipboard, and the theme.</summary>
public sealed record ShellDesktopServices(IFileDialogService Files, IUserDialogService Dialogs, IDesktopService Desktop, IClipboardService Clipboard, IThemeService Theme);

/// <summary>
/// What the tools the shell creates need from the container. They take the shell as their host, so the container cannot
/// build them itself, and the shell only hands these on.
/// </summary>
public sealed record ShellToolServices(WorkspaceStateStore<UserTaskItem> Tasks, WorkspaceStateStore<FileGroup> FileGroups,
    OpenDocumentWatcherFactory WatchOpenDocuments, KnownSourceWatcherFactory WatchSources);
