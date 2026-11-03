using WpfStudio.Contracts;
using WpfStudio.Core.Documents;

namespace WpfStudio.App.Services;

// Facts one feature announces through the container's IMessenger, for whichever features care. Handlers run on the
// sender's thread, so send on the UI thread; a service event that arrives elsewhere goes through IUiDispatcher first.
// Request and response stay on the one-owner hosts, and strict parent-child pairs keep direct events.

/// <summary>The shell's project model was replaced or closed.</summary>
public sealed record WorkspaceChangedMessage(WorkspaceSnapshot? Workspace);
/// <summary>A feature wants a tool window shown.</summary>
public sealed record ShowToolMessage(string ToolId);
/// <summary>A document reached disk, through an explicit save or the auto-save timer.</summary>
public sealed record DocumentSavedMessage(DocumentState Document);
/// <summary>Brushes and window frames now use this palette.</summary>
public sealed record ThemeChangedMessage(Palette Palette);
/// <summary>The assistant's settings were loaded or saved, so whether it has an API key may have changed.</summary>
public sealed record AssistantSettingsSavedMessage;
