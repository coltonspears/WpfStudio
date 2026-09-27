# Validation record

Validation was performed on this Windows 11 x64 workstation on 2026-09-26. The machine has a Ryzen 9 5900X, approximately 96 GiB of RAM, .NET SDKs 9 and 10, Desktop runtimes 8–10, WebView2, and SQL Server LocalDB. An isolated SDK 8.0.425 installation was also used for compatibility verification without changing the system toolchain. Full Visual Studio is installed on this workstation; this is not a clean-machine certification.

## UI overhaul (graphite theme, custom title bar)

The `ui-overhaul` branch replaces the VS2013 docking look with the studio's own graphite theme, draws the title bar with `WindowChrome`, restructures the menus, and adds the command center, fuzzy command palette, editor context bar, and a new Start page. It was validated on the same workstation on 2026-09-26 with a Debug build.

- **Test suites:** Core 24/24, Workspace 41/41 (the isolated SDK 8 check is skipped by design), Database 41/41, Shell 89/89 (four new checks cover palette ranking and modes, fuzzy scoring, related-file discovery, and direct theme selection), App UI 1/1. The loaded-window UI check still asserts no binding errors across both themes.
- **Runtime:** 16/17. `TerminalIntegrationTests.UnicodeAndControlCTravelThroughThePseudoconsole` timed out in this validation session. It fails the same way on the unchanged `main` branch in the same session, which runs through a remote automation shell, so it is environmental and unrelated to these view changes. Re-run it from a normal interactive terminal before release.
- **Screenshots:** the title bar is WPF content now, so every capture under `artifacts/screenshots` includes the menu, command center, and caption buttons. New captures: `command-palette.png`, `quick-open.png`, `new-item.png`, `change-preview.png`, and `sql.png`.
- **Real window:** the built executable was launched with an isolated profile and probed with `WM_NCHITTEST`. The empty title bar returns `HTCAPTION` (drag, double-click, and system menu), the maximize button returns `HTMAXBUTTON` (Windows 11 Snap Layouts), and the menu and close button return `HTCLIENT`. When the window is maximized, its content stays within the monitor work area; the 8 px frame overhang that Windows adds is compensated.

## Automated checks

The expanded Release suite passed **210 distinct checks**: Core (24), Workspace (41 plus the isolated SDK 8 check), Runtime (17), Database (41), Shell (85), and App UI (1). The initial full solution run passed 208 with no failures, followed by the final 85-check Shell run (including a new shutdown regression) and the loaded-window UI check after integration fixes. The SDK 8 check is intentionally skipped by the regular solution invocation and passed separately with the isolated toolchain. Reports are under `artifacts/TestResults/expanded-final`, `artifacts/TestResults/expanded-final-ui`, and `artifacts/TestResults/sdk8-final.trx`. The Release solution build completed without warnings or errors.

The updated portable executable passed actual startup, prerequisite, isolated-profile persistence, and graceful-shutdown checks. The final UI test used the **published WorkspaceHost** to load and build the sample and load the 20-project fixture. It also verified that package actions stay inside the viewport, feature tabs own their save/close behavior, saved layouts preserve the active tab, and all Git tab templates load without binding errors.

- Document saves preserve encoding, reject conflicting external changes, retain newer edits during saves, and recover unsaved content. Multi-file changes reject stale snapshots before modifying buffers. Undo removes pending generated files without deleting saved files.
- Workspace integration uses real SDK evaluation, Roslyn, WPF-generated source, Toolkit generators, linked files, solution/project formats, custom configurations, launch profiles, cancellation, and failed-load fallback. Completion cache and build-output limits have flood regressions.
- The isolated SDK 8 test pins `global.json` to 8.0.425, builds WPF/Toolkit using that SDK, starts the .NET 10 worker against MSBuild 8, and verifies generated members, diagnostics, and completion. SDK 9 selection is tested separately.
- Debugger tests run real netcoredbg sessions against .NET 8, 9, and 10 WPF fixtures. They exercise breakpoints, async continuations, stepping, locals, evaluation, exception information, missing symbols, attach/detach, and owned-process cleanup. Stopping or disposing an attached session preserves the target process.
- Terminal tests use real ConPTY and WebView2: Unicode, Ctrl+C, resizing, sustained output, search, renderer reparenting, a forced renderer crash, and cleanup. A crashed renderer produces an error and releases its owned shell rather than blocking indefinitely.
- WPF tests cover resource scope and precedence, linked XAML, cycles, uncertain runtime resolution, entity offsets, safe resource rename, metadata-preserving build actions, compiler-aware scaffolding, binding suggestions, and view/code-behind/view-model navigation.
- SQL tests use a disposable LocalDB instance for authentication failures, cancellation, shared-session `GO` batches, temporary tables, multiple results, line mapping, and truncation. Aggregate result/message limits drain extra output without preventing later statements from executing. Profile encryption, CSV, saves, external changes, themes, and recovery have separate tests.
- The loaded-window smoke test opens and builds CounterApp through the real workspace worker, loads both themes, exercises overlays and a real prompt dialog, checks document closing, and restores serialized docking layouts. It verifies tool/document bindings and SQL F5 routing with no binding errors.

Additional feature regression coverage:

- Packages: SDK 8/10 command selection without an implicit browsing restore, enabled/disabled source parsing, semantic version ordering, multi-framework direct-reference inventory, central-version/property fallback, shell-free argument handling, save/removal guards, cancellation, and stale-response rejection after switching feeds or workspaces. The real CLI fixture builds tiny local package archives and uses a private package cache to verify install, evaluated inventory, central-version update, removal, and restore without an internet feed. Restores retain configured source mappings. A failed or cancelled mutation still invalidates the workspace because the CLI may already have modified files.
- Git: real disposable repositories and a local bare remote verify literal paths, Unicode/renames, initial commits, staging/unstaging, preservation of later working edits, staged-only commits, history/diffs, branch and dirty-tree guards, fast-forward fetch/pull/push behavior, cancellation, workspace-switch isolation, and credential redaction. Hosted remote authentication is not established by these local-remote tests.
- ColtonGPT: simulated OpenRouter responses exercise streaming, Unicode, multiline events, cancellation, bounded responses/history, catalog filtering, authentication/credit/rate-limit errors, and interrupted streams. Settings tests verify Windows user-scoped API-key protection, removal, and validation. View-model tests verify that editor content is opt-in, request previews match sent context, no chat requests happen automatically, and New chat clears sharing/history. No live model request has been verified without a user-provided API key.
- Refactoring: semantic tests exercise local `var`/explicit-type changes, unsafe conversions, anonymous/ref locals, type-name collisions, unused-import removal, preserving comments/directives, and cancellation. Existing document transactions provide version checks, preview, and undo for these operations.
- Expanded UI checks cover package/Git/ColtonGPT pane bindings, settings and request previews, vector file/component icons, WPF Explorer categories and resource-reference details, editor/tree/tool context menus, and breakpoint state presentation in both themes. Since the UI overhaul, the screenshots include the custom title bar; only the Windows resize border and shadow are outside the capture.

Run the reproducible checks:

```powershell
dotnet test WpfStudio.sln -c Release --logger trx --results-directory artifacts/TestResults
./tools/Measure-Performance.ps1
./tools/Publish.ps1 -Zip
./tools/Smoke-Portable.ps1
```

## Performance samples

After the expansion, the updated portable executable reached its idle shell in **1,442 ms**, used **158.9 MiB** of working set before a workspace or optional session was opened, and shut down in **134 ms** with exit code 0. This is one workstation sample with possibly warm caches, not a cold-start guarantee. Raw results are in `artifacts/performance/expanded-portable-smoke.json`. The latest `ui-measurements.json` includes the expanded UI checks before loading the fixture, so its test-process memory is not an isolated optional-feature cost.

These baseline workstation measurements precede the package/Git/ColtonGPT expansion and are not general guarantees or measurements of the new panes. The fixture contains 20 restored WPF projects and 5,000 authored source files. File navigation becomes available before semantic loading completes.

| Measurement | Observed | Scope |
| --- | ---: | --- |
| Portable executable start to idle shell | 1,440 ms | Empty isolated profile; OS/.NET caches may be warm |
| Empty portable shell working set | 158 MiB | Actual executable, before opening a workspace |
| Graceful portable shutdown | 120 ms | Exit code 0, settings saved, no owned children left |
| Shell construction to rendered idle | 876 ms | Loaded-window test; excludes process/CLR startup |
| First navigable file tree | 15 ms | Initial filesystem tree for the fixture |
| Complete semantic workspace | 21.4 s | Evaluation, generated documents, and language workspace |
| Warm completion p95 | 27.9 ms | 25 requests after warm-up |
| Shell test process working set | 234.4 MiB | Includes test runner and previous smoke checks |
| Workspace worker working set | 245.7 MiB | Same loaded-window fixture run |
| Largest UI dispatcher heartbeat gap | 116 ms | During fixture loading |

Raw results: `artifacts/performance/results.json`, `ui-measurements.json`, and `portable-smoke.json`; `ui-measurements.json` is refreshed by each UI check and now describes the expanded shell. The warm-completion benchmark, loaded-window benchmark, and portable startup check are separate runs. The portable package includes copies under `validation/`.

Optional features are measured separately. The first terminal became ready in approximately 573 ms, adding about 10.1 MiB to its test host; its WebView2 environment used about 288.5 MiB across five processes, and Command Prompt used about 5.5 MiB. The WebView2 environment can be shared between terminal sessions. The debugger adapter used about 45.5 MiB while paused in a .NET 10 WPF fixture; that excludes the debuggee. Host deltas include JIT, GC, and test activity and should not be interpreted as isolated component allocations. Raw samples are in `terminal-feature.json` and `debugger-feature.json`.

The first-query SQL harness sample used 25.6 MiB of private memory after execution. Its before/after delta was negative because of transient runtime/test memory, so it does not establish an isolated SQL feature overhead. `sql-feature.json` records this limitation; SQL Server process memory, result grids, and the main IDE were outside that sample.

## Remaining manual acceptance

- Run the portable package on a clean Windows 11 x64 machine without Visual Studio installed. Runtime SDK discovery uses `dotnet`; no Visual Studio or `vswhere` discovery is required in the implementation.
- Move floating windows between physical monitors with different DPI settings.
- Exercise long-lived sessions and full-screen terminal applications on the intended workstation.
- Check corporate SQL Server authentication, encryption/certificate configuration, latency, and representative schemas. Automated SQL execution uses disposable LocalDB rather than a production database.
- Configure a real OpenRouter key through **Tools > Settings**, select an available model, and verify live streaming, provider-specific limits, and cancellation. No live-provider success is claimed by simulated transport tests.
- Exercise company NuGet sources and credential providers. V3 browsing has no private-feed sign-in UI; CLI install/restore uses existing NuGet configuration. Verify repository-specific conditional/imported references and shared central-version changes against the intended projects.
- Verify hosted Git authentication with the developer's configured credentials. The pane does not supply interactive sign-in, merge/rebase resolution, or force-push workflows; use the terminal for those operations.
- Measure optional package/Git/ColtonGPT overhead separately on representative workloads. Previous terminal/debugger/SQL samples and aggregate UI-test memory do not establish overhead for the new tools.

Static XAML assistance deliberately leaves runtime-dependent bindings/resources uncertain. Pack-URI generation uses the owning assembly and evaluated Link path for linked assets. If project evaluation cannot supply that path, refresh the workspace after resolving its SDK/evaluation diagnostics.
