# Memory investigations

Open **Tools > Memory profiler**, or find **Memory profiler** in the command palette. The workbench works independently of a loaded solution.

This first profiling update implements managed-memory investigations. CPU, counters, async, database, File I/O, and Windows events are planned in the [profiling suite roadmap](profiling-suite-design.md); they are not implemented by this update.

## Start an investigation

Choose **Open dump…** for a full managed-process `.dmp`, or pick a process in the toolbar (**Refresh** updates the list) and choose **Capture**. Live capture uses an immutable Windows process snapshot. It does not inject an inspection agent, invoke application getters, force a GC, or terminate the target when analysis is cancelled or closed. Creating the OS snapshot can briefly affect the target; analyzing large heaps uses substantial worker memory.

The process selector shows the **OS working set**. The workbench shows **managed object bytes**. These measure different things: native allocations, mapped files, GC free space, and reserved/committed heap space are not included in managed object sizes. Root reachability describes the captured instant, not a promise about the next collection or the operating system's memory return policy.

While a process is selected, the empty workbench shows its **private bytes** and **working set** live (sampled once a second while the profiler is visible; **Live** pauses it), so you can watch memory climb as you use the app before capturing anything.

After a capture, the second toolbar row switches between five views (**Ctrl+1–5**), shows the snapshot's source, runtime and load time, and holds the baseline controls. **Go to type or 0x address** (**Ctrl+G**) filters the type list by name, or opens the object at a hexadecimal address. Incomplete coverage is shown as a warning banner with the coverage notes. The object browser docks on the right of every view; the sidebar button hides it.

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
| Types that grew in every snapshot | Computed in the UI from three or more snapshots of the same process: types whose instance count rose every time. Application types make it a likely leak. |
| Types that grew since the baseline | Computed in the UI from the snapshot being compared with: types that gained objects and bytes. |
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
- **Instances**: objects of the type, largest retained first, with address or type search and paging. **Group by** turns thousands of instances into a handful of groups that are alive for the same reason:
  - **Retention path**: instances that reach a GC root the same way, shown as a chain (`Cache.Pages › List<Page> · _items › Page[] · […]`). Repeated links (linked lists, trees) collapse into one step marked *repeated*, so lists of any length share a group; very deep chains keep both ends. The group holding the most memory is where a leak accumulates.
  - **Owner**: the object that exclusively owns each instance (its immediate dominator) and the field it uses.
  - **Generation**: Gen 0/1/2, the large and pinned object heaps.
  - **Value**: identical strings, arrays with the same contents, or objects whose fields are all equal (references compare by identity). Each duplicate group shows the bytes the extra copies waste.

  Groups show their count and retained bytes; expand one to see its largest instances. The 20,000 largest instances are grouped.

## Retention: the dominator tree

The dominator tree answers "who owns this memory?": each object owns everything nested under it, so if it became unreachable all of that would be freed. Sibling instances of one type are grouped (`12 × Byte[]`), each row shows the field or root it is held through, its retained size and share of the heap. At the top level the runtime's statics arrays are replaced by the static fields they store.

The treemap beside the tree shows what the selected node keeps alive. Double-click a rectangle (or a tree row) to drill in; the breadcrumb returns to the heap. Colours follow the type, so the same type keeps its colour as you drill.

**Sunburst** shows the same ownership several levels deep: the focused owner in the centre, what it keeps alive in the first ring, what those keep alive in the next, up to four rings. A segment's angle is its share of its parent's retained bytes, so the gap left in a ring is the parent's own size; owners below 0.4% of the centre fold into a *smaller* segment. Hover a segment to highlight its path, click to inspect it, double-click to drill in (the tree and breadcrumb follow), and click the centre to step back out.

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

## Snapshots: history, live memory and comparison

Every capture is kept in the session's snapshot history (up to 30). Only the newest snapshot keeps its worker and native snapshot; earlier ones keep their totals, type counts and findings for comparison. The **Snapshots** view (**Ctrl+5**) is the leak-hunting hub:

- **Process memory**: a live chart of the selected process's private bytes (area) and working set (line) with every snapshot marked by its managed heap size. The window (1 minute to 1 hour) is a maximum; a short recording fills the chart. Click a snapshot marker to compare with it. Without a live process (dumps), the chart plots the snapshots' managed heap side by side.
- **Snapshot cards**: time, managed heap, change since the previous snapshot of the same source, and which one is being viewed or used as the baseline. Click a card to compare with it; hover to remove it from the history.
- **Comparison**: headline changes (managed heap, kept alive, objects, and how many types grew in every snapshot), then every type's change: objects before → after, a diverging bar of the byte change (growth right in red, shrinkage left in green), and a trend line of its bytes across the process's snapshots. Filter by **Changed**, **Growing**, **New** or **All**, hide framework types, or search. Types that grew in every snapshot are badged and listed first. Double-click a row to open the type.

When you capture the same process (or dump file) again, the new snapshot is compared with the previous one automatically; turn off **Compare with previous capture** to stop that. **Set baseline**, or clicking a card, pins a baseline that later captures keep comparing with until you clear it. Until there is something to compare, the view shows a four-step guide: capture, repeat the action, capture again, confirm with a third capture.

Comparison uses type/module identity, counts, and own bytes. It does not treat addresses as stable object identities across moving collections. Types that disappear remain visible with negative count/byte deltas.

Growth appears as findings (growth since the baseline, and types that grew in every snapshot), in the Overview's growth list, in the Snapshots view and as a column in the type list (sort by **Growth since baseline**). Open a growing type, group its instances by retention path, and check which path accumulates instances; confirm that the operation's expected owner lifetime has ended before treating persistent growth as a leak.

## Right-click menus and the command palette

Every list of objects or types (type list, instances and groups, findings, largest owners, the dominator tree, the comparison table, the object browser's fields, root paths and retained types) and graph nodes share one right-click menu: **Inspect**, **Why is it alive?**, **What does it keep alive?**, **Show in graph**, **Open** the type, **Retention paths of this type**, **Group instances by retention**, and copy the address, type name, value or retention path. A right-drag on the graph still pans.

The command palette (**Ctrl+Shift+P**) adds **Capture memory snapshot**, **Open memory dump…**, **Compare memory snapshots**, **Set memory baseline**, **Memory: go to type or address** and **Export memory report…**.

## Export evidence

The export button writes a self-contained **HTML report** (headline numbers, generations, findings with their items, the biggest owners, types by retained size, the managed heap across snapshots and the type changes when snapshots were compared) that opens in any browser and can be attached to a bug report. Choose a `.json` file name instead for the raw investigation: the capture summary (including findings), baseline totals, snapshot history, comparison rows, the selected object's fields/root paths, the visible graph, the retained composition, the type's retention flow, instance groups and the removal estimate. Reports are evidence for review; they are not reloadable heap snapshots. Keep the original dump when another investigator needs to explore other objects.

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

Default analysis budgets are **1,000,000 objects** and **6,000,000 references**. Capture also reads disposed flags, reference-array lengths and up to 2,000,000 strings (500,000 distinct values, 64 million characters) for the inspections. Options allow up to 5,000,000 objects / 30,000,000 references. Larger limits can require gigabytes of worker memory, especially for large reference arrays; x86 address space is more constrained. Missing/corrupt data, omitted referenced objects/roots, and exhausted graph budgets make coverage incomplete. Estimates then explicitly remain provisional and can overstate reclamation. Live snapshots are immutable, so the worker reads them in cached 64 KiB pages (up to 512 MiB in the 64-bit worker, 96 MiB in the x86 worker) instead of one system call per read; dumps are read as before.

The worker retains the full bounded analysis; the UI separately limits graph neighborhoods to 120 objects / 400 references (12 owners and 16 references per hop, expandable 24 at a time), dominator pages to 150 nodes, sunbursts to four rings of 24 owners (3,000 segments), instance groups to 60 groups with 40 example instances over the 20,000 largest instances, retention flows to 5,000 instances, object pages to 200, matching type display to 5,000, reference lists to 200 per direction, fields to 128, strings to 200–256 characters, and delegate targets to 20. Root examples traverse at most 50,000 ancestors with 128 slots per displayed path. Display limits do not change full-graph dominator calculations. Follow a neighbor to explore beyond the current map. Root and graph truncation remain part of the inspection/report data.

Opening/loading a capture has a five-minute worker deadline; queries have a one-minute deadline. A timeout closes the owned worker and requires reopening the capture. Failed/cancelled replacement captures preserve the previous successful session. Closing WpfStudio disposes the active worker and native snapshot. Normal shutdown disconnects and waits for native snapshot cleanup and file-handle release; an unresponsive worker is terminated after the shutdown deadline.

## Validation

`WpfStudio.Profiling.Tests` exercises shared ownership, parallel slots, multiple roots, rooted/unrooted cycles, dependent handles, permanent frozen roots, incomplete coverage, a 100,000-object chain, and randomized graphs against an independent reachability oracle. Its real-process tests capture and load full dumps from .NET 10 and Framework 4.8 fixtures, including frozen string-literal eligibility, retention grouping and value grouping of identical arrays. `HeapExplorationTests` cover instance grouping (retention paths, owners, generations, repeated-link collapsing, hidden roots, budgets), the nested dominator tree behind the sunburst (tree-compatible keys, group drill-in, folding), retention flows (static fields shown as roots, instances held by two statics on both branches, hidden root kinds, column folding and flow conservation), dominator pages (sibling grouping, statics holders replaced by their fields), non-double-counted type retention, retained composition, root-path ordering, graph neighbourhoods, every automatic inspection, and label shortening.

`WpfStudio.Shell.Tests` exercises worker IPC against x64 .NET and x86 Framework processes and full dumps (including the packaged self-contained x86 worker), incompatible WOW64 recovery, baseline disappearance, capture cancellation/failure, stale/empty selection results, and worker disposal without terminating targets or leaving dump files locked. View-model tests cover browser back/forward history and lazy field expansion, findings and baseline growth, snapshot history (automatic comparison with the previous capture, pinned baselines, steady growth, new and gone types, removal), the HTML report, instance groups, sunburst drill-in, live memory samples, retention-flow root filters, graph expansion merging, and type navigation.

The WPF smoke test exercises the live memory chart before capture, real capture, right-click menus, retention and value grouping, the sunburst, the Snapshots view with three captures and steady growth, the Overview (KPIs, generations, findings, treemap), the type Sankey (static cache and static event roots), collection expansion in the field tree, the vertical graph with expansion, focus, fit and zoom, the dominator tree and treemap, root paths, shared-root and exclusive-release estimates, baseline growth, a short docked layout, docking reuse, light/dark rendering, and zero binding errors. Screenshots are written to `artifacts/screenshots/memory-*.png`. For a focused run (which also enlarges the window for review screenshots):

```powershell
$env:WPFSTUDIO_TEST_MEMORY_ONLY = '1'
dotnet test tests/WpfStudio.App.Tests/WpfStudio.App.Tests.csproj
Remove-Item Env:WPFSTUDIO_TEST_MEMORY_ONLY
```
