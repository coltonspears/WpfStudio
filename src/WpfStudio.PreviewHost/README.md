# Isolated WPF preview host

This executable owns a WPF dispatcher and an offscreen presentation source. The
editor talks to `IPreviewRpc` over a single current-user-only
named pipe. Disconnect ends the process; the client terminates an owned host when
rendering is cancelled or times out. Project constructors never run in the IDE.

The current host targets .NET 10 on Windows. It supports loose WPF XAML,
Window/Page content, resource dictionaries, dependency-property inspection,
binding status, element picking and reversible preview-only property overrides.
Source identities attached during parsing survive templates without changing
namescopes. Framework-generated visuals have no invented source location.

`CaptureAsync` updates the bitmap, tree and current binding observations on the
existing instance. It preserves node identities for surviving objects, scenario
data, constructors, temporary overrides and their reset baseline. It checks the
requested revision and presentation-source lifetime around a normal dispatcher
idle pass, without reloading XAML or forcing layout constraints. Historical WPF
binding traces are labeled separately from current node/property failures.
The engine enables WPF tracing once before registering its listener and retains
the exact trace-source instance for cleanup. Without that opt-in, WPF's first
binding initialization can replace the source and silently drop the listener
when no debugger is attached.

Stop revokes active and queued requests before waiting for the transport gate.
Requests made afterward wait for cleanup and can create a fresh host. Cleanup
terminates only the pinned preview process; programs started by project code
are not recursively terminated based on parent PID relationships.

In source mode, `x:Class` and event handlers are omitted with diagnostics.
Standard WPF design mode is enabled before custom control construction. Project assemblies
must be local DLLs within the caller's selected project directory. The host copies
the complete output subtree to a client-owned temporary directory before loading;
managed, satellite and native dependencies resolve from that copy so loaded code
does not lock project build outputs. A rebuilt entry assembly triggers a fresh
host. The client removes only its verified temporary copy after the host exits.
This is process isolation, not a security sandbox: custom constructors,
markup extensions, converters and XAML resources still execute or load in the
host.

Compiled mode is an explicit opt-in through `PreviewMode.Compiled`, a built
assembly and the full view type name. It invokes the built view's parameterless
constructor or an explicitly selected scenario view factory. Normal view
construction runs `InitializeComponent`, including view event connections. Window,
Page and UserControl retain their actual types; compiled windows retain their
ancestor/resource scopes on an offscreen native surface. Each compiled render
starts a fresh host. Returned build provenance includes the loaded assembly SHA256
and module version ID. Unsaved source is not included and compiled nodes expose
no current-buffer source locations or source-write capabilities.

The optional `ApplicationResourcePath` defaults to `App.xaml`; clear it for a
view without application resources. The host extracts the compiled
`Application.Resources` BAML subtree using public WPF reader APIs, including
merged resource dictionaries. It does not instantiate the project's `App`.
Avoiding `App.Run` alone is insufficient: the official WPF implementation queues
startup from the [Application constructor](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Application.cs).
The resource loader uses [Baml2006Reader](https://learn.microsoft.com/en-us/dotnet/api/system.windows.baml2006.baml2006reader?view=windowsdesktop-10.0)
with the WPF [XamlReader.Load overload](https://learn.microsoft.com/en-us/dotnet/api/system.windows.markup.xamlreader.load?view=windowsdesktop-10.0).

Before loading compiled resources, the dedicated host sets the project as its
entry and WPF resource assembly using public .NET/WPF APIs. This lets absolute
`pack://application:,,,/` references and `Application.GetResourceStream` resolve
against the project. This process-wide identity is another reason each compiled
render requires a fresh host; the project entry point is never invoked.

Property validation converts values without applying setters. Property identities
include declaring owner type and assembly, and ambiguous display names are
rejected. Source capabilities are checked against the original authored element
type, independently of temporary host adaptations. Display values are separate
from complete invariant scalar literals. Temporary TextBlock.Text edits are
disabled where they would destroy authored or formatted inline content.

Explicit named scenarios can invoke public static project view/data factories,
including developer-supplied DI composition. Every scenario render uses a fresh
host; data factories replace the root DataContext before layout without writing
through an existing TwoWay binding. See [preview scenarios](../../docs/xaml-preview-scenarios.md)
for configuration and design-time value support. Factory discovery never loads
project code into the IDE. The host does not infer project application startup,
dependency-injection composition or a running application's state.
It does not promise .NET Framework or arbitrary project runtime/bitness fidelity.
Supporting those requires separate host builds selected by project target runtime
and architecture, while retaining the transport-neutral records in
`WpfStudio.Contracts/PreviewContracts.cs`. The inspection logic in `PreviewEngine`
can then be extracted for an opt-in running-application agent.

Validate with:

```powershell
dotnet test tests/WpfStudio.Preview.Tests/WpfStudio.Preview.Tests.csproj --configuration Release
```

The suite includes a real compiled WPF application fixture whose App constructor
and startup handlers throw if executed, proving resource extraction avoids both.
It also includes STA layout/binding tests and real process/RPC tests for host
recovery, source mapping, property reset, hanging custom constructors,
cancellation, timeout, crash, disposal and assembly rebuilds.

After publishing, validate the exact packaged host (the test rejects missing
companions and checks the running executable path):

```powershell
$env:WPFSTUDIO_PREVIEW_HOST_UNDER_TEST = (Resolve-Path artifacts/WpfStudio/PreviewHost/WpfStudio.PreviewHost.exe).Path
try {
    dotnet test tests/WpfStudio.Preview.Tests/WpfStudio.Preview.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~CompiledPreviewTests|FullyQualifiedName~SourceWritebackRoundTripTests|FullyQualifiedName~ExplicitHostRendersPicksReportsBindingFailuresAndResetsProperties'
} finally {
    Remove-Item Env:WPFSTUDIO_PREVIEW_HOST_UNDER_TEST
}
```
