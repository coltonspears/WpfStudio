# Validation record

Validation was performed on this Windows 11 x64 workstation on 2026-09-26. The machine has a Ryzen 9 5900X, approximately 96 GiB of RAM, .NET SDKs 9 and 10, Desktop runtimes 8–10, WebView2, and SQL Server LocalDB. An isolated SDK 8.0.425 installation was also used for compatibility verification without changing the system toolchain. Full Visual Studio is installed on this workstation; this is not a clean-machine certification.

## Automated checks

The final Release checks passed **128 tests** across the Core (24), Workspace (14 plus the isolated SDK 8 test), Runtime (17), Database (41), Shell (30), and App UI (1) projects. The SDK 8 test is intentionally skipped by the regular invocation and passed in its dedicated isolated-toolchain invocation. Release reports are under `artifacts/TestResults`; `runtime-final.trx`, `app-packaged-final.trx`, and `sdk8-final.trx` contain the final dedicated checks and supersede earlier runs of those tests.

The published executable also passed its actual startup, prerequisite, isolated-profile persistence, and graceful-shutdown checks. The final UI test used the **published WorkspaceHost** to load and build the sample and load the 20-project fixture.

- Document saves preserve encoding, reject conflicting external changes, retain newer edits during saves, and recover unsaved content. Multi-file changes reject stale snapshots before modifying buffers. Undo removes pending generated files without deleting saved files.
- Workspace integration uses real SDK evaluation, Roslyn, WPF-generated source, Toolkit generators, linked files, solution/project formats, custom configurations, launch profiles, cancellation, and failed-load fallback. Completion cache and build-output limits have flood regressions.
- The isolated SDK 8 test pins `global.json` to 8.0.425, builds WPF/Toolkit using that SDK, starts the .NET 10 worker against MSBuild 8, and verifies generated members, diagnostics, and completion. SDK 9 selection is tested separately.
- Debugger tests run real netcoredbg sessions against .NET 8, 9, and 10 WPF fixtures. They exercise breakpoints, async continuations, stepping, locals, evaluation, exception information, missing symbols, attach/detach, and owned-process cleanup. Stopping or disposing an attached session preserves the target process.
- Terminal tests use real ConPTY and WebView2: Unicode, Ctrl+C, resizing, sustained output, search, renderer reparenting, a forced renderer crash, and cleanup. A crashed renderer produces an error and releases its owned shell rather than blocking indefinitely.
- WPF tests cover resource scope and precedence, linked XAML, cycles, uncertain runtime resolution, entity offsets, safe resource rename, metadata-preserving build actions, compiler-aware scaffolding, binding suggestions, and view/code-behind/view-model navigation.
- SQL tests use a disposable LocalDB instance for authentication failures, cancellation, shared-session `GO` batches, temporary tables, multiple results, line mapping, and truncation. Aggregate result/message limits drain extra output without preventing later statements from executing. Profile encryption, CSV, saves, external changes, themes, and recovery have separate tests.
- The loaded-window smoke test opens and builds CounterApp through the real workspace worker, loads both themes, exercises overlays and a real prompt dialog, checks document closing, and restores serialized docking layouts. It verifies tool/document bindings and SQL F5 routing with no binding errors.

Run the reproducible checks:

```powershell
dotnet test WpfStudio.sln -c Release --logger trx --results-directory artifacts/TestResults
./tools/Measure-Performance.ps1
./tools/Publish.ps1 -Zip
./tools/Smoke-Portable.ps1
```

## Performance samples

These are workstation measurements, not general guarantees. The fixture contains 20 restored WPF projects and 5,000 authored source files. File navigation becomes available before semantic loading completes.

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

Raw results: `artifacts/performance/results.json`, `ui-measurements.json`, and `portable-smoke.json`. The warm-completion benchmark, loaded-window benchmark, and portable startup check are separate runs. The portable package includes copies under `validation/`.

Optional features are measured separately. The first terminal became ready in approximately 573 ms, adding about 10.1 MiB to its test host; its WebView2 environment used about 288.5 MiB across five processes, and Command Prompt used about 5.5 MiB. The WebView2 environment can be shared between terminal sessions. The debugger adapter used about 45.5 MiB while paused in a .NET 10 WPF fixture; that excludes the debuggee. Host deltas include JIT, GC, and test activity and should not be interpreted as isolated component allocations. Raw samples are in `terminal-feature.json` and `debugger-feature.json`.

The first-query SQL harness sample used 25.6 MiB of private memory after execution. Its before/after delta was negative because of transient runtime/test memory, so it does not establish an isolated SQL feature overhead. `sql-feature.json` records this limitation; SQL Server process memory, result grids, and the main IDE were outside that sample.

## Remaining manual acceptance

- Run the portable package on a clean Windows 11 x64 machine without Visual Studio installed. Runtime SDK discovery uses `dotnet`; no Visual Studio or `vswhere` discovery is required in the implementation.
- Move floating windows between physical monitors with different DPI settings.
- Exercise long-lived sessions and full-screen terminal applications on the intended workstation.
- Check corporate SQL Server authentication, encryption/certificate configuration, latency, and representative schemas. Automated SQL execution uses disposable LocalDB rather than a production database.

Static XAML assistance deliberately leaves runtime-dependent bindings/resources uncertain. Pack-URI generation uses the owning assembly and evaluated Link path for linked assets. If project evaluation cannot supply that path, refresh the workspace after resolving its SDK/evaluation diagnostics.
