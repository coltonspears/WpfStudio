# Memory investigations

Open **Tools > Memory profiler**, or find **Memory profiler** in the command palette. The workbench works independently of a loaded solution.

This first profiling update implements managed-memory investigations. CPU, counters, async, database, File I/O, and Windows events are planned in the [profiling suite roadmap](profiling-suite-design.md); they are not implemented by this update.

## Start an investigation

Choose **Open dump…** for a full managed-process `.dmp`, or pick a process in the toolbar (**Refresh** updates the list) and choose **Capture**. Live capture uses an immutable Windows process snapshot. It does not inject an inspection agent, invoke application getters, force a GC, or terminate the target when analysis is cancelled or closed. Creating the OS snapshot can briefly affect the target; analyzing large heaps uses substantial worker memory.

The process selector shows the **OS working set**. The workbench shows **managed object bytes**. These measure different things: native allocations, mapped files, GC free space, and reserved/committed heap space are not included in managed object sizes. Root reachability describes the captured instant, not a promise about the next collection or the operating system's memory return policy.

After a capture, the second toolbar row switches between four views (**Ctrl+1–4**), shows the snapshot's source, runtime and load time, and holds the baseline controls. **Go to type or 0x address** (**Ctrl+G**) filters the type list by name, or opens the object at a hexadecimal address. Incomplete coverage is shown as a warning banner with the coverage notes. The object browser docks on the right of every view; the sidebar button hides it.

## Overview: findings first

The Overview is the starting point for people new to memory work.

- **Headline numbers**: managed heap, *kept alive* (reachable from GC roots), *collectible now* (no root path; garbage the next GC frees, not a leak), GC roots by kind, and heap free space (fragmentation). With a baseline, deltas appear under the first two.
- **Heap by generation**: a stacked bar of Gen 0/1/2, the large and pinned object heaps and frozen segments. Hover a segment for its size.
- **Findings**: automatic inspections computed in the worker at capture time. Each one names the evidence, explains why it matters, and lists the objects or types to open.

| Finding | What it detects |
| --- | --- |
| Closed windows still in memory | WPF `Window` instances (including subclasses) whose `_disposed` flag is set but that are still reachable. |
| Disposed objects still referenced | Application types with a set disposed flag (`_disposed`, `disposed`, `_isDisposed`, `disposedValue`, …) that a root still reaches. |
| Kept alive only by event handlers | Application objects whose immediate dominator is a delegate or a delegate's invocation list: an event subscription is the only owner. |
| Types that grew since the baseline | Computed in the UI from the pinned baseline: types that gained objects and bytes. |
| Duplicate strings | String values that occur more than once, with the bytes the extra copies waste. Strings over 4 KB and those beyond the scan budget are not compared. |
| Mostly empty arrays | Reference arrays of 32+ slots with at most 25% used (oversized or never-trimmed collections). |
| Finalization only | Objects reachable only through the finalizer queue. |
| Large object heap / fragmentation | LOH usage, and free space above 25% of the heap. |
| Pinned objects | Pinned handles, highlighting pins in generations 0 and 1. |

Leak inspections ignore runtime, BCL and WPF framework types so application types stand out. A finding is evidence to check against the object's expected lifetime, not a verdict.

- **Heap composition**: a squarified treemap of types grouped by namespace or assembly, sized by own bytes, retained bytes or object count. The eight largest groups get the fixed categorical palette (`Chart1Brush`…`Chart8Brush`); the rest fold into a neutral group. Click a rectangle for details, double-click to open the type.
- **Largest owners**: the objects that exclusively keep the most memory alive. Pass-through wrappers and the runtime's statics arrays are skipped in favour of the object that really owns the memory.
- **Growth since baseline**, or a three-step guide to setting one up.

## Types: what keeps instances alive

The type list can be filtered, sorted (own bytes, retained bytes, largest retained object, object count, growth) and grouped by namespace or assembly; **My code** hides framework types. Each row shows object count, own bytes and the bytes all instances keep alive together (*retained*), with proportional bars, and growth when a baseline is set. Type-level retained bytes never double-count an instance nested inside another instance of the same type.

Select a type to see:

- **Retention paths**: a Sankey diagram with GC roots and static fields on the left and the type on the right. Each band's width is how many instances (or retained bytes) are held through that owner, and field names label the bands. Every instance contributes its shortest path to each distinct root (up to three; the 1,000 largest instances), so an object held by both a cache and an event subscription shows on both branches. Static fields appear as roots in their own right instead of the runtime `object[]` that stores them. Columns fold to the seven largest owners plus an "other types" node, paths longer than six owners end in "More owners…", and instances with no root are shown in green. Hover a node or band to trace the flow; click a node to open its largest example in the browser; double-click an owner to open its type. The finalizer queue is hidden by default; the **Finalizer queue** and **Stack** toggles include or exclude those roots.
- **Instances**: objects of the type, largest retained first, with address or type search and paging.

## Retention: the dominator tree

The dominator tree answers "who owns this memory?": each object owns everything nested under it, so if it became unreachable all of that would be freed. Sibling instances of one type are grouped (`12 × Byte[]`), each row shows the field or root it is held through, its retained size and share of the heap. At the top level the runtime's statics arrays are replaced by the static fields they store.

The treemap beside the tree shows what the selected node keeps alive. Double-click a rectangle (or a tree row) to drill in; the breadcrumb returns to the heap. Colours follow the type, so the same type keeps its colour as you drill.

## Graph: the retention graph

The graph is vertical: GC roots and static fields at the top, the inspected object (indigo) in the middle, and the objects it references below. It shows the root-path examples, the object's largest owners and its largest referenced objects.

- **+N owners** above a node and **+N** below it add one more hop of owners or references in place; the clicked node stays where it is on screen.
- Four or more leaf siblings of one type fold into a stacked group; double-click it to expand.
- Edge labels show field names; selecting a label selects that reference for a removal estimate.
- The minimap (bottom right, **M**) shows the whole graph with the visible region; click or drag in it to move. It hides itself when the whole graph already fits.
- Drag to pan, drag nodes to rearrange, wheel to zoom, **Ctrl+0** to fit, arrow keys to move between nodes, **Enter** to inspect. **Finalizer queue** roots are hidden by default so long-lived owners stand out. **Focus graph** hides the browser.

## The object browser

The browser follows the selection from every view. **Back**/**Forward** (also **Alt+←/→** and the mouse's back/forward buttons) and the breadcrumb walk the navigation history.

The header shows the type, address (copyable), whether it is alive or collectible, its generation and pinning, and its own and retained sizes with the share of the heap it keeps alive.

- **Fields** is an expandable tree read from captured memory. Expanding a reference loads its fields in place; the → button or a double-click opens it. Arrays list their elements (byte arrays also show a hex preview, char arrays their text); `List<T>`, `Dictionary<TKey,TValue>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `ObservableCollection<T>` and other `Collection<T>` wrappers show their elements, with a **Raw view** of the declared fields. Dates, time spans, GUIDs and enums are formatted; other structs show their first fields. Large collections page with **Show more**.
- **Why alive** summarizes what holds the object and shows up to eight root paths, longest-lived first (static fields and handles before stacks and the finalizer queue), as chains you can click through. Two static fields stored in the same runtime array are two separate paths. Below are the exclusive owners (dominators) and observations about delegates, timers, statics, pinning and finalization.
- **Keeps alive** lists everything the object exclusively retains, by type. **What if a reference went away?** models removing every owner (**Remove all owners**) or one selected slot (**Remove selected slot**) and reports the managed bytes that would become eligible, the types involved and, if the object would survive, the path that still holds it.
- **References** lists incoming owners/roots and outgoing slots for exact slot selection.

Own and retained bytes keep their meaning:

- **Own bytes** are the selected object's allocation, including its inline contents.
- **Retained bytes** include that object and the reachable objects that would become unreachable if every removable incoming reference/root to it were severed. Shared children reached through another owner are excluded. Retained sizes overlap across domination chains; summing every object's retained size double-counts memory.
- An object with no captured root path has zero newly reclaimable retained bytes. On a complete graph it is already eligible for collection. On an incomplete graph its reachability is unknown.
- Frozen-segment objects, including some runtime string literals, have permanent ownership. Their retained/release estimate is zero; ordinary owner removal does not make them collectible.

This use of dominators follows the distinction between paths and exclusive retention described in [JetBrains' dominator documentation](https://www.jetbrains.com/help/dotmemory/Retained_by.html).

## Model cleanup

**Remove selected slot** removes only the selected reference slot in a copy of the graph. It preserves other slots between the same objects, other roots, and shared descendants. For example, removing a cache slot does not free a page still subscribed to a static event; the surviving path explains the remaining ownership.

**Remove all owners** models severing every removable incoming strong reference/root to the selected object. Permanent frozen roots and immutable frozen-object fields remain intact. This answers how much it exclusively retains and what would need to lose ownership. It does not suggest that setting one field to null will necessarily release that amount.

Both operations are simulations. They never change the running application's fields or handles. Counts include only objects that were reachable before the modeled removal and become unreachable afterward. Dependent handles are modeled as conditional key-to-value edges, not permanent roots; weak handles do not retain their targets. Objects freed by an estimate turn green in the graph.

The estimate concerns managed collection eligibility. Finalization, resurrection, later application activity, unmanaged ownership, and the CLR's heap policy can change eventual reclamation and working-set behavior.

## Compare snapshots and export evidence

Choose **Set baseline**, repeat a workload (for example, open and close a page several times), and capture again or open a second dump. Comparison uses type/module identity, counts, and own bytes. It does not treat addresses as stable object identities across moving collections. Types that disappear remain visible with negative count/byte deltas. Baselines keep aggregate data; the previous worker and native snapshot are released when the new capture succeeds.

Growth appears as a finding, in the Overview's growth list and as a column in the type list (sort by **Growth since baseline**). Open a growing type and check its retention paths; confirm that the operation's expected owner lifetime has ended before treating persistent growth as a leak.

The export button writes a JSON investigation containing the capture summary (including findings), baseline totals, the selected object's fields/root paths, the visible graph, the retained composition, the type's retention flow and the removal estimate. The report is evidence for review; it is not a reloadable complete heap snapshot. Keep the original dump when another investigator needs to explore other objects.

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

Default analysis budgets are **1,000,000 objects** and **6,000,000 references**. Capture also reads disposed flags, reference-array lengths and up to 2,000,000 strings (500,000 distinct values, 64 million characters) for the inspections. Options allow up to 5,000,000 objects / 30,000,000 references. Larger limits can require gigabytes of worker memory, especially for large reference arrays; x86 address space is more constrained. Missing/corrupt data, omitted referenced objects/roots, and exhausted graph budgets make coverage incomplete. Estimates then explicitly remain provisional and can overstate reclamation.

The worker retains the full bounded analysis; the UI separately limits graph neighborhoods to 120 objects / 400 references (12 owners and 16 references per hop, expandable 24 at a time), dominator pages to 150 nodes, retention flows to 5,000 instances, object pages to 200, matching type display to 5,000, reference lists to 200 per direction, fields to 128, strings to 200–256 characters, and delegate targets to 20. Root examples traverse at most 50,000 ancestors with 128 slots per displayed path. Display limits do not change full-graph dominator calculations. Follow a neighbor to explore beyond the current map. Root and graph truncation remain part of the inspection/report data.

Opening/loading a capture has a five-minute worker deadline; queries have a one-minute deadline. A timeout closes the owned worker and requires reopening the capture. Failed/cancelled replacement captures preserve the previous successful session. Closing WpfStudio disposes the active worker and native snapshot. Normal shutdown disconnects and waits for native snapshot cleanup and file-handle release; an unresponsive worker is terminated after the shutdown deadline.

## Validation

`WpfStudio.Profiling.Tests` exercises shared ownership, parallel slots, multiple roots, rooted/unrooted cycles, dependent handles, permanent frozen roots, incomplete coverage, a 100,000-object chain, and randomized graphs against an independent reachability oracle. Its real-process tests capture and load full dumps from .NET 10 and Framework 4.8 fixtures, including frozen string-literal eligibility. `HeapExplorationTests` cover retention flows (static fields shown as roots, instances held by two statics on both branches, hidden root kinds, column folding and flow conservation), dominator pages (sibling grouping, statics holders replaced by their fields), non-double-counted type retention, retained composition, root-path ordering, graph neighbourhoods, every automatic inspection, and label shortening.

`WpfStudio.Shell.Tests` exercises worker IPC against x64 .NET and x86 Framework processes and full dumps (including the packaged self-contained x86 worker), incompatible WOW64 recovery, baseline disappearance, capture cancellation/failure, stale/empty selection results, and worker disposal without terminating targets or leaving dump files locked. View-model tests cover browser back/forward history and lazy field expansion, findings and baseline growth, retention-flow root filters, graph expansion merging, and type navigation.

The WPF smoke test exercises real capture, the Overview (KPIs, generations, findings, treemap), the type Sankey (static cache and static event roots), collection expansion in the field tree, the vertical graph with expansion, focus, fit and zoom, the dominator tree and treemap, root paths, shared-root and exclusive-release estimates, baseline growth, a short docked layout, docking reuse, light/dark rendering, and zero binding errors. Screenshots are written to `artifacts/screenshots/memory-*.png`. For a focused run (which also enlarges the window for review screenshots):

```powershell
$env:WPFSTUDIO_TEST_MEMORY_ONLY = '1'
dotnet test tests/WpfStudio.App.Tests/WpfStudio.App.Tests.csproj
Remove-Item Env:WPFSTUDIO_TEST_MEMORY_ONLY
```
