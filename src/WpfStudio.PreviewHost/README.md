# Isolated WPF preview host

This executable owns a WPF dispatcher and an offscreen presentation source. The
editor talks to `IPreviewRpc` over a single current-user-only
named pipe. Disconnect ends the process; the client terminates an owned host when
rendering is cancelled or times out. Project constructors never run in the IDE.

This project is the host for .NET on Windows and, built for net48, the engine of the
[.NET Framework host](#net-framework-host). It supports loose WPF XAML,
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
Source Windows retain their real `Window` type on a hidden native surface, so
Window-only attached behaviors, `WindowChrome` and `AncestorType=Window` bindings
work. The project's view constructor and code-behind are still omitted. Page
navigation remains omitted through the content-only adaptation.
Standard WPF design mode is enabled before custom control construction. Project assemblies
must be local DLLs within the caller's selected project directory. The host copies
the complete output subtree to a client-owned temporary directory before loading.
The built `.deps.json` also supplies runtime package assets missing from ordinary
class-library output. The evaluated `ProjectAssetsFile` supplies the restored
package folders, including custom locations; only exact versions from the built
runtime target are considered. Copy-local output wins. Runtime-specific managed
and native assets use the host's compatible runtime identifiers, and satellite
culture directories are preserved. Compiler reference assemblies are not loaded.
All selected assets are copied into the owned directory so loaded code does not
lock project outputs or package-cache files. A change to output files, manifests,
restore metadata or selected package assets triggers a fresh host on the next
render, including when the entry DLL itself is unchanged. The client removes only
its verified temporary copy after the host exits.

Before parsing source, explicit `clr-namespace` assembly references are resolved
and referenced libraries with matching `XmlnsDefinition` attributes are discovered
from assembly metadata. This covers XAML-only references and lets event suppression
and source mapping recognize custom controls on the first render. Assembly-qualified
resource-dictionary sources such as `/Controls;component/Themes/Colors.xaml` use
the pack scheme; ordinary relative sources retain the authored file's base URI.
See [multi-project dependency loading](../../docs/xaml-preview-dependencies.md).
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

The optional `ApplicationResourcePath` defaults to `App.xaml` and now applies to
ordinary source previews as well as compiled previews and named scenarios. Clear
it to omit application resources. A default source preview with no matching
compiled application resource continues with an informational diagnostic, allowing
control libraries and applications with empty resources. A malformed resource
still fails, and compiled/scenario requests still require the selected resource.
Source refreshes can reuse the same application assembly identity and reload its
resources without constructing `App`. The host extracts the compiled
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

When a view has no root DataContext and observed binding sources are unavailable,
the preview status explains how to provide a scenario data factory. Individual
binding failures remain available; the notice does not hide errors or fabricate
application data by running startup.

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
It does not promise fidelity for .NET versions before 10. The inspection logic in
`PreviewEngine` can be extracted for an opt-in running-application agent.

## .NET Framework host

A build for .NET Framework cannot be previewed on .NET: a library written for it can
call members .NET does not have, such as `AppDomainSetup.AppDomainManagerType`, which
DevExpress reads while initializing its themes. `PreviewHostSelection` reads the built
assembly and `PreviewClient` starts the matching executable:

| Project | Role |
| --- | --- |
| `WpfStudio.PreviewHost.Framework` | The process and its JSON-RPC transport, AnyCPU |
| `WpfStudio.PreviewHost.Framework32` | The same, in a 32-bit process |
| `WpfStudio.PreviewHost` (net48) | The engine, the same source as the .NET host |

The difficulty is that .NET Framework has one binder per application domain, and
StreamJsonRpc brings about 25 libraries that real projects also use at other versions:
`System.Memory`, `Newtonsoft.Json`, `System.Text.Json` and so on. So there are two domains.
The default domain holds the pipe and the transport and never loads project code. The first
render copies the output and creates a second domain with the output as its application
base and the build's configuration file, so project code binds exactly as in its own process.
`FrameworkEngineServer` runs the ordinary `PreviewEngine` there on its own STA thread.

Calls cross between the domains as a method name and JSON text, so the contract records
need no serialization support. The method list comes from `IPreviewRpc`, so a new
method needs nothing here.

**The engine's references are the isolation.** On net48 this project references only
.NET Framework, its own libraries and `Newtonsoft.Json`, whose private copy loads side by
side with a project's other version. Keeping the transport's packages out of this project
is what makes that a compile-time fact: with `System.Memory` merely referenced, C# binds
`array.Contains(...)` to a method in it, and the engine would need the package at run time.
Metadata reading needs `System.Reflection.Metadata`, so the engine asks the default domain
to do that through `FrameworkHostServices`. `Directory.Build.targets` and
`src/Shared/NetFrameworkCompatibility.cs` provide the newer language and library members as
source, compiled into each assembly. Do not add a package to the net48 engine.

`PreviewAppDomainManager` is the project domain's manager. It registers the side-by-side
lookup before any engine code runs, and answers `Assembly.GetEntryAssembly()`, which is
how the project becomes the application: WPF only accepts `Application.ResourceAssembly`
while there is no entry assembly, and .NET Framework has no `Assembly.SetEntryAssembly`.

To run the whole Preview suite against this host (tests that load .NET fixtures are
expected to fail, because those builds cannot run on .NET Framework):

```powershell
$env:WPFSTUDIO_PREVIEW_HOST_UNDER_TEST = (Resolve-Path src/WpfStudio.PreviewHost.Framework/bin/Debug/net48/WpfStudio.PreviewHost.Framework.exe).Path
dotnet test tests/WpfStudio.Preview.Tests/WpfStudio.Preview.Tests.csproj
```

`FrameworkPreviewTests` needs no setting: real net48 fixtures choose this host themselves.

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
