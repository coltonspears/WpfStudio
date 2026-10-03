# Memory investigations

Open **Tools > Memory profiler**, or find **Memory profiler** in the command palette. The workbench works independently of a loaded solution.

This first profiling update implements managed-memory investigations. CPU, counters, async, database, File I/O, and Windows events are planned in the [profiling suite roadmap](profiling-suite-design.md); they are not implemented by this update.

## Start an investigation

Choose **Open dump…** for a full managed-process `.dmp`, or **Refresh processes**, select an application, and choose **Capture snapshot**. Live capture uses an immutable Windows process snapshot. It does not inject an inspection agent, invoke application getters, force a GC, or terminate the target when analysis is cancelled or closed. Creating the OS snapshot can briefly affect the target; analyzing large heaps uses substantial worker memory.

The process selector shows the **OS working set**. The capture summary shows **managed object bytes**. These measure different things: native allocations, mapped files, GC free space, and reserved/committed heap space are not included in managed object sizes. Root reachability describes the captured instant, not a promise about the next collection or the operating system's memory return policy.

The time in the header is the analysis load time. Opening an older dump does not give it a new capture time; use the original dump's provenance when comparing workloads.

Expand the heap overview below the capture controls for managed/reachable/unrooted totals, coverage notes, and baseline details. It starts collapsed to leave room for the inspector in shorter docked workbenches; incomplete coverage is also marked in the header and status.

## Find a retained object

Filter the type list by type or module name. Sort by **Total managed bytes**, **Largest retained object**, or **Growth since baseline**. Select a type to browse its objects; objects are ordered by retained size. **All types** removes the type restriction, and the object search accepts type names and hexadecimal addresses. **Previous** and **Next** page through matching objects without shipping the entire heap to the UI.

Select an object to see its own bytes, retained bytes, generation, pinning, and relationship map:

- **Own bytes** are the selected object's allocation, including its inline contents.
- **Retained bytes** include that object and the reachable objects that would become unreachable if every removable incoming reference/root to it were severed. Shared children reached through another owner are excluded. Retained sizes overlap across domination chains; summing every object's retained size double-counts memory.
- An object with no captured root path has zero newly reclaimable retained bytes. On a complete graph it is already eligible for collection. On an incomplete graph its reachability is unknown.
- Frozen-segment objects, including some runtime string literals, have permanent ownership. Their retained/release estimate is zero; ordinary owner removal does not make them collectible. The inspector shows their generation and permanent root, and disables removal of that intrinsic root.

This use of dominators follows the distinction between paths and exclusive retention described in [JetBrains' dominator documentation](https://www.jetbrains.com/help/dotmemory/Retained_by.html).

## Explore the relationship map and inspector

Arrows point from owner to referenced object. Amber marks a GC-root target, indigo marks the investigated object, and green marks a sample of objects affected by the current removal estimate.

Drag a node to rearrange it. Drag the background, or use the middle/right mouse button, to pan. The wheel and **+ / −** zoom; **Fit** fits the visible graph. Double-click a node to inspect its neighborhood; **Back** returns to the previous object. **Focus map** gives the graph the full workbench width and hides the overview; **Show inspector** restores the linked panels. Keyboard users can focus the map, use arrows to select nodes, **Enter** to inspect, **+ / −** to zoom, and **Ctrl+0** to fit.

The inspector provides:

- **Roots**: up to eight root-path examples, dominating owners, and evidence about static ownership, delegate/event retention, timers, pinning, and finalization. Static field names are resolved where available; otherwise the underlying runtime slot remains visible. A rooted object is not automatically a leak: check its expected lifetime.
- **Fields**: scalar values, bounded strings, reference addresses, and delegate target methods, read from captured memory. **Inspect** follows an object-valued field. Arrays show their type and length; their reference slots are available under References. Inline structs are identified; their outgoing object references appear in the reference list.
- **References**: incoming owners/roots and outgoing reference slots. Selecting a row or a map edge label selects the exact reference for a removal estimate. **Owner** and **Target** navigate to those objects.
- **What if**: affected bytes, object counts, type breakdown, and a surviving root path when the selected object remains rooted.

## Model cleanup

**Estimate slot** removes only the selected reference slot in a copy of the graph. It preserves other slots between the same objects, other roots, and shared descendants. For example, removing a cache slot does not free a page still subscribed to a static event; the surviving path explains the remaining ownership.

**Estimate all owners** models severing every removable incoming strong reference/root to the selected object. Permanent frozen roots and immutable frozen-object fields remain intact. This answers how much it exclusively retains and what would need to lose ownership. It does not suggest that setting one field to null will necessarily release that amount.

Both operations are simulations. They never change the running application's fields or handles. Counts include only objects that were reachable before the modeled removal and become unreachable afterward. Dependent handles are modeled as conditional key-to-value edges, not permanent roots; weak handles do not retain their targets.

The estimate concerns managed collection eligibility. Finalization, resurrection, later application activity, unmanaged ownership, and the CLR's heap policy can change eventual reclamation and working-set behavior.

## Compare snapshots and export evidence

Choose **Set baseline**, repeat a workload (for example, open and close a page several times), and capture again or open a second dump. Comparison uses type/module identity, counts, and own bytes. It does not treat addresses as stable object identities across moving collections. Types that disappear remain visible with negative count/byte deltas. Baselines keep aggregate data; the previous worker and native snapshot are released when the new capture succeeds.

Use the growth sort to choose a candidate, then inspect its current retention paths. Check that the operation's expected owner lifetime has ended before treating persistent growth as a leak. Comparisons between different applications, runtime versions, or incomplete heaps need additional context.

**Export report…** writes a JSON investigation containing the capture summary, baseline totals, selected object's fields/root paths, visible graph, and removal estimate. The report is evidence for review; it is not a reloadable complete heap snapshot. Keep the original dump when another investigator needs to explore other objects.

## Runtime support and analysis budgets

| Input | Support in this update |
| --- | --- |
| Modern .NET x64 full Windows dumps / live snapshots | Implemented; integration-tested with .NET 10 |
| .NET Framework x64 full Windows dumps / live snapshots | Implemented; integration-tested with .NET Framework 4.8 |
| .NET Framework x86 full Windows dumps / live snapshots | Implemented; integration-tested with .NET Framework 4.8 through the packaged x86 worker |
| Native 64-bit WOW64 subsystem dumps of x86 processes | Detected with recovery instructions; recapture as a 32-bit full process dump |
| `.gcdump`, `.nettrace`, `.etl` | Not heap-reader inputs in this update; separate collectors/importers are planned |
| ARM64, remote profiling, native heap analysis | Planned; unsupported in this update |

[ClrMD requires the analysis process and DAC architecture to match the target](https://github.com/microsoft/clrmd/blob/main/doc/GettingStarted.md). WpfStudio routes 32-bit captures to a separate x86 worker. Portable packaging includes a self-contained x86 worker. A source build can instead use the installed **.NET 10 x86 runtime** at `Program Files (x86)/dotnet`; the profiled application's runtime stays unchanged.

For a 32-bit application, use a 32-bit full process dump, for example `procdump -ma <PID> application.dmp`. [ProcDump selects the target's bitness by default](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump); its `-64` option instead captures the native WOW64 subsystem. This heap reader detects that incompatible format and offers recapture or live-snapshot instructions. x86 workers use a stream-based dump reader to avoid mapping the entire file into limited 32-bit address space.

The matching runtime DAC (`mscordacwks.dll` for Framework, `mscordaccore.dll` for modern .NET) is resolved locally or from Microsoft's symbol server. **Options > Choose DAC…** accepts a matching local file when automatic resolution is unavailable. A DAC from another runtime build is not interchangeable. **Runtime index** chooses a CLR in a process/dump containing multiple runtimes; totals cover that CLR only. Windows access permissions still apply to live process discovery and capture; inaccessible processes are omitted.

Default analysis budgets are **1,000,000 objects** and **6,000,000 references**. Options allow up to 5,000,000 objects / 30,000,000 references. Larger limits can require gigabytes of worker memory, especially for large reference arrays; x86 address space is more constrained. Missing/corrupt data, omitted referenced objects/roots, and exhausted graph budgets make coverage incomplete. Estimates then explicitly remain provisional and can overstate reclamation.

The worker retains the full bounded analysis; the UI separately limits graph neighborhoods to 120 objects / 400 references, object pages to 200, matching type display to 5,000, reference lists to 200 per direction, fields to 128, strings to 200–256 characters, and delegate targets to 20. Root examples traverse at most 50,000 ancestors with 128 slots per displayed path. Display limits do not change full-graph dominator calculations. Follow a neighbor to explore beyond the current map. Root and graph truncation remain part of the inspection/report data.

Opening/loading a capture has a five-minute worker deadline; queries have a one-minute deadline. A timeout closes the owned worker and requires reopening the capture. Failed/cancelled replacement captures preserve the previous successful session. Closing WpfStudio disposes the active worker and native snapshot. Normal shutdown disconnects and waits for native snapshot cleanup and file-handle release; an unresponsive worker is terminated after the shutdown deadline.

## Validation

`WpfStudio.Profiling.Tests` exercises shared ownership, parallel slots, multiple roots, rooted/unrooted cycles, dependent handles, permanent frozen roots, incomplete coverage, a 100,000-object chain, and randomized graphs against an independent reachability oracle. Its real-process tests capture and load full dumps from .NET 10 and Framework 4.8 fixtures, including frozen string-literal eligibility.

`WpfStudio.Shell.Tests` exercises worker IPC against x64 .NET and x86 Framework processes and full dumps (including the packaged self-contained x86 worker), incompatible WOW64 recovery, baseline disappearance, capture cancellation/failure, stale/empty selection results, and worker disposal without terminating targets or leaving dump files locked.

The WPF smoke test exercises real capture, field/root display, zoom/focus commands, shared-root and exclusive-release estimates, baseline comparison, docking reuse, light/dark rendering, and binding diagnostics. For a focused run:

```powershell
$env:WPFSTUDIO_TEST_MEMORY_ONLY = '1'
dotnet test tests/WpfStudio.App.Tests/WpfStudio.App.Tests.csproj
Remove-Item Env:WPFSTUDIO_TEST_MEMORY_ONLY
```
