using WpfStudio.Core;

namespace WpfStudio.App.Services;

/// <summary>
/// What the tools the shell creates need from the container. They take the shell as their host, so the container cannot
/// build them itself, and the shell only hands these on.
/// </summary>
public sealed record ShellToolServices(WorkspaceStateStore<UserTaskItem> Tasks, WorkspaceStateStore<FileGroup> FileGroups,
    OpenDocumentWatcherFactory WatchOpenDocuments, KnownSourceWatcherFactory WatchSources);
