# Isolated interactive preview probe

This is an independent two-process WPF harness, not production implementation. It has no project/package references. Both processes run the same executable and PerMonitorV2 manifest. This diagnostic harness is kept separate from the production app. Automated runs measure ownership and hang recovery; they do not establish natural input fidelity.

The parent `HwndHost` creates and owns a local `STATIC` child HWND. The child process authenticates the current-user pipe server PID, bridge owner PID, session token and per-session HWND property, checks actual DPI contexts, then creates its own child `HwndSource` with `TreatAsInputRoot=true`. The parent authenticates the pipe client PID and returned child HWND. Only the parent’s local bridge is returned as `HwndHost.Handle`.

`--surface window` instead creates and preserves an actual WPF `Window`, hides it, changes its HWND styles and parents that HWND to the bridge. It logs `Window.GetWindow(root)` identity. This is a distinct compatibility experiment; it must not be inferred from the ordinary child-source result.

## Build and run

```powershell
dotnet build tools/InteractivePreviewProbe/InteractivePreviewProbe.csproj -c Release
```

Run each automated case with its own log. The automated parent starts offscreen, with `ShowActivated=false` and `ShowInTaskbar=false`; its child is also never activated as a foreground window. No code uses `SendInput`, `SetCursorPos`, `SetForegroundWindow`, synthetic WPF routed events or production IDE processes.

```powershell
$probeExe = 'C:\Projects\repos\WpfStudio\tools\InteractivePreviewProbe\bin\Release\net10.0-windows\InteractivePreviewProbe.exe'
$probeRun = Start-Process -FilePath $probeExe -ArgumentList @('--auto-message-smoke', '--surface', 'child', '--log', 'C:\Projects\repos\WpfStudio\artifacts\probes\interactive-preview\child.jsonl') -WindowStyle Hidden -PassThru
$null = $probeRun.Handle
if (-not $probeRun.WaitForExit(40000)) { $probeRun.Kill(); throw 'Probe parent exceeded its deadline.' }
$probeRun.ExitCode
$probeRun.Dispose()

$probeRun = Start-Process -FilePath $probeExe -ArgumentList @('--auto-message-smoke', '--surface', 'window', '--log', 'C:\Projects\repos\WpfStudio\artifacts\probes\interactive-preview\window.jsonl') -WindowStyle Hidden -PassThru
$null = $probeRun.Handle
if (-not $probeRun.WaitForExit(40000)) { $probeRun.Kill(); throw 'Probe parent exceeded its deadline.' }
$probeRun.ExitCode
$probeRun.Dispose()
```

The parent’s background watchdog checks dispatcher heartbeats every 250 ms, kills its pinned child `Process` after 3 seconds without a dispatcher response, and never kills a process tree. The automated parent has an independent 30-second background exit deadline. It records its child PID and executable path. If interrupted externally before the watchdog runs, cleanup must use that exact already-observed child instance, not a broad process-name kill. Automated popups are transparent, non-hit-testable and noncapturing, and are hidden on opening because WPF can clamp an offscreen popup onto a monitor; visible popup interaction remains a manual check.

Exit `0` means authenticated child/DPI attachment, an owned popup HWND, removal of the child and popup HWNDs, and parent-dispatcher recovery were observed. Exit `2` is a failed observation/protocol path, and exit `3` is the parent hard deadline.

## What the automated run actually measures

- Native HWND ownership, parentage and matching awareness contexts.
- Posted `WM_CHAR`, Tab and mouse-message routing, with WndProc and WPF event logs. The parent requests these operations through RPC; only the child’s owning dispatcher posts to its current, validated `HwndSource.Handle`. The parent never posts input to a cached foreign HWND.
- Explicit host focus entry through the actual `IKeyboardInputSink.TabInto` path; remote tab-out uses `IKeyboardInputSite` and an asynchronous parent notification.
- A host-opened popup’s real HWND and cleanup when the host is hidden.
- An intentionally blocked child dispatcher followed by background-only process termination. The parent attempts `SetFocus` only on its own bridge while the child is blocked. `parent-focus-returned` and `parent-dispatcher-gap` show whether that operation/input relationship actually stalled this run.
- Parent dispatch after child termination and confirmation that owned HWNDs disappeared.

The `posted-message-observations` booleans are deliberately **observations**, not proof of physical input support. Posting messages bypasses input queues, keyboard state, hooks, pointer position/capture, dead-key translation and IME. Some of those observations may fail while real interaction works, or succeed while real interaction does not. Popup opening is explicitly requested by the harness; it does not prove a real ComboBox click works. An offscreen run cannot certify physical keyboard/mouse, touch, accessibility, foreground activation or mixed-monitor DPI behavior.

## Manual matrix after automated evidence

Only launch visible mode when the user explicitly wants an interactive test window; omit `--auto-message-smoke`. It opens ordinary parent controls before/after the embedded surface plus remote TextBox, Button, ComboBox, Slider, Popup, ContextMenu and a deliberate hang button. No global input injection is used.

Check both surface modes:

1. Mouse click and typing; Tab and Shift+Tab into, within and out of the embedded controls; clipboard shortcuts; Alt mnemonics; IME and dead keys. Confirm the IDE’s eventual shortcut policy does not consume target shortcuts.
2. Slider dragging with capture across/outside the bounds; scroll; popup/menu/tooltip position and input; minimize, deactivate and hide cleanup.
3. Resize/dock transitions and movement between 100%, 125%, 150% and 200% monitors. The probe uses actual client pixels for resize; it does not claim WPF ScaleTransform/ScrollViewer clipping works for HWND airspace.
4. Hang while a remote control has focus, then try a parent control. Record the pause and watchdog recovery; a cross-process parent/child relationship does not provide independent input queues.
5. Close parent while popup is open; close/hang child; verify no owned process/window remains.

## Primary sources and production constraints

- [HwndSource input-root behavior](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.hwndsourceparameters.treatasinputroot?view=windowsdesktop-10.0): a child source defaults to not preprocessing its own message pump. Explicit input-root setup is needed here.
- [SetParent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent): styles are not adjusted automatically; differing DPI awareness can reset the child process’s awareness.
- [HwndHost source](https://raw.githubusercontent.com/dotnet/wpf/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Interop/HwndHost.cs): only same-process/thread HWNDs are subclassed. A local bridge prevents foreign HWND reuse from becoming a stale `HwndHost.Handle`.
- [Cross-process parent/child input coupling](https://devblogs.microsoft.com/oldnewthing/20130607-00/?p=4143): cross-thread parent/child relationships implicitly attach input queues. A watchdog bounds an observed hang; it does not remove that architectural coupling.
- [Posted messages are not keyboard input](https://devblogs.microsoft.com/oldnewthing/20250319-00/?p=110979): native-message smoke results cannot certify natural input equivalence.
- [WPF interoperation regions](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/technology-regions-overview): the HWND occupies native airspace. Inspect overlays belong in the remote process or a distinct screenshot mode.

Production API should use an opaque render/session lease plus native parent handle, expected owner PID and matching awareness. Attach/resize/focus/hide/detach commands must validate the current lease on the host dispatcher. UI creation must never await RPC inside `BuildWindowCore`. Remote HWND lifecycle calls remain in its process; local destruction must occur only after bounded remote detach/termination. PreviewClient needs an independent dispatcher heartbeat during interaction, not just a timeout on user-requested RPCs. A source/compiled/scenario restart must invalidate the lease before old callbacks can attach or focus a replacement session. This harness does not yet prove those full production races, app code closing/reconfiguring its own Window, dialogs, multiple windows or UI Automation integration.
