# Wants and needs: progress and notes

Status for each item on the wants list, ordered roughly by how much it gives for the effort it takes. **Done** items shipped on the `claude/workbench-essentials` branch; **Existing** items were already in WpfStudio before this list.

| Status | Meaning |
| --- | --- |
| ✅ Done | Implemented, with tests |
| 🟢 Existing | Already in WpfStudio; notes cover what is still missing |
| 🟡 Next | Recommended next, with a suggested design |
| 🔵 Later | Larger feature; design notes only |
| 💭 Pipedream | Exploratory; feasibility notes |

## Summary

| Item | Status | Where |
| --- | --- | --- |
| Close Solution | ✅ Done | File > Close solution; `ShellViewModel.Workbench.cs`, `WorkspaceClient.UnloadAsync` |
| Window > Close Tabs | ✅ Done | Window menu: all, other, saved, to the right |
| Help > About | ✅ Done | Help > About WpfStudio, with **Copy version info** |
| Help > What's New | ✅ Done | Help > What's new; entries in `ViewModels/ReleaseNotes.cs` |
| Filterable/searchable Problems | ✅ Done | Problems toolbar: text, severity toggles, scope |
| Filterable/searchable Solution Explorer | ✅ Done | Filter box above the tree |
| Grouped code-behind in Solution Explorer (can be turned off) | ✅ Done | Nesting toggle on the Solution Explorer toolbar; saved in settings |
| Smarter auto-indent for braces | ✅ Done | `Services/CSharpTypingService.cs` |
| Task List | ✅ Done | View > Task List: TODO-style comments plus your own items |
| Pinned File Groups | ✅ Done | File Groups tab beside Solution Explorer |
| Build/Rebuild/Clean solution or project | 🟢 Existing | Build menu (solution) and Solution Explorer context menu (project) |
| Formatting | 🟢 Existing | Code > Format document (C# and XAML) |
| Custom design-time DataContexts | 🟢 Existing (partial) | Preview scenarios (`wpfstudio.preview.json`) |
| Global/scoped tools and tasks (like VS Code) | 🟡 Next | — |
| Debug symbols / extra DLLs / source files | 🟡 Next | — |
| Runtime downloader | 🟡 Next | — |
| Split designer view | 🟡 Next | — |
| Template wizards (models from DB/REST/CSV, SQL from model) | 🔵 Later | — |
| Extended testing (integration, end-to-end, UI) | 🔵 Later | — |
| Web tunnel, tracing, IDE-scoped debugging, record/replay, smart breakpoints | 💭 Pipedream | — |

## Done

### Close Solution
File > **Close solution** asks about unsaved tabs (Cancel keeps everything open), stops debugging, clears Solution Explorer, Problems, WPF Explorer and the launch choices, then stops the language worker with the new `WorkspaceClient.UnloadAsync`. The settings file forgets the workspace, so the next start opens on the Start page. Opening another solution still closes the current one on its own.

### Window > Close Tabs
**Close all tabs**, **Close other tabs**, **Close saved tabs** (keeps tabs with unsaved edits) and **Close tabs to the right**. Tabs close one at a time; Cancel on a save prompt stops there and keeps that tab and the ones after it. All four are in the command palette.
*Follow-up:* the same commands on the tab's own right-click menu, through AvalonDock's `DocumentContextMenu`.

### Help > About and What's New
About shows the informational version (and commit when the build stamps one), runtime, OS and data folder. **Copy version info** puts them on the clipboard for bug reports. What's New lists `ReleaseNoteCatalog.All`, newest first.
*Follow-ups:* set `<Version>`/`<SourceRevisionId>` in `Directory.Build.props` so About shows a real version; open What's New once automatically when the version changes (store the last version seen in `StudioSettings`).

### Problems filter
A text filter matches code, message, file name and project. Separate terms with spaces; a term starting with `-` excludes matches, so `-CS8618` hides nullable warnings. Toggles show or hide errors, warnings and messages, with counts. The scope list chooses **All files**, **Open files** or **Current file**. The *N of M shown* button clears every filter. The status-bar counts still cover all problems.

### Solution Explorer filter and nesting
Typing in the filter box shows only the files whose name contains the text (or, with a `/`, whose path relative to the project contains it), plus matching package and project dependencies, with every folder expanded. Clearing it restores the tree as it was before filtering.

Nesting follows Visual Studio's rules: `X.xaml.cs` under `X.xaml`, `X.Designer.cs` under `X.resx`/`X.settings`, and dotted partial files under the file they extend (`ShellViewModel.Tools.cs` under `ShellViewModel.cs`, `appsettings.Development.json` under `appsettings.json`). The tree button on the Solution Explorer toolbar turns it off, and the choice is saved.

### Smarter auto-indent
In C# editors:
- Enter between `{` and `}` moves the `}` to its own line, aligned with the opening line, and leaves the caret on an indented line between them.
- Enter after `{` indents the next line by one level; text after the caret moves down with it.
- Enter before a `}` lines that brace up with the line that opened its block.
- Enter after a braceless `if (…)`, `else`, `for`, `foreach`, `while`, `using`, `lock` or `fixed` header indents the body.
- `{` typed on the body line of such a header snaps back to the header's indent. This is the case where you move a brace onto its own line.
- `}` typed on a blank line aligns with the line that opened its block.

None of these apply inside strings (regular, verbatim, interpolated or raw) or comments. Every rule is plain text logic, covered by `CSharpTypingTests`.

### Task List
View > **Task List** has two halves:
- **Comments:** `TODO`, `HACK`, `FIXME`, `UNDONE` and `BUG` in C#, XAML, SQL, PowerShell, JS/TS, CSS, Razor and MSBuild files. Tokens must be upper case and follow a comment opener. `TODO(owner): text` fills the Owner column. HACK, FIXME and BUG sort first. Saving a file updates its rows without rescanning; Refresh rescans everything. Double-click opens the line.
- **My tasks:** your own checklist for the workspace, stored in your profile (`<data>/tasks/<hash>.json`), not in the repository. Start a task with `@` to link it to the caret line of the active file.

### Pinned File Groups
The **File Groups** tab next to Solution Explorer. Pin the open tabs as a group (pin button, or Window > Pin open tabs), add the active file, or right-click a file in Solution Explorer > **Add to file group…**. Double-click a group to open all its files. **Switch to this group** closes the other tabs first. You can rename and delete groups and remove single files. Every group appears in the command palette as *Open file group: …* and *Switch to file group: …*. Groups are personal (`<data>/file-groups/<hash>.json`).
*Follow-ups:* an option to share a group through `.wpfstudio/file-groups.json` in the repository; drag files from Solution Explorer onto a group. A pane added after you last saved a layout docks at the bottom the first time; **Window > Reset to default layout** puts it beside Solution Explorer.

## Existing

### Build/Rebuild/Clean solution or project
This was already in WpfStudio: the Build menu acts on the solution, and right-clicking a solution or project node offers Build, Rebuild, Clean and Restore. Small additions worth having: **Build startup project**, and a keyboard shortcut for building the project of the active file.

### Formatting
Code > Format document (Ctrl+Shift+F) formats C# through Roslyn and XAML through the XAML formatter. Missing pieces, cheapest first:
1. **Format on save**, as an editor setting next to *trim trailing whitespace*.
2. **Format selection**: Roslyn's `Formatter.FormatAsync(document, span)`.
3. **Format on paste and on `;`/`}`**: Roslyn has `Formatter` with spans too. Keep the typing service for indentation and let Roslyn do spacing.
4. Make sure the worker loads `.editorconfig` as analyzer config documents, so formatting follows the repository's style.

### Custom design-time DataContexts
Preview scenarios already let a project define several named DataContexts (`wpfstudio.preview.json` plus a factory method) and switch between them. **Interact** mode covers live interaction. What is missing is data contexts that live **outside the project**:
- Add a scenario source in your profile or in `.wpfstudio/preview/` holding `wpfstudio.preview.json` entries that point to a **C# script file** (`.csx`). Compile it with Roslyn against the project's built assembly inside the preview host, so no factory class has to be in the project.
- Or, simpler: a `dataContextJson` scenario that deserializes JSON into the view's DataContext type. This suits read-only layouts.

## Next (recommended order)

### 1. Global/scoped tools and tasks, like VS Code
This has the highest value of what remains: you would use it every day.
- **Files:** `.wpfstudio/tasks.json` in the repository (shared) and `<data>/tasks.json` (personal), merged; when labels collide, the personal one wins.
- **Schema** (a subset of VS Code's): `label`, `command`, `args`, `cwd` (supports `${workspaceFolder}`, `${file}`, `${projectDir}`), `env`, `group` (`build`/`test`/`none`), `dependsOn` with `dependsOrder: parallel|sequence`, `isBackground`, `problemMatcher: "msbuild"`. The `msbuild` matcher can reuse the build output parser, so errors land in Problems.
- **Running:** each task in its own terminal tab of the existing terminal pane. Parallel `dependsOn` covers "launch client + server".
- **UI:** a Tasks pane listing them with Run/Stop, plus a form editor (label, command, args, cwd, env grid) with **Test run** before saving. Add them to the command palette as `Task: <label>`, and add *Run task…* to the Tools menu.
- Ideally the file reads VS Code's own `tasks.json` too, so existing repositories work immediately.

### 2. Debug symbols, extra DLLs and source files
`DebugSession` always launches netcoredbg with `justMyCode = true`. Steps:
1. Add a **Just My Code** toggle in Settings > Debugging, passed through to `launch`/`attach`. This is a small change and gives the most here.
2. Add **symbol search paths** and **source file maps** (a build path mapped to a local path, for PDBs built elsewhere). Check which launch arguments the pinned netcoredbg release accepts before building the UI; option names differ between debuggers.
3. A **Modules** view (DAP `modules` request) showing which DLLs have symbols loaded, with "Load symbols from…" for a chosen PDB folder.
4. Source Link and decompiled sources: the read-only `GeneratedSources` path already exists. Download Source Link files into it when a frame has no local source.

### 3. Runtime downloader
- Read the official release metadata (`releases-index.json`, then the channel's `releases.json`) to list versions, download URLs and SHA-512 hashes, so nothing has to be looked up online by hand.
- **Detection:** for each project, map `TargetFramework` (plus `UseWPF`/`UseWindowsForms`, which need `Microsoft.WindowsDesktop.App`) to a required runtime. Compare it with `dotnet --list-runtimes` and with `global.json` against `dotnet --list-sdks`. When something is missing, show a banner on workspace load: *"Shop.App needs .NET 8 Desktop Runtime — Install"*.
- **Install:** copy the debugger installer's pattern (download, verify checksum, install per user), or run the official installer elevated. For .NET Framework targets, detect missing targeting packs and link the developer pack.

### 4. Split designer view
The designer is its own document, so it can already be docked beside the XAML editor by hand. A one-click **Split** would open the XAML editor and designer as a side-by-side pair in one tab: a Grid with a splitter and orientation toggle, kept in sync by the existing selection sync (`SelectionSourceChanged`/`RevealDesignerSelection`). Remember the split ratio per file.

## Later

### Template wizards
Build one wizard shell and add each source as a plug-in:
- **Models from a database:** reuse the SQL Server workbench's connection profiles. Read `INFORMATION_SCHEMA.COLUMNS` and the key metadata, map SQL types to C# (nullable-aware), and offer record, class or `ObservableObject` output.
- **From REST:** paste a sample JSON response, or point at an OpenAPI document, and infer types (merge array elements, detect dates, GUIDs and numbers).
- **From CSV/Excel:** header row plus type inference over the first N rows.
- **SQL from a model class:** use the Roslyn semantic model of the class under the caret to generate `CREATE TABLE` (keys from `[Key]` or `Id`, lengths from `[MaxLength]`) and, optionally, CRUD procedures.
- Send all output through the existing **preview changes** dialog, so nothing is written unreviewed.

### Extended testing
1. **Test Explorer first:** discovery (`dotnet test --list-tests`, or VSTest/Microsoft.Testing.Platform discovery) and TRX results in a tree, with run, debug and re-run failed, plus CodeLens "Run test" above test methods. Everything else builds on this.
2. **Integration tests:** mostly organization. Filter by trait or category, add per-run environment settings (connection strings) and a "start dependencies" step, which the Tasks feature above can provide.
3. **UI / end-to-end:** WPF has no true headless mode, but **FlaUI** (UI Automation) drives a real window in tests and works on CI agents with a desktop session. A **Record test** mode could emit FlaUI code from the inspection agent's view of clicks and typing; it shares its plumbing with *record and replay* below.

## Pipedreams: feasibility

- **Web tunnel to launch in a browser:** a WPF window cannot render in a browser. The realistic version tunnels the app's backend services (Microsoft Dev Tunnels CLI) so a phone or teammate can reach them. For the UI itself, the closest option is remote streaming, which is a different product.
- **dotTrace-like tracing:** the profiling suite design already points this way. For .NET Core, use EventPipe through `Microsoft.Diagnostics.NETCore.Client` with the sample profiler and `TraceEvent` to build call trees, a flame graph and a timeline. .NET Framework needs ETW.
- **IDE-scoped context debugging:** the intent needs clarifying: debugging WpfStudio's own extensions, or scoping debugging to the active feature? A file group could define "my code" for Just My Code.
- **Record and replay debug sessions:** the inspection agent (startup hook) already sits in the target process. Start with **input recording** (routed input plus UIA element paths) and replay through UIA, together with VM property snapshots at each step. Full state replay (time-travel) is far harder and out of reach without a TTD-style engine.
- **Smart breakpoints:** the most achievable pipedream, because conditions, hit counts and tracepoints already go through DAP. Design:
  1. On a hit, the IDE reads the frame's locals and arguments through DAP `scopes`/`variables`, to a chosen depth, as a JSON snapshot.
  2. The user's snippet runs **in the IDE** with Roslyn scripting, against the snapshot as typed globals (`value.Snapshot?.Branch` works on the snapshot). Helpers: `Log(...)`, `Save(name, obj)`, `Capture()` (the whole frame), `Count(key)`.
  3. A *Stop* / *Continue* choice per breakpoint. Output collects in a "Breakpoint log" pane and is written to a file when the session ends or pauses.

  The limit: the snippet sees copies, not live objects, so it cannot call methods on them. netcoredbg's evaluator handles simple expressions only, with no lambdas or LINQ. For live calls, keep the expression part small and send it through DAP `evaluate`.
