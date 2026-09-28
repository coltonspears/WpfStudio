# XAML language worker performance

**Status: measured and regression-tested on 2026-09-28.** The resource optimization package returned correct completion and diagnostics at all three tested sizes, with warm completion p95 of 4.99–12.37 ms on this workstation. The prior worker could not resolve the 600-file fixture within its resource capture budget. A later named-element integration package also passed this workload; its separate measurements appear below. These are synthetic worker measurements, not end-to-end typing latency or a comparison with Visual Studio.

## Workload and correctness gate

The opt-in [XAML worker benchmark](../tools/XamlLanguageBench/README.md) creates temporary SDK WPF projects and calls the real workspace worker through its normal client and named-pipe protocol. The comparison uses 20, 200, and 600 evaluated XAML files and 30 iterations. Each fixture contains one consumer, three nested resource dictionaries, and unrelated views containing about 6 KiB of control markup each.

The consumer binds `Name` through a resource declared in the last dictionary. A dependency-only update alternates that declaration between unrelated `Customer` and `Order` types using an unsaved overlay. The consumer stays byte-identical at version 1 and the dictionary stays unchanged on disk. Correct completion offers `Name` for `Customer` or `Title` for `Order`; analysis reports the exact missing `Name` span only for `Order`.

Every observation must have the expected accepted/available state, version, semantic result, and **null response status**. An incomplete or unknown result fails even if it is fast or contains some expected items. An unavailable resource type cannot count as a performance improvement. The 600-file fixture intentionally exceeds the former 512-file eager resource capture limit while requiring only a small import chain for the requested result.

Restore and workspace load are recorded separately. Each fixture then records first completion/analysis, repeated warm completion/analysis, and dependency-update calls. The update-cycle measurement includes its completion and analysis calls together; it is not an additional independent request. The report retains individual observations, correctness counts, and nearest-rank p50/p95/max summaries.

## Implementation under measurement

The worker separates resource identity from captured text:

- A complete evaluated catalog retains up to 16,384 identities and 16 million metadata characters. It includes unloaded candidates and duplicate URI mappings, so capturing only one candidate's bytes cannot falsely establish uniqueness.
- An ordinary editor request reads its current source, the unique current-project application document where established, and their transitive dictionary imports. Text capture remains bounded to 512 file/context identities, 8 million decoded characters total, and 1 million characters per file.
- Import discovery and semantic lookup share URI resolution and framework schema checks. Physical file reads are deduplicated, while each logical origin retains its relative URI base. Missing, locked, ambiguous, or omitted consulted dependencies remain unknown.
- Explicit open-buffer overlays take precedence. Consulted closed dependencies are reread by content; timestamps and lengths alone do not establish freshness. No mutable overlay, parse, or lookup state is shared between operations.
- Batch analysis and rename keep their bounded full evaluated scans and existing completeness requirements. The immutable snapshot memoizes resource fingerprints per project, avoiding repeated catalog traversal and hashing for each batch consumer.

The [resource-resolution architecture](xaml-resource-resolution-plan.md) documents supported resource declarations, conservative boundaries, and transaction guards. This optimization changes which bytes ordinary requests read; it does not broaden runtime resource inference or relax rename safety.

## Compilation metadata reuse

The schema resolver's cache is keyed by the actual immutable Roslyn `Compilation` in a `ConditionalWeakTable`. It stores immutable assembly identities and XML namespace mappings, including indexes by XML namespace URI and assembly name. Import discovery and semantic lookup can reuse those mappings instead of repeatedly enumerating the same assembly attributes.

A changed C# buffer, generated source, or reference snapshot produces a different compilation key. The weak table allows otherwise unused compilations and their cached metadata to be collected. Request cancellation tokens, XML elements, resource texts, overlays, and lookup results are not cached. Canceled or failed construction does not publish partial metadata; concurrent construction may occur, but only a complete immutable value is retained. Four regression cases cover changed/generated/reference compilations, cancellation, and concurrent callers.

## Measurement sequence

The initial comparison reports are local artifacts named `xaml-resource-scale-before.json` and `xaml-resource-scale-after.json`. They are intentionally referenced by filename, because ignored build artifacts are not available through repository documentation links.

The prior packaged worker recorded 152 correct observations out of 152 at both 20 and 200 files. At 600 files, all 152 observations were incorrect because resource capture reached the old snapshot limit. Both reports completed without cancellation or fixture setup errors.

The first after-run recorded 152 correct observations out of 152 at each fixture size, including 600 files. **That run is intermediate and unaccepted for final performance claims:** latency variance and a small-fixture regression prompted the compilation metadata cache described above. The cached Release output then passed all observations in `xaml-resource-scale-cached.json`, with warm completion p95 of 6.21, 6.86, and 7.76 ms. The table below uses the subsequent packaged-worker run, including its slower observations.

Both initial reports identify .NET runtime 10.0.11, SDK 10.0.400, Windows build 26100, and 24 logical processors. Their explicit worker paths differ: the before-run used the prior package, while the intermediate after-run used a Release development output. The recorded payload hashes distinguish those implementations; these machine details alone do not establish equivalent machine load or cache state.

## Resource optimization results

The final package was measured with the same fixture sizes, 30 iterations, explicit worker DLL and compiled benchmark client (`xaml-resource-scale-after-packaged.json`). No test suites or builds ran during the benchmark. Each fixture contains **122 RPC requests plus 30 combined-cycle observations**, totaling 152 observations. All 366 requests and 90 cycle observations passed; there were no setup errors or cancellations.

| Evaluated XAML files | Correct observations (prior → final) | Prior completion p95 (ms) | Final completion p95 (ms) | Final analysis p95 (ms) | Final dependency-update cycle p95 (ms) |
| --- | --- | --- | --- | --- | --- |
| 20 | 152/152 → 152/152 | 34.46 | 4.99 | 11.48 | 17.59 |
| 200 | 152/152 → 152/152 | 87.20 | 10.05 | 17.55 | 10.75 |
| 600 | 0/152 → 152/152 | Unresolved; not comparable | 12.37 | 13.87 | 13.05 |

Final warm completion p50 was 3.85, 4.86, and 5.67 ms; the corresponding maxima were 5.02, 19.36, and 18.32 ms. Warm analysis maxima were 19.47, 17.83, and 24.05 ms. First completion still took **500.07, 493.83, and 466.35 ms after workspace load**. The warm numbers must not be used as first-use or startup estimates. Prior analysis p95 at the two correct sizes was 55.81 and 103.82 ms. The 600-file baseline's failed answers do not establish a speed ratio for successful analysis.

[Machine-readable summary excerpts](performance/xaml-resource-scale.json) retain the original four runs and the later integration run, including counts, phase distributions, setup timings, report hashes and worker payload hashes. Full local reports retain the individual observations. Both final resource-optimization source and package runs used Workspace DLL SHA256 `65CA2191EE072E1AD4B2F00FA21766EEB756F00EEAE659AD7928FB658A1B7344`; that measured packaged host DLL hash is `62003FD2D00DDA4E4214D2BDCF21238E79E878626539F67C9535360E67A04BA5`. The summary also records each run's contracts DLL and prior payload hashes.

The clean Release build and **876 distinct affected tests** passed: Workspace 400, Shell 474, loaded app UI 1, and isolated SDK 8 compatibility 1. The last covers the ordinary Workspace run's opt-in skip. The 21 new Workspace cases cover bounded indexes and duplicate identities, linked logical origins and shared URI rules, unavailable later merges, large-catalog language operations, unchanged-consumer overlays, same-length/same-timestamp changes, complete-scan rename guards, and schema cache isolation. **Seven overlapping integration cases** also passed with the packaged worker explicitly selected. See the [validation record](../VALIDATION.md) for exact reports.

## Named-element integration follow-up

After adding shared named-element analysis, the refreshed packaged worker passed the same 20/200/600-file resource workload and 30 iterations. All **456 observations** were correct (366 requests and 90 combined-cycle observations), with no setup errors or cancellation. No tests or builds ran during measurement. The local report is `xaml-namescope-after-packaged.json`; its summary is the fifth run in the tracked JSON above.

| Evaluated XAML files | Completion p50 / p95 / max (ms) | Analysis p95 (ms) | Dependency-update cycle p95 (ms) | First completion (ms) |
| --- | --- | ---: | ---: | ---: |
| 20 | 3.75 / 12.62 / 18.34 | 12.11 | 14.66 | 531.53 |
| 200 | 4.02 / 7.27 / 14.43 | 25.46 | 12.02 | 484.05 |
| 600 | 5.21 / 7.32 / 12.89 | 12.97 | 14.25 | 483.78 |

The changed tails include slower 20-file completion and 200-file analysis p95 than the earlier package. These single-workstation runs do not isolate the cause or establish a statistically significant speed change. All samples are retained. This follow-up checks the existing resource workload through the integrated language services; it does not measure large named-element scopes or editor typing latency.

The measured Workspace DLL SHA256 is `5757950F5C335913CD81FAFBE9B2DCF2BD8B7EB8B4B25171C32D9C78B09831BA`, and the selected host DLL hash is `3B1CA327C4B261B8580C1BD579833831A7916515D61DCA1BA82C6DBF19CBAE57`. The report also records the contracts DLL hash, SDK 10.0.400 and runtime 10.0.11. The [validation record](../VALIDATION.md) contains the separate named-element correctness and packaged .NET 8–10 checks.

## Reproduction and limits

Use the build/run commands and options in the [benchmark README](../tools/XamlLanguageBench/README.md). Select an explicit worker with `--host`, keep that package unchanged during the run, and supply a separate `--output` path for each measurement. The report records SHA256 hashes of the selected host file and adjacent `WpfStudio.Workspace.dll` and `WpfStudio.Contracts.dll` when present. Default client discovery does not populate those explicit-path hashes. Preserve the reports with their measured payloads and compare identical sizes, iteration counts, SDK, runtime, configuration, and hardware.

This is a sequential RPC benchmark including client transport and serialization. A first request occurs after restore and workspace loading; it is not a cold-machine or application-startup measurement. The repeated phases may include JIT/tiered compilation and garbage collection; no samples are discarded. Synthetic markup, warm filesystem/SDK caches, background machine load, and a single machine limit generalization. There are no statistical confidence intervals or broad hardware claims.

The benchmark does not measure editor debounce, end-to-end keystroke latency, rendering, native preview input, concurrent requests, process memory, or physical interaction. It exercises ordinary language requests, not the full batch/rename workload. Keep those distinctions when reporting results: successful large-project resource completion is established by semantic evidence and freshness tests; an interactive typing experience requires separate UI validation.
