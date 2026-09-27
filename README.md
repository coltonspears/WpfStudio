# WpfStudio

A native Windows IDE focused on WPF, C#, CommunityToolkit.Mvvm, and SQL Server. It combines a dockable editor with an isolated Roslyn/MSBuild worker, debugging, an embedded terminal, WPF navigation, NuGet packages, Git, a SQL workbench, and the ColtonGPT assistant.

## Run from source

Use Windows 11 x64 with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Install the Windows Desktop runtimes needed by the applications you develop; .NET 8, 9, and 10 projects can select their SDK through `global.json`. The full Visual Studio IDE is not required.

The terminal needs the free [Microsoft Edge WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/). PowerShell 7 is preferred when installed; Windows PowerShell and Command Prompt also work. Git integration uses an installed `git` command and its existing authentication configuration. ColtonGPT requires your OpenRouter API key and a model your account can access. SQL tools require access to a SQL Server instance. SQL Server LocalDB is optional and enables the database integration test.

```powershell
dotnet restore WpfStudio.sln
./tools/Get-Debugger.ps1
dotnet build WpfStudio.sln
dotnet run --project src/WpfStudio.App
```

Pass a workspace path to open it at launch:

```powershell
dotnet run --project src/WpfStudio.App -- C:\Work\MyApp\MyApp.sln
```

`Get-Debugger.ps1` downloads netcoredbg 3.2.0-1092 and verifies the pinned archive checksum. Terminal JavaScript assets are already included; installing Node.js or npm is unnecessary.

The IDE opens `WpfStudio.App.MainWindow`, resolved and shown by `App.OnStartup` in `src/WpfStudio.App/App.xaml.cs`. Its content is defined in `MainWindow.xaml`; Windows draws the native title bar and border. The smoke-test screenshots capture only the WPF content, so they omit that title bar. Theme and docking layout are restored from your local profile. The startup-project selector inside the IDE selects the application to run or debug, rather than the IDE's own window.

## Daily workflow

**Open and navigate.** Open a `.sln`, `.slnx`, or SDK-style `.csproj`. The solution tree uses vector icons for projects, folders, C#, XAML, and other files. Use quick-open for a filename and solution search for text. Right-click a tree item to open its project file, select the startup project, manage packages, reveal its folder, or copy its path. Keep an eye on Output when SDK discovery or restore fails: files remain accessible even if language services cannot fully load the project.

**Edit and refactor.** C# completion, signature assistance, definitions, references, rename, and formatting run through the worker. Right-click in the editor or press **Ctrl+.** to open its actions. Refactor includes symbol rename, organize usings, and switching a local declaration between `var` and an explicit type. These changes are previewed, checked against current document versions, and undoable. Type conversions reject cases that could change meaning; organizing usings avoids moving comments or conditional directives.

**Build and debug.** Select the startup project, configuration, target framework, and launch profile. Restore, build, rebuild, clean, run, and test use the installed `dotnet` toolchain. Modified source documents are saved before build/debug; a failed save stops the operation. Click the C# editor gutter or press **F9** to toggle a breakpoint. Right-click the line and choose **Breakpoint > Condition** or **Enable / disable on this line**; **Debug > Show breakpoints** opens the list. Gutter markers and tooltips distinguish breakpoint state and show conditions. The Debug pane provides stack frames, threads, locals, watches, exception settings, and output. Attach by managed process ID. Stop ends a launched application; Detach releases an attached process.

**Understand WPF Explorer.** Open **View > WPF Explorer**. Its Components tab is a searchable index of your source: filter views and controls, view models, dictionaries, styles and templates, keyed resources, converters, or assets. Selecting an item explains its role and shows its source file; double-click to navigate. For a resource key, the details list XAML references that can be resolved to that declaration. Same-key references needing runtime context are counted separately. The Issues tab separates definite errors from informational cases that static analysis cannot settle. This is source analysis; controls are never instantiated.

Create paired view/view-model files, controls, dictionaries, or converters with **New item** and preview the generated files. Related-file navigation connects XAML, code-behind, and conventional view models. Right-click a resource to open its declaration or preview a key rename. Asset tools expose import, build actions, and pack URIs. Refresh the index after editing resource declarations. XAML assistance uses statically known types, resource keys, and DataContext information.

**Manage packages.** Open **Tools > Manage NuGet packages**, choose the target project, and inspect Installed or search Browse. Select a version, optionally include prereleases, then explicitly install, update, or remove a reference. Package output shows restore failures and cancellation. Changes save modified documents first, use the project's `dotnet` SDK and NuGet configuration, and refresh the language workspace afterward. Central package versions are shown with a reminder that an update can affect other projects. Browse supports public NuGet V3 search; local or authenticated feeds can use an exact package ID/version through existing CLI credentials or credential providers. Install/restore respects all configured sources and source mappings. Use the terminal when a feed requires interactive sign-in.

**Use Git.** Open **Tools > Git changes** to inspect working and staged changes, view diffs, stage or unstage files, and commit the staged snapshot. History shows recent commits and their diffs. Create or switch branches, fetch, pull, or push through explicit commands. Pull is fast-forward only; branch changes and pull require a clean working tree. Save failures stop operations that first need current editor contents. Existing Git credentials are used without an interactive sign-in prompt; authenticate through the terminal if needed. Merge/rebase resolution and advanced repository administration remain terminal workflows.

**Ask ColtonGPT.** Open **Tools > Settings**, enter an OpenRouter API key, choose **Load models** or enter a model ID, then save. The key is protected for your Windows account under the local WpfStudio profile. Open **View > ColtonGPT** or **Ask ColtonGPT** from the editor menu. Choose no editor context, selected text, or the current document, and inspect **Preview exactly what will be sent** before sending with **Ctrl+Enter**. Messages and attached code go to OpenRouter and the selected model provider. Responses stream into the conversation; Stop cancels the request. The assistant cannot execute commands or edit files. Conversations remain in memory, and **New chat** clears history and editor-context sharing.

**Use the terminal.** Open Terminal, choose a shell, and create a session. Sessions retain their process across docking changes. Ctrl+C interrupts the active process; Ctrl+Shift+F searches scrollback. Closing a session cleans up its owned processes. The terminal uses local xterm assets, WebView2, and Windows ConPTY.

**Work with SQL Server.** Open SQL Server connections, enter a server/database, and choose Windows or SQL authentication. Encryption and certificate validation start enabled. Saving a SQL password is optional; remembered passwords are protected for the current Windows user. Connect refreshes the database list. Expand database objects on demand, select an object, and choose Use database or Open definition. The query target is always shown above the editor.

Create or open a SQL script, then explicitly Execute it. A selection executes only that selection; otherwise the full script executes. Ctrl+Space suggests SQL keywords and names from the schema objects you have browsed. Results are shown in separate grids, messages retain original script line numbers, and Cancel interrupts execution. Export CSV writes the displayed result set, including only displayed rows when a result is truncated.

Closing a modified SQL script prompts to save, discard, or cancel. Saving an opened script checks for external content changes before overwriting it, and an edit made while saving keeps the script open and dirty. Unsaved query tabs are saved to an atomic recovery file after a short typing pause; the SQL pane offers to restore them after an interrupted session. Recovery stores script text and file paths, including the previous saved text for conflict checks; it excludes connection passwords, results, and messages. Saving, discarding a tab, or completing a clean application close removes the corresponding recovery copies.

`GO` batches within one execution share a connection, so temporary tables and transactions work across batches. A new execution opens a new connection. `GO n`, SQLCMD directives, and SQLCMD variables are rejected before running the script. Results retain up to 10,000 rows or 16 MiB per result set, with at most 32 result sets and 64 MiB estimated display memory across one execution. The estimate includes row, column, and cell overhead so wide rows containing nulls still count. A single value retains up to 65,536 characters. Messages are limited to 1,000 entries or 256 KiB. Limits are shown explicitly; extra results are drained and later statements continue executing. These display limits never cancel SQL automatically. Table definitions show column declarations; keys and indexes can be inspected separately in the tree.

## Keyboard and layout

| Command | Shortcut |
| --- | --- |
| Open solution or project | Ctrl+Shift+O |
| Open file | Ctrl+O |
| Build | Ctrl+Shift+B |
| Start debugging | F5 |
| Run without debugging | Ctrl+F5 |
| Create WPF item | Ctrl+Shift+N |
| Completion in an editor | Ctrl+Space |
| Editor actions / refactoring menu | Ctrl+. |
| Rename C# symbol | F2 |
| Toggle C# breakpoint | F9 |
| Send ColtonGPT message | Ctrl+Enter |
| Execute SQL when focus is in SQL pane | F5 |
| Search terminal scrollback | Ctrl+Shift+F |

Use the command palette for the remaining commands. Drag document and tool tabs to split, float, or dock them. Auto-hide side tools to increase editing space. Saved layouts can be restored or reset. Light and dark themes are available.

## Verify and package

```powershell
dotnet test WpfStudio.sln
./tools/Publish.ps1 -Zip
```

The publish script runs tests, publishes Windows x64 app and worker outputs, checks that Roslyn BuildHost, terminal assets, and debugger files exist, and writes the portable app under `artifacts/WpfStudio`. `-Zip` also creates `artifacts/WpfStudio-win-x64.zip`. Copy the complete directory and launch `WpfStudio.exe`; preserve `WorkspaceHost/`, `debugger/`, `Assets/`, and their dependency files. The default package requires the .NET 10 Windows Desktop runtime. `-SelfContained` includes that runtime but you still need the SDKs used to build your projects. `-SkipTests` is available for repeated local packaging after verification.

Before first launch, run `./Check-Prerequisites.ps1` from the portable folder (or `./tools/Check-Prerequisites.ps1` from the source repository). It checks Windows 11 x64, the .NET 10 SDK, the Desktop runtime or bundled runtime, and workspace-host files. Optional checks report missing WebView2, terminal assets, or debugger files with remediation links. It installs nothing. Use `-AsJson` for a machine-readable report, and `-ApplicationDirectory C:\Path\To\WpfStudio` to check another package; missing required components produce exit code 1.

Database tests cover batch parsing, DPAPI round-trips, display limits, CSV, view-model behavior, and WPF bindings. When LocalDB is installed, a uniquely named disposable instance verifies real SQL execution, authentication failures, cancellation, source-line mapping, and result truncation; it is deleted afterward. Other test projects verify document transactions, WPF analysis, workspace/language operations, and runtime features. Test output is the source of truth for any unavailable or skipped integration prerequisites.

Settings, layouts, recovery data, and SQL connection profiles live under `%LOCALAPPDATA%\WpfStudio`. Project sources remain in their original directories. Caches and optional feature processes are kept separate from the UI process where practical.

## Architecture and boundaries

| Project | Responsibility |
| --- | --- |
| `WpfStudio.App` | WPF shell, docking, editor, package/Git/ColtonGPT tools, application composition |
| `WpfStudio.Contracts` | Versioned documents, diagnostics, workspace edits, process-neutral interfaces |
| `WpfStudio.Core` | Document recovery/edit transactions, settings, WPF indexing and generation |
| `WpfStudio.Workspace` / `WorkspaceHost` | Named-pipe JSON-RPC, SDK/MSBuild discovery, Roslyn, build execution |
| `WpfStudio.Runtime` | Debug Adapter Protocol/netcoredbg and ConPTY/WebView2 terminal |
| `WpfStudio.Database` | SQL profiles, schema browsing, batch execution, bounded results, SQL pane |

This implementation targets SDK-style managed Windows x64 development. .NET Framework projects, native/mixed debugging, visual drag-and-drop design, live XAML preview, Hot Reload, and database administration are outside this release. Package management and common Git workflows have dedicated panes; advanced Git operations and private-feed interactive authentication use the terminal. Dynamic resources, runtime-created DataContexts, and application-specific markup extensions can prevent static XAML analysis from reaching a definite answer. C# rename cannot safely rewrite arbitrary string-based XAML bindings; review its warnings and preview. Refactoring actions are deliberately conservative rather than a complete Visual Studio code-fix catalog.

Performance goals are a usable shell within 2 seconds, a navigable 20-project/5,000-file tree within 5 seconds, warm completion below 250 ms at p95, and settled UI/worker memory below 1 GiB before optional features. These are measurement targets rather than unconditional guarantees; restore state, analyzer packages, SDK version, and hardware affect results.

Run `./tools/Measure-Performance.ps1` to generate a restored 20-project/5,000-source-file WPF fixture under `artifacts/performance`, measure worker load and 25 warm completion requests, and write `artifacts/performance/results.json`. This benchmark labels harness/worker memory separately; it does not substitute for measuring actual shell startup, first tree display, or optional terminal/debugger/SQL overhead.

The portable package includes `samples/CounterApp/CounterApp.csproj` for a small WPF/MVVM editing and debugging walkthrough. Open that project, build, set a breakpoint in `CounterViewModel.cs`, and press F5.

See [validation results and remaining manual checks](VALIDATION.md) and [third-party notices](THIRD-PARTY-NOTICES.md). `tools/Smoke-Portable.ps1` verifies the real published executable with an isolated settings directory; `WPFSTUDIO_DATA_DIRECTORY` can override the default profile for test runs.
