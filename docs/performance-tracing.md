# Performance tracing

WpfStudio measures itself while it runs, so that "it felt slow" can be answered with where the time went. Nothing leaves the machine: the measurements are counted in memory, slow operations are written to a local log, and a report is shown when you ask for one.

**Status: built on 2026-11-11 without a Windows machine.** The measuring core, the language worker's side and the log are tested natively; the UI thread monitor was run on the .NET 10 Windows desktop runtime under Wine. Nothing here has run on Windows yet. See [VALIDATION.md](../VALIDATION.md#performance-tracing-2026-11-11).

## Using it

1. Do the thing that feels slow for half a minute: type in the file, ask for completions, let the preview follow.
2. Choose **Help > Performance report** (also in the command palette).

The report appears in Output and is appended to `Logs/performance.log` in the data folder (**Help > Open data folder**). Asking for a report starts the counting again, so the next report covers only what you do after this one. To measure one action, ask for a report, do the action, and ask again.

The same log receives a line for every operation that goes over its limit at the moment it happens, and a report when the application closes. A stall of the UI thread of half a second or more also puts one line in Output, at most once every ten seconds.

The log holds names of methods and input events, durations and counts. It holds no source text, no file names and no keys: an input event is recorded as `TextInput` or `KeyDown`, never as what was typed.

## What is measured

| Area | One measurement is | Reported by itself from |
| --- | --- | --- |
| `ui wait` | How long the UI thread took to reach work it was just given, sampled ten times a second. This is the delay a key press meets before WPF starts on it | 150 ms |
| `input` | One input event on the UI thread, from before its handlers to after them. Typing a character is `TextInput`, and everything that runs synchronously because the text changed is inside it | 50 ms |
| `edit` | One part of what a text change sets off inside the key press that made it: `text changed: everything`, and within it the editor's context subscribers, its Git margin and blame scheduling, the start of analysis, and the designer's invalidation | 30 ms |
| `completion` | A completion list from being asked for to being on screen: synchronizing the buffer, the worker's answer, ranking and building the list. Lists that were cancelled by more typing are not counted | 250 ms |
| `ui` | One dispatcher operation: layout, rendering, a posted callback, or an `await` resuming on the UI thread | 100 ms |
| `worker call` | A request to the language worker as the IDE sees it, from before the request is written to its answer | 250 ms |
| `worker` | The same request inside the worker, from the call into the method that serves it to its answer, with what the garbage collector did meanwhile | 250 ms |
| `preview call` | A request to the preview process: its wait for the request before it, starting a host when one is needed, and the request | 250 ms |
| `wait` | A request that found another in its way: an edit waiting for the worker connection, or a render waiting for a superseded one to finish. Only waits that were not immediate are counted | 250 ms |
| `assist` | One Code Assist model run, on the pool thread that ran it | 100 ms |
| `pool wait` | How long the thread pool took to start work it was just given, in the IDE and in the worker | 150 ms |
| `startup` | Time from the start of the process to the window being shown and to the shell being initialized | always |

Every measurement is counted, fast or slow. The limits only decide what gets a line of its own in the log.

### Names on the UI thread

A dispatcher operation is named after the code it runs, without that code being changed:

- `DesignerViewModel.RenderAsync (resumed)` is an `await` in `RenderAsync` resuming on the UI thread, and the time is what ran until its next `await`.
- `EditorViewModel.AnalyzeAsync (lambda)` is a lambda in `AnalyzeAsync` that was posted to the dispatcher. `(lambda, resumed)` is an async lambda resuming.
- `MediaContext.RenderMessageHandler` and `ContextLayoutManager.UpdateLayoutCallback` are WPF rendering and layout.
- `operations under 1 ms` and `events under 1 ms` count what was too short to be worth naming. A large count with a large total there means many small operations, which no single name would show.

An operation that runs a message loop of its own, as a dialog does, is not counted: its duration is how long the dialog was open.

Naming reads two private fields of WPF's `DispatcherOperation`. They are present in the .NET 8 and .NET 10 desktop runtimes; without them operations are still timed and appear as `dispatcher operation`.

## Reading a report

This is a real report from the language worker, loading a one-file project and answering one completion request on a four-core Linux machine:

```
Language worker, last 3.92 s · GC: server, blocking; paused 63 ms in all (1.6% of the time); 1 gen2 of 9 collections; heap 18 MB; 4 cores
  area         count     total       p50       p95       max  name
  pool wait       37    207 ms    0.2 ms     66 ms    130 ms  thread pool
  worker           1    2.61 s    2.61 s    2.61 s    2.61 s  LoadAsync
  worker           1    885 ms    885 ms    885 ms    885 ms  GetCompletionsAsync
  worker           1     46 ms     46 ms     46 ms     46 ms  UpdateDocumentAsync
```

and the lines the log received for the same requests, one from each side:

```
worker worker           2.61 s  LoadAsync · GC paused 22 ms (1 gen1, 7 gen0)
shell  worker call      2.94 s  LoadAsync
worker worker           885 ms  GetCompletionsAsync · GC paused 41 ms (1 gen2)
shell  worker call      906 ms  GetCompletionsAsync
```

- `input TextInput` is a whole keystroke as WPF handles it. `edit text changed: everything` is the part of it that is this application's reaction to the new text, and the other `edit` rows are parts of that. What is left of `TextInput` is the editor control inserting the character.
- **count** against what you did says what reruns. Sixty `TextInput` events and sixty `AnalyzeXamlAsync` calls mean an analysis per character; sixty and four mean the pause before analysis is working.
- **total** is where the time went, and the rows are sorted by it within an area.
- **p50** and **p95** are upper bounds, at most a quarter above the true value. **max** is exact.
- The header's garbage collector line is for the whole process. A blocking collector stops every request at once for the length of a collection, which the pause time and the `GC paused` detail on slow requests show.
- `worker call` minus `worker` for the same request is what the connection cost: writing and reading the request and the answer, and the time the request stood behind others in the worker.

Median and percentile are estimated from counters, a quarter of an octave wide, so a report costs the same after a day as after a minute.

## Recording a trace

The measurements are also events of an `EventSource` named `WpfStudio`, for a tool that records the process from outside. On one timeline with CPU samples they say what the code was doing during a slow operation, which durations alone cannot.

```bash
dotnet tool install -g dotnet-trace
dotnet-trace ps
dotnet-trace collect -p <process id> --profile dotnet-common,dotnet-sampled-thread-time --providers WpfStudio:0xFFFFFFFFFFFFFFFF:5
```

The keyword mask and level are needed: with `--providers WpfStudio` alone, dotnet-trace 10 enables the provider with no keywords and records none of its events. An event is written at the end of the operation it describes, so the operation covers the given duration before the event's time. `SlowOperation` events are written from level 3, every `Operation` at level 5. Open the `.nettrace` file in PerfView or Visual Studio, or add `--format Speedscope` for [speedscope](https://www.speedscope.app).

The IDE and the language worker are separate processes and each needs its own recording.

## Cost

Recording one measurement is two clock reads and a few interlocked additions: about 75 ns in all in a release build on a four-core Linux machine, of which the recording is 13 ns when the caller keeps its counter and 50 ns when it gives a name. Nothing is written or allocated for an operation that was fast, and the name of a dispatcher operation is looked up only when the operation took a millisecond or more. The two probes each wake a thread ten times a second. There is no switch to turn the tracing off.

What the hooks cost the UI thread per operation on Windows has not been measured.

## What it does not show

- **Stacks.** A stall is described by the operations that ran during it, not by what a method was executing. Record a trace for that.
- **Time inside the preview process.** A render is timed from the IDE; which part of it was parsing, layout or encoding is not.
- **Waiting inside the worker.** A request that stood behind another before its method was called shows only as the difference between `worker call` and `worker`.
- **The UI thread outside the dispatcher.** Time spent in a window's own modal loop, such as dragging or resizing it, is reported as a wait that "nothing the dispatcher ran accounts for".
