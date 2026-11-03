namespace WpfStudio.Runtime.Debugging;

/// <summary>The debugged program paused or resumed. Sent on the UI thread.</summary>
public sealed record DebuggerStateMessage(bool IsStopped);
/// <summary>The debugger wants a source line shown: the selected frame, or a chosen breakpoint. Sent on the UI thread.</summary>
public sealed record DebuggerSourceMessage(string Path, int Line);
