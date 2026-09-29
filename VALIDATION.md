# Validation record

Validation was performed on this Windows 11 x64 workstation on 2026-09-26–28. The machine has a Ryzen 9 5900X, approximately 96 GiB of RAM, .NET SDKs 9 and 10, Desktop runtimes 8–10, WebView2, and SQL Server LocalDB. An isolated SDK 8.0.425 installation was also used for compatibility verification without changing the system toolchain. Full Visual Studio is installed on this workstation; this is not a clean-machine certification.

## Change review, diff view and Git workbench (2026-09-29)

The before/after review showed two plain text boxes in a fixed-width dialog, without syntax colouring or change markers, and its horizontal scroll bar sat directly under the text. It now fills the window with a list of changed files and their line counts, and a shared diff view:

* `TextDiff` (Core) is a Myers line diff with prefix/suffix trimming and a bounded edit distance. It pairs changed blocks into modified rows and marks the changed words inside them. Line-ending-only differences compare equal.
* `DiffView` (App) shows a syntax-highlighted side-by-side or inline diff. It uses AvalonEdit's C#, XML/XAML, JSON and SQL definitions. Unchanged regions collapse to three lines of context and expand on click, or **Whole file** shows everything. **F8**/**Shift+F8** and the overview ruler move between changes, and the view opens scrolled to the first one. Both columns scroll together, and their horizontal scroll bars are pinned to the bottom. Views narrower than 720 px switch to inline, and the toolbar compacts to fit. New and deleted files show a single column.
* The theme's multi-line `TextBox` now stretches its content host, so wrapped and scrolling text boxes keep the horizontal scroll bar at the bottom edge everywhere.

The Git pane was rebuilt around the same view:

* **Changes** lists staged and working files with status letters and hover stage/unstage actions. The selected file's diff loads immediately, against the index or HEAD, with the working-tree file or staged blob as the new side. Selection survives stage, unstage and refresh.
* **History** shows author avatars, branch and tag chips, relative dates, a filter, and paging beyond the first 50 commits. The selected commit shows its full message, author, parents and changed files with line counts, and a diff of each file against its first parent. Binary, oversized and submodule entries explain why no text is shown.
* The branch name in the header opens a switch/create picker. All diff reads use `--no-optional-locks`, so they never hold the index lock that a stage or commit needs.

The Release solution build passed with zero warnings and errors. The Release suites below ran on this workstation:

| Suite | Passed | Notes |
| --- | ---: | --- |
| Core | 231 | 13 added for `TextDiff`: identical/CRLF input, insertion alignment, word spans, dissimilar lines, unequal blocks, new/deleted files, hunks and context, bounded fallback, line splitting |
| Shell | 531 | 4 added: staged/working/new/deleted/binary versions, commit decorations/details/renames/root commits, `diff-tree` record parsing, and view-model paging, filtering, selection and restore after staging; the existing diff test now checks both versions |
| Preview | 241 | Unchanged |
| Native view | 61 | Unchanged |
| Loaded app UI | 1 | The change-preview step asserts the split diff and its added line; new captures `git-history*.png`, `git-workbench*.png` |

These checks do not establish physical pointer, wheel or keyboard acceptance of the new views.

## Designer canvas and live preview continuity (2026-09-28)

The XAML Designer was reorganized around its canvas: one toolbar, an icon outline that folds template parts, a fitted artboard sized from the root's `Width`/`Height` or `d:DesignWidth`/`d:DesignHeight`, zoom and pan controls, hover outlines, a labelled selection adorner, authored-element picking (**Ctrl+click** for template parts), editor selection mirroring, and property search with a set-values filter. See [working on the canvas](docs/xaml-preview-interaction.md#work-on-the-canvas).

Exercising the previous designer on the CounterApp sample reproduced four live-preview defects, now fixed:

* Every keystroke cleared the canvas, outline and properties for the 650 ms debounce plus the render. The last successful frame now stays visible, dimmed, until the new render lands; picking and inspection still require the current render.
* Unrenderable XAML left an empty canvas with the error only in the Diagnostics tab. A banner now shows the first error over the last frame, with **Go to error**.
* Each re-render dropped the selection. It is restored by unique `x:Name`, then authored position, then visual position, together with the selected property row, without moving the editor caret.
* A render superseded by newer typing terminated the preview process, so the next render cold-started a new one. The client now lets the healthy host finish in the background for up to three seconds, holding request order, and replaces only a host that is still busy.

The designer's TreeView item style also lacked `BasedOn`, which dropped the theme's full-row selection and chevrons; Live XAML had the same omission. Both now use the theme. The live-preview scenario list is no longer rebuilt on each render while its configuration is unchanged. The theme's `ActivityBar` starts its animation on `Loaded`, which throws for a bar that is collapsed before its template is built, so the designer creates its busy bar only while busy.

The Release solution build passed with zero warnings and errors. The Debug suites below ran on this workstation:

| Suite | Passed | Notes |
| --- | ---: | --- |
| Shell | 527 | 11 added: last frame, error banner and navigation, document switch, selection and property restore (named and unnamed), editor-mirroring echo guards, pick preference, outline folding, fit zoom, artboard presets, property search |
| Preview | 241 | 8 added: root/design-time artboard sizing and clamping, authored versus exact picking, superseded render keeps its host process |
| Native view | 61 | Unchanged surface gesture tests pass with hover and exact picking added |
| Loaded app UI | 1 | Extended: the broken-source step asserts the retained frame, visible error banner and restored `Greeting` selection against the real preview host |
| Runtime | 16 of 17 | `TerminalIntegrationTests.UnicodeAndControlCTravelThroughThePseudoconsole` times out in this automation session; it fails identically on unmodified `main` and does not involve the designer |

The [feature tour](docs/xaml-feature-tour.md#work-on-the-designer-canvas) adds two captures from this run and refreshes the designer and Live XAML captures. The portable package was not republished for this change. These automated checks do not establish physical pointer, wheel or keyboard acceptance of hover, zoom, panning or selection mirroring.

## Visual layout editing (2026-09-28)

Source previews now offer opt-in movement and eight-handle resizing for verified direct children of framework Canvas and Grid panels. Pointer gestures show draft bounds and snap guides; keyboard gestures support nudging and resizing. One completed gesture produces one reviewed source transaction and one workspace Undo. Canvas anchors and existing Grid tracks are preserved. The host checks source, parent, geometry and property provenance before review and again before applying. Unsupported or stale contexts explain why editing is unavailable.

The Release solution build passed with **zero warnings and errors** (`artifacts/TestResults/xaml-layout-editing/build-final.log`). All six affected suites then passed **1,046 checks**, with no failures or skips:

| Suite | Passed | Report in `artifacts/TestResults/xaml-layout-editing` |
| --- | ---: | --- |
| Core | 218 | `core-final.trx` |
| Shell | 516 | `shell-final.trx` |
| Preview | 233 | `preview-final.trx` |
| Native view | 61 | `nativeview-final.trx` |
| Runtime | 17 | `runtime-final.trx` |
| Loaded app UI | 1 | `app-final.trx` |

The 86 added cases cover layout calculation and atomic lexical edits (35), Shell review/undo/stale-context guards (8), host provenance plus real WPF rerendered geometry (26), and STA pointer/keyboard/capture/rendering behavior (17). Real rerenders check leading/trailing Canvas anchors, Grid alignments, Auto dimensions and opposite resize anchors. The extended loaded-app workflow verifies the visible draft, snapping, reviewed source change, unchanged disk file, rerendered bounds and exact Undo. The full workflow also passed its binding-error checks.

Initial test runs exposed desktop-pointer synchronization entering the synthetic adapter harness and an animation clock that had not begun ticking. The harness now excludes unrelated routed mouse moves while retaining real capture/lifecycle behavior, and the animation test seeks its controllable clock before asserting an active animation. The final suites retain all assertions. The capture-only follow-up wraps the fixture's Button attributes so the changed width is visible in the review; its build also passed with zero warnings/errors (`capture-build.log`).

The win-x64 package was refreshed (`publish-final.log`). The overlapping layout loaded-app workflow passed with `WPFSTUDIO_PREVIEW_HOST_UNDER_TEST` explicitly selecting its published host (`packaged-layout-ui.trx`), including both validation RPCs, source apply, rerender and Undo. The final packaging-only change repairs two existing notice links for the portable folder; the tested app and preview-host assembly hashes were unchanged by that republish. Fourteen project Markdown files match their source hashes, all fourteen capture/source/package image triples match, and 123 local links across the seventeen packaged Markdown files were verified. No new portable executable startup timing was collected.

The [feature tour](docs/xaml-feature-tour.md#move-and-resize-with-a-source-review) adds actual loaded-app captures of the draft and review. These automated captures and input-adapter checks do not establish physical-input acceptance. [Layout editing limits](docs/xaml-preview-interaction.md#move-and-resize-authored-elements) include reparenting, Grid track reassignment, toolbox insertion, templates, transformed/right-to-left frames and layout expressions. The [remaining roadmap](docs/xaml-devtools-design.md#full-goal-acceptance-ledger) still applies; this milestone does not establish Visual Studio parity or superiority.

## Current-source XAML fields (2026-09-28)

Direct name, type, insertion and removal edits now update editor-derived C# page declarations from current evaluated XAML. The worker retains authored sources separately from its semantic solution, replaces only positively identified WPF page output, and leaves generated files unchanged on disk. Completion, diagnostics and C# F12 use current source; editor declarations do not establish runtime identity or replace compiling saved XAML. Reviewed name edits synchronize authored buffers and derived fields together without retaining compiler-baseline rename history.

Real-worker fixtures cover .NET 8–10, unbuilt evaluated pages, linked project contexts, independent discard, restart, malformed typing, authored collisions and custom control base changes. Shell cases verify refresh of unchanged C# editors and deterministic cancellation-after-commit notification recovery. The loaded-app workflow changes an unsaved named TextBox into a PasswordBox and observes the unchanged C# editor report CS1061 for `.Text`, then verifies direct rename completion and exact XAML navigation. The [feature tour](docs/xaml-feature-tour.md) includes the actual capture.

Review and regression runs caught and corrected shared synthetic paths across linked projects, an unsafe generated-document dereference, an accepted-text notification cache after cancelled mutations, and unnecessary semantic replacement on the first unchanged XAML synchronization. A smoke-test fixture now restores its unsaved buffers before opening the next workspace; its deliberate Cancel response had correctly prevented that transition. Existing stale-response assertions remain intact.

The final Release solution build passed with **zero warnings and errors** (`artifacts/TestResults/xaml-live-fields/build-final2.log`). The complete affected suites then passed **1,116 distinct checks**, with one opt-in SDK 8 check skipped and no failures:

| Suite | Passed | Result in `artifacts/TestResults/xaml-live-fields` |
| --- | ---: | --- |
| Workspace | 607 | `workspace-final2.trx` |
| Shell | 508 | `shell-final2.trx` |
| Loaded app UI | 1 | `ui-final2.trx` |

This includes 32 focused page-model cases, 13 new real-worker cases and six new Shell cases. Earlier focused reports overlap these totals. The isolated SDK 8 check was not rerun; the ordinary real-worker fixtures exercise .NET 8, 9 and 10 target metadata. All twelve documentation images were refreshed from the successful final loaded-app run and copied unchanged. These are automated app captures, not physical-input acceptance.

The win-x64 package was refreshed (`publish.log`). All **13 overlapping live-field integration cases passed** with `WPFSTUDIO_TEST_WORKSPACE_HOST` explicitly selecting the published worker (`packaged-live-fields.trx`). All 14 packaged Markdown files, 112 local documentation file links and twelve capture/source/package screenshot triples were verified. No new portable executable startup measurement was collected.

The current model's [coverage and limits](docs/xaml-named-elements.md#current-xaml-fields-while-editing) and the [remaining designer roadmap](docs/xaml-devtools-design.md#full-goal-acceptance-ledger) still apply. This increment adds no physical-input, clean-machine, typing-latency or Visual Studio comparison claims. Earlier performance reports remain tied to their recorded payload hashes.

## Working XAML milestone for GitHub (2026-09-28)

This snapshot delivers the current authoring, isolated preview and opt-in live inspection workflows in the [feature tour](docs/xaml-feature-tour.md). The [remaining designer roadmap](docs/xaml-devtools-design.md#full-goal-acceptance-ledger) is future work rather than a prerequisite for this delivery. Earlier entries below record incremental development and retain the limitations that applied at each stage.

The complete Release solution build passed with **zero warnings and errors**. All nine test projects then ran sequentially with `-c Release --no-build --no-restore`: **1,773 passed, one opt-in check skipped, zero failures**.

| Suite | Passed | Skipped |
| --- | ---: | ---: |
| Core | 183 | 0 |
| Database | 41 | 0 |
| Runtime | 17 | 0 |
| Workspace | 562 | 1 |
| Shell | 502 | 0 |
| Preview | 207 | 0 |
| Inspection | 216 | 0 |
| Native preview controls | 44 | 0 |
| Loaded app UI | 1 | 0 |

Build and per-suite logs/TRX reports are under `artifacts/TestResults/release-snapshot/`. The skipped Workspace case requires the separately configured isolated SDK 8 toolchain; it was not rerun for this snapshot. The inspection suite exercises actual .NET 8–10 targets. The process-ownership script also passed all 26 assertions without launching or terminating processes.

The published worker passed all twelve overlapping name-projection integration cases in `artifacts/TestResults/xaml-devtools/name-projection-packaged-workspace.trx`. Production source has not changed since that package verification. The final loaded-app workflow refreshed all eleven actual application screenshots; the documentation and package contain byte-for-byte copies. Documentation links and packaged document/image hashes were checked again for this snapshot. Existing performance reports retain their original measured payload hashes; this pass adds no performance, clean-machine or physical-input claims.

## XAML DevTools: live compiler fields for reviewed names (2026-09-28)

Accepted page-name renames now synchronize authored buffers and generated-field changes together inside Roslyn. Requesting or cancelling the review leaves compiler state unchanged. Plans retain original compiler-checksummed XAML bytes, generated-document hashes and bounded rename steps; replay derives the generated text again rather than accepting replacement code. Current name metadata, field types/document identities and every linked owner are checked before committing. Generated output is never returned as an authored edit or written by projection.

The final Release solution build passed with zero warnings/errors (`name-projection-build3.log`). **1,065 distinct affected checks passed:**

| Suite | Passed | Results in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Workspace | 562 | `name-projection-workspace-full.trx` |
| Shell | 502 | `name-projection-shell-full.trx` plus `name-projection-navigation-final.trx` |
| Loaded app UI | 1 | `name-projection-ui-full.trx` |

The ordinary Workspace run retains one opt-in SDK 8 skip; the isolated SDK test was not rerun. The twelve new real-worker cases include .NET 8/9/10 fixtures, repeat rename of the same and another field, known-state undo, current C# completion/diagnostics/F12/references, linked contexts, worker restart before and after saving/closing, independent C#/XAML discard, closed C# refresh, cancelled/tampered requests, changed generated baselines, retired-name reuse by authored C#, changed namescope metadata and a cached replay with incoming C# field collisions. Rejected plans leave the prior authored versions valid. Three new Shell cases cover cancelled review, retaining the authored transaction after post-apply synchronization failure, and real rename/restart/undo with diagnostics refreshing in an unchanged C# editor.

The first focused run exposed legitimate WPF regeneration during workspace restart after saving XAML. Replay now adopts fresh output only after every current page field passes the ordinary checksum/type/name bridge in every owning project, and the loaded generated text matches disk. This retires the previous projection history; it does not replay an old generated overlay over the new compiler result. A Shell fixture initially captured generated bytes before MSBuild workspace loading could normalize its resource URI. Its comparison now begins after load; projection operations themselves preserve those bytes. Earlier failure reports remain local.

The full Shell run initially passed 501 cases. Its remaining navigation fixture attempted to reuse C# version 1 for different text after workspace Undo had now synchronized the original version. The fixture now edits the real editor buffer and synchronizes its incremented version, preserving all stale-action assertions. All three navigation cases passed on rerun; two overlap the earlier passes. The test-only build also passed cleanly (`name-projection-navigation-test-build.log`). Counts above deduplicate case-sensitive test IDs; focused runs overlap the full totals.

The complete loaded-app workflow passed, including new assertions that unsaved renamed C# has no compiler errors, completion includes the new field and omits the old one, C# F12 selects the exact current XAML declaration, and Undo restores healthy C# while generated bytes remain unchanged. All eleven [feature-tour captures](docs/xaml-feature-tour.md) were refreshed from that successful run and copied byte-for-byte. The rename review now explains the in-memory compiler update. These remain automated application captures, not physical-input acceptance.

The win-x64 package was refreshed (`name-projection-publish.log`). All **twelve overlapping projection integration cases passed** with `WPFSTUDIO_TEST_WORKSPACE_HOST` explicitly selecting the published worker (`name-projection-packaged-workspace.trx`). All 14 packaged Markdown files and eleven capture/source/package image triples matched their source hashes, and 101 local documentation links were verified. The existing performance summary also matches its packaged copy; its measurements still describe the earlier payload hashes recorded in that report. No new portable executable startup or performance measurement was collected.

This increment supports exact reviewed rename states, not arbitrary name/type edits or insertion/removal of named elements. Divergent unsaved XAML reports projection unavailable; a fresh compiler baseline retires old projection history, and undoing across that boundary can still require regeneration. History is limited to 32 steps, 32 generated documents per page and 32 projected pages, with per-model/workspace retained-text and aggregate proof-work budgets. The [named-element guide](docs/xaml-named-elements.md#live-compiler-fields-after-a-reviewed-rename) records lifecycle and limits. No new typing-latency, native-input, portable-startup or Visual Studio comparison measurements were collected; earlier performance reports retain their own payload hashes. The broader designer goal and GitHub delivery remain outstanding.

## XAML DevTools: named-element references and reviewed rename (2026-09-28)

Find References and reviewed Rename now connect a supported authored name declaration, its `ElementName` consumers and verified generated-field C# uses. Page and template identities remain separate even when their spelling matches. The bridge requires current compiler-generated WPF evidence: exact raw XAML checksum, mapped declaration line, root class and field type. Unsupported consumers, stale evidence, linked-context disagreement or new compiler conflicts block the complete rename. Generated edits exist only in the temporary compiler validation solution; the returned transaction changes authored source buffers.

The final Release solution build passed with zero warnings/errors (`name-refactor-build-final.log`). **1,050 distinct affected checks passed:**

| Suite | Passed | Results in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Workspace | 550 | `name-refactor-workspace-full.trx` |
| Shell | 499 | `name-refactor-shell-full.trx` |
| Loaded app UI | 1 | `name-refactor-ui-final.trx` |

The ordinary Workspace run retains one opt-in SDK 8 skip; the isolated-toolchain check was not rerun for this increment. The 75 added Workspace cases cover the checksum/generated-field bridge (30), real-worker refactoring (9), and authored occurrence/index validation (36). Real fixtures exercise .NET 8, 9 and 10 WPF metadata, linked pages, page/template duplicates, stale disk and buffer inputs, compiler conflicts and unsupported consumers. Two added Shell cases reject proposed edits to `.g.cs` and `.g.i.cs` without opening generated tabs or partially changing XAML. Focused reports (`name-refactor-focused.trx`, `name-refactor-shell-focused.trx`) overlap the full totals.

The loaded-app workflow verifies references from both languages, review/cancel, a two-file unsaved rename and one workspace Undo restoring both buffers. It checks that generated files are absent from results and edits. Inspection of the first successful run's capture exposed an existing Search label overlap; constraining the filename column fixed it, and the final complete UI workflow passed again. The [feature tour](docs/xaml-feature-tour.md) contains eleven actual loaded-app captures copied unchanged from the final run, including the three-location reference list and XAML/C# rename review. These are automated application captures, not physical-input acceptance.

The first build exposed one missing required resolver argument, which was corrected before testing. Separate real WPF probes confirmed the generated field's mapped attribute line, raw-byte checksum, root-name type and page/template distinction. A qualified `FrameworkElement.Name` attribute compiles without the ordinary generated field; the index now conservatively marks that unsupported name syntax instead of issuing a false missing-name warning. No application objects are constructed during language analysis.

The win-x64 package was refreshed (`name-refactor-publish.log`). All **nine overlapping name-refactoring integration cases passed** with `WPFSTUDIO_TEST_WORKSPACE_HOST` explicitly selecting the published worker (`name-refactor-packaged-workspace.trx`). The test-only build adding that host override also passed with zero warnings/errors (`name-refactor-test-build.log`). After the final documentation copy, all 14 packaged Markdown files and eleven image triples matched their source hashes, and 97 local documentation links were verified. The package includes the feature tour and named-element guide.

Live unsaved generated-field projection is still outstanding: after applying a page-name rename, save, build and reload to refresh C# field metadata. The review and [named-element guide](docs/xaml-named-elements.md#references-and-reviewed-rename) state this requirement. Broader name consumers and the [full designer acceptance ledger](docs/xaml-devtools-design.md#full-goal-acceptance-ledger) remain unfinished. No new native-input, typing-latency, portable-startup or Visual Studio comparison measurements were collected. Existing performance reports remain tied to their recorded earlier payload hashes. GitHub delivery remains pending completion of the broader work.

## XAML DevTools: named-element authoring (2026-09-28)

The workspace worker now shares a compiler-backed namescope index across `ElementName` completion, hover, definitions, declaration diagnostics, spelling fixes and binding-path source inference. It recognizes `x:Name` and proven runtime-name aliases, including inherited metadata and the actual `x:Class` root type. Standard templates have separate local scopes. Unknown/custom/runtime scopes remain uncertain, and a missing authored page name is a warning because runtime registration or outer scopes may supply it. The [named-element guide](docs/xaml-named-elements.md) records syntax, limits and unsupported forms.

The final Release solution build passed with zero warnings/errors (`namescope-build5.log`). **973 distinct affected checks passed:**

| Suite | Passed | Results in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Workspace | 475 | `namescope-workspace-full.trx` plus `namescope-attached-final.trx` |
| Shell | 497 | `namescope-shell.trx` |
| Loaded app UI | 1 | `namescope-ui-final.trx` |

The ordinary Workspace run retains one opt-in SDK 8 skip; that optional isolated-toolchain test was not rerun for this increment. The new real-worker cases passed against actual .NET 8, 9 and 10 WPF reference metadata and built corrected fixtures. They cover completion, checked fixes, hover, F12, batch/open-buffer precedence, unsaved declaration changes and worker restart. The 107 focused cases (`namescope-focused3.trx`) overlap the full totals. Counts deduplicate by case-sensitive test ID.

Initial real-metadata tests exposed that WPF's standard `DependencyObject.NameScope` metadata identifies the normal namescope carrier; it does not make every element a custom scope. The index now distinguishes that exact framework metadata, including `NameScope` in WindowsBase, from genuine custom boundaries. One older synthetic attached-path fixture lacked resource/template metadata; after adding faithful stubs, all 42 cases passed without changing their assertions. They overlap 41 earlier passes and resolve the full Workspace run's single failure. Earlier failure reports remain local.

The loaded app workflow verifies the visible typo warning, name completion, an unsaved checked correction, hover, same-editor F12 and restoring the diagnostic after workspace Undo. Its first post-undo assertion raced the queued resource-snapshot invalidation. Draining the dispatcher before explicit analysis resolved the test race; the final full UI workflow passed with no binding errors. The [feature tour](docs/xaml-feature-tour.md) contains nine actual loaded-app captures copied byte-for-byte from that successful run. The added image shows the `XAMLNAME004` warning, not an autocomplete or quick-fix popup. These captures do not establish physical-input behavior.

The local win-x64 package was refreshed (`namescope-publish.log`). Three overlapping .NET 8–10 named-element integration cases passed with `WPFSTUDIO_TEST_WORKSPACE_HOST` explicitly selecting the packaged worker (`namescope-packaged-workspace.trx`). The package includes the named-element guide and all nine feature-tour images. Source, capture and packaged image hashes match; local documentation links and packaged document hashes were verified after the final documentation copy. No new portable startup timing was collected.

The existing sequential resource benchmark also passed all 456 observations at 20, 200 and 600 files with 30 iterations (`xaml-namescope-after-packaged.json`). Warm completion p95 was 12.62 / 7.27 / 7.32 ms; first completion took 484–532 ms after workspace load. The [performance report](docs/xaml-language-performance.md#named-element-integration-follow-up) and tracked summary retain all phases and the exact measured payload hashes, including the slower tails relative to the earlier package. This is a resource-workload regression check, not named-element scale, typing-latency or Visual Studio comparison evidence.

Named-element Find References, cross-language generated-field Rename, broader name-reference forms and the [full designer acceptance ledger](docs/xaml-devtools-design.md#full-goal-acceptance-ledger) remain unfinished. GitHub delivery remains pending completion of that broader work.

## XAML DevTools: shared binding explanations and cached path steps (2026-09-28)

Preview and Live XAML now share current binding observations, bounded validation rows, historical WPF evidence and a selected-expression explanation panel. The cached path adapter identifies the resolved prefix and first unresolved step without invoking model getters, converters or custom descriptor metadata. WPF can erase the failed owner's reference, so null intermediates and missing nested members remain distinct possibilities. Private field/type/enum shapes are guarded; unsupported or incomplete observations cannot establish recovery. Composite parent statuses and individual child observations remain separate.

The Release solution build passed with zero warnings/errors (`binding-path-build-final3.log`); the subsequent navigation-test-only build also passed cleanly (`binding-path-inspection-test-build.log`). **921 distinct affected tests passed**, with no unresolved failures or skips:

| Suite | Passed | Results in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Preview | 207 | `binding-path-preview-full.trx` |
| Inspection | 216 | `binding-path-inspection-full.trx` plus `binding-path-inspection-source-final.trx` |
| Shell | 497 | `binding-path-shell-final3.trx` |
| Loaded app UI | 1 | `binding-path-ui-final.trx` |

The Inspection run passed 213 cases initially. Three navigation assertions compared the newly enriched inspection record with a navigation response that intentionally contains only revalidated declaration/source data. Those assertions now exclude the diagnostic observation and explicitly require its absence from navigation responses. All five source-navigation cases passed on rerun, including the three corrected cases; two overlap the original 213. Counts above deduplicate by test ID, including case-sensitive theory inputs. Earlier failed reports remain available locally.

The 18 new Preview cases include guarded path/identity readers, two source/compiled host workflows, child-only Multi/Priority validation, and an invalid child outside the traversal limit. Live workflows exercise .NET Desktop 8.0.30, 9.0.20 and 10.0.11. Getter, converter, custom descriptor Name/GetValue, model write and opaque error-formatting counters verify passive observation. Edit conflict fingerprints now use weak cached path identities instead of the descriptor's virtual Name getter. Unsupported fingerprints cannot compare equal to authorize an edit. Existing temporary override/reset, source mapping and edit-lifetime cases passed.

The tests distinguish initial `Binding.DoNothing` from suppressing a later transfer, fallback from null replacement, current validation from historical notifications, and a composite parent's retained `UpdateTargetError` from recovered children and a successfully displayed target value. No parent status is rewritten as recovered. Source/compiled preview reinspection clears current errors on actual recovery while preserving historical evidence and exact expression IDs.

The 23 new Shell cases cover exact issue/child selection, unchanged property drafts, replacement rejection, old-host omissions and late replies across selection, pause, source and operation changes. An operation epoch suppresses stale binding explanations while retaining property/token updates needed by source-review conflict checks. Loaded UI validation exercises both real panels, exact navigation, visible cached steps, expanded details, scrolling and binding-error-free rendering. The [feature tour](docs/xaml-feature-tour.md) now contains eight actual loaded-app captures, copied unchanged from this final UI run; two show the shared path-step panels. These remain automated captures, not physical-input acceptance.

The local win-x64 package was refreshed (`binding-path-publish.log`). **18 overlapping packaged integration cases passed:** six source/compiled preview and declaration-navigation cases with `WPFSTUDIO_PREVIEW_HOST_UNDER_TEST` selecting the published host (`binding-path-packaged-preview.trx`), and 12 live diagnostic, declaration-navigation and binding override/reset cases with `WPFSTUDIO_INSPECTION_HOOK_UNDER_TEST` selecting the published hook (`binding-path-packaged-inspection.trx`). The live cases verify the loaded agent path. The package includes the new guide and all eight screenshots; source/capture/package image hashes match. Local documentation links and package document hashes were checked after copying this final validation record. No new portable executable startup timing was collected.

The [binding explanation guide](docs/xaml-binding-diagnostics.md) records supported observations and limitations. Continuous between-poll diagnosis, complete current null/conversion attribution, broader runtime compatibility and the [remaining full designer acceptance work](docs/xaml-devtools-design.md#full-goal-acceptance-ledger) are still outstanding. No new physical input or performance comparison was collected. The resource performance report below remains tied to its recorded earlier worker payload hashes.

## XAML DevTools: resource scalability and compiler metadata reuse (2026-09-28)

Ordinary XAML language requests now capture the current document/application dictionary dependency closure instead of reading every evaluated view. A separate bounded catalog retains all evaluated URI candidates, including unloaded duplicates. Physical reads are shared, while each logical import origin keeps its own relative base. Missing or ambiguous consulted inputs remain unknown. Batch analysis and rename keep their complete-scan requirements. Immutable per-snapshot resource fingerprints and weak compilation-keyed schema metadata eliminate repeated catalog hashing and assembly-attribute discovery without caching request overlays or cancellation.

The final Release solution build completed with zero warnings/errors (`resource-scale-cache-build.log`). **876 distinct affected tests passed:**

| Suite | Passed | Result in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Workspace | 400 | `resource-scale-workspace-full.trx` |
| Isolated SDK 8 compatibility | 1 | `resource-scale-sdk8-final.trx` |
| Shell | 474 | `resource-scale-shell-full.trx` |
| Loaded app UI | 1 | `resource-scale-ui-final.trx` |

The ordinary Workspace run's one opt-in skip passed separately under SDK 8.0.425. The 21 new Workspace cases comprise 14 index/discovery cases, three real-worker scale workflows, and four schema-cache cases. They cover unloaded duplicate identities, linked logical origins, inherited `xml:base`, bounded metadata and cancellation, unrelated versus consulted file locks, direct-key/reverse-merge precedence, unsaved dependency-only updates, same-length/same-timestamp disk changes, and retaining batch/rename refusal on incomplete scans. Metadata tests cover changed/generated/reference compilations and independent cancellation/concurrent callers. All 86 focused schema/resource checks passed in `resource-scale-cache-focused.trx`; they overlap the full-suite total.

The refreshed portable package passed **seven overlapping integration checks** with `WPFSTUDIO_TEST_WORKSPACE_HOST` explicitly selecting its `WorkspaceHost/WpfStudio.WorkspaceHost.dll` (`resource-scale-packaged-workspace.trx`). These include the three resource workflows, three scale workflows and qualified-binding regression. Final publication (`resource-scale-publish-final.log`) includes the documentation, benchmark summary and six unchanged feature-tour images with matching source hashes. Its measured worker payload hashes are unchanged.

The [performance report](docs/xaml-language-performance.md) compares identical sequential 20/200/600-file fixtures and 30 iterations using explicit prior/final packaged workers. The prior worker returned only incomplete resource results at 600 files. The final package returned **456 correct observations**: 366 RPC requests and 90 combined-cycle timings. Final warm completion p95 was **4.99 / 10.05 / 12.37 ms**; analysis p95 was **11.48 / 17.55 / 13.87 ms**. First completion still took **466–500 ms after workspace load**. At 600 files the incomplete prior answers are not a successful-result latency comparison. An initial closure-only implementation restored correctness but added repeated schema work; the final cache and package were measured separately after that finding.

Reports are `xaml-resource-scale-before.json`, `xaml-resource-scale-after.json` (intermediate), `xaml-resource-scale-cached.json` (final source output), and `xaml-resource-scale-after-packaged.json` under local `artifacts/performance`. [Tracked summary excerpts](docs/performance/xaml-resource-scale.json) contain machine details, correctness counts, all phase distributions, setup timings, report hashes and exact worker payload hashes. Tests and builds were stopped during measurements. The benchmark includes client serialization and transport but does not measure editor debounce, end-to-end typing latency, native input, concurrent workloads, arbitrary real-project scaling or Visual Studio performance. No new portable startup timing or physical-input validation was collected. Broader acceptance remains in [the full designer ledger](docs/xaml-devtools-design.md#full-goal-acceptance-ledger).

## XAML DevTools: project resource binding sources (2026-09-28)

Bindings can now use declared object types from evaluated external dictionaries and the owning application's resources. The same immutable resource context supplies completion, diagnostics, hover, definitions, spelling fixes, references and rename. Linked logical paths, nested relative imports and referenced-project component URIs retain their declaring XML namespaces and assembly identities. Unsaved dictionary changes invalidate consumers without changing their text. Current unsaved C# symbols remain authoritative.

The resolver checks direct keys before reverse-ordered merges. Missing or locked higher-priority inputs, runtime keys, unsupported providers, forward references, ambiguous types and uncertain scopes stay unknown. Imported structural types are checked against the actual framework symbols; matching type names alone are insufficient. Static aliases and supported Style.BasedOn declarations share bounded lookup and cycle guards. No application resource objects are constructed by semantic analysis.

Spelling fixes retain hashed/versioned prerequisites only for consulted resource files, including higher-priority dictionaries that had no matching key. A local design-context fix remains available when an unrelated dictionary is locked. Rename preserves complete scan guards and rebinds consumers against all proposed XAML texts. Shell tests cover open/closed prerequisite conflicts, no partial buffer edits, undo, stale replies, and releasing temporary dependency buffers without opening resource tabs. Disk checks are content comparisons before application, not continuous read leases; the existing external-file check-to-commit window remains.

The final Release solution build passed with zero warnings/errors (`resources-build-final2.log`). **855 distinct affected checks passed:**

| Suite | Passed | Result in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Workspace | 379 | `resources-workspace-full.trx` |
| Isolated SDK 8 compatibility | 1 | `resources-sdk8-final.trx` |
| Shell | 474 | `resources-shell-final.trx` |
| Loaded app UI | 1 | `resources-ui-final.trx` |

The ordinary Workspace run skipped the opt-in SDK 8 case; it passed separately under SDK 8.0.425. The 42 new Workspace checks comprise 38 semantic cases, three real-worker workflows and one evaluated metadata case. The 11 new Shell checks include real-worker editor lifecycle and dependency transaction cases. Those 11 were rerun successfully against the final worker (`resources-shell-focused-final2.trx`) after the framework-symbol identity change; they overlap the full Shell count. Initial focused reports also overlap these totals.

Real-worker tests change dictionary text without saving the consumer, close/discard overlays, update unsaved C# types, change same-length/same-timestamp files, lock/delete/recreate dictionaries, use linked resources in a referenced assembly, separate executable and library application scope, and retain rename prerequisites. Review caught and corrected invalid structural dictionary inference and skipped resource boundaries in BasedOn lookup. The initial build's duplicate primary-constructor capture warning was also removed.

The local win-x64 package was refreshed (`resources-publish.log`). Four overlapping real-worker integration checks passed with `WPFSTUDIO_TEST_WORKSPACE_HOST` explicitly selecting its `WorkspaceHost/WpfStudio.WorkspaceHost.dll`: the three resource workflows and the prior qualified-path regression (`resources-packaged-workspace.trx`). The package includes the feature tour and six images; source/package image hashes match. No new portable executable startup timing or physical-input validation was collected.

The [feature tour](docs/xaml-feature-tour.md) contains six actual automated loaded-app captures, copied unchanged into `docs/images/xaml/` for GitHub documentation. Captions distinguish observed UI from physical-input acceptance. README links and tour asset paths were checked. The new screenshots and tests do not establish physical input/IME/DPI behavior, large-workspace latency, arbitrary runtime resource attribution, or completion of the [full designer goal](docs/xaml-devtools-design.md#full-goal-acceptance-ledger).

## XAML DevTools: native Tab handoff and focus scrolling (2026-09-28)

Boundary Tab traversal now moves focus synchronously through the editor's native container. Each attachment and call has a checked identity; repeated or opposite-direction calls no longer share a latest-only heartbeat slot. The editor verifies the actual child process/window, parent, current focus and temporary sender property before traversing. The preview pane participates in its parent's tab order as a local navigation group. Unknown callback outcomes end native interaction instead of replaying traversal.

The host samples supported focused-control bounds separately from heartbeats. Observations carry attachment, geometry sequence, focus epoch/time, native focus and DPI. The viewport scrolls minimally on both axes, reveals the nearest edge of a fully offscreen oversized control, preserves an already visible portion, and does not undo newer manual scrolling. Animated transforms are withheld before value evaluation; a real custom-animation test verifies that observation does not call its evaluator. Separate popup sources and unsupported geometry remain unavailable.

The independent watchdog also bounds an unfinished handoff while a nested application dispatcher loop continues answering heartbeats. A real two-process regression enters that loop during focus loss, verifies the handoff-specific three-second failure, confirms the exact child exits and checks that the editor dispatcher remains usable. Other regressions cover duplicate/stale grants, rapid suppression during attachment/update, same-session removal/reinsertion, geometry before attachment acknowledgement and manual-scroll freshness.

The Release solution build completed with zero warnings/errors (`native-handoff-final-build.log`). **697 distinct affected-suite tests passed, with no failures or skips:**

| Suite | Passed | Result in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Preview | 189 | `native-handoff-preview-final.trx` |
| Shell | 463 | `native-handoff-shell-final.trx` |
| Native controls/integration | 44 | `native-handoff-view-final.trx` |
| Loaded app UI | 1 | `native-handoff-ui-final.trx` |

The local `artifacts/WpfStudio` package was refreshed (`native-handoff-publish.log`). **22 overlapping packaged checks passed** with `WPFSTUDIO_PREVIEW_HOST_UNDER_TEST` explicitly selecting that package's `PreviewHost/WpfStudio.PreviewHost.exe`: 19 host/lifecycle/focus cases in `native-handoff-packaged-preview.trx` and three actual native-pane cases in `native-handoff-packaged-view.trx`. The latter include real Tab traversal, focus scrolling and nested-focus watchdog recovery. The package also contains the interaction guide and the proposed resource-resolution plan.

Initial runs exposed the pane's missing tab-order grouping and a test destination whose owner window could restore focus into its only child. Both were corrected without weakening the actual-focus assertions. A focus-geometry check timed out during concurrent native suite execution and then passed independently; its diagnostics now retain the last complete observation, and focus-sensitive checks run in the serialized WPF collection. The full Preview run passed with that isolation. Earlier failed reports remain under `artifacts/TestResults/xaml-devtools` for comparison.

These tests use actual WPF focus APIs, native callbacks and owned processes. They do not establish physical keyboard/pointer fidelity, IME, visible popup routing, mixed-monitor DPI, docking or accessibility. No new portable executable startup measurement or physical-input validation was collected in this increment. Cross-process native parenting still couples input queues; the independent watchdog remains necessary. The full designer goal remains open in [the acceptance ledger](docs/xaml-devtools-design.md#full-goal-acceptance-ledger). The new [resource-resolution plan](docs/xaml-resource-resolution-plan.md) is proposed language-engine work, not implemented coverage.

## XAML DevTools: qualified binding paths and preview focus (2026-09-28)

The language engine now resolves supported parenthesized and owner-qualified binding segments, including `(Grid.Row)`, `(local:Provider.Value).Name`, nested paths and collection continuations. It uses lexical namespace scope and readable compiler metadata without executing the project. Completion preserves owner prefixes and exact entity-encoded replacement spans; hover and definitions distinguish the owner, accessor and continuation. A real worker regression verifies that unsaved C# changes to the attached property's declared type immediately change XAML diagnostics, suggestions and definitions. Unknown registrations, dynamic sources and unsupported paths remain unresolved. Qualified tokens carry explicit incomplete rename/reference coverage.

Native preview keyboard entry now uses a separate revocable local focus grant and a typed result. Delayed responses cannot continue Tab navigation after the entry was superseded; delayed boundary notifications require focus still inside the current native viewport. Arrow navigation no longer requests Tab traversal. Tests cover local input/focus changes, expiry, session/suppression changes, newer requests in the same direction, revoked grants in the real host and successful no-tab-stop responses without stopping the process.

The Release solution build completed with zero warnings/errors (`qualified-bindings-final-build.log`). **1,009 distinct affected-suite tests passed:**

| Suite | Passed | Result in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Workspace | 337 | `qualified-bindings-workspace-final.trx` |
| Isolated SDK 8 | 1 | `qualified-bindings-sdk8-final.trx` |
| Preview | 184 | `focus-grants-preview-final.trx` |
| Shell | 463 | `qualified-bindings-shell.trx` |
| Native controls/integration | 23 | `focus-grants-view-final.trx` |
| Loaded app UI | 1 | `qualified-bindings-ui-final.trx` |

The regular Workspace run's one opt-in skip passed separately under the isolated SDK 8 host. The 43 new language checks also passed in `qualified-bindings-focused-final.trx`; focused reports overlap the full suites. The first focused run caught an owner-completion insertion that dropped its namespace prefix; the final result includes that correction and default/entity-encoded owner cases. A later test-only change allows the same worker regression to select the packaged worker explicitly.

The local `artifacts/WpfStudio` package was refreshed (`qualified-bindings-publish.log`). **17 overlapping packaged checks passed:** 15 native host/lifecycle cases, one actual native-pane integration and the new qualified-binding worker case. The runs explicitly selected `PreviewHost/WpfStudio.PreviewHost.exe` and `WorkspaceHost/WpfStudio.WorkspaceHost.dll` from that package. Reports are `qualified-bindings-packaged-preview.trx`, `qualified-bindings-packaged-view.trx` and `qualified-bindings-packaged-workspace.trx`.

These tests invoke WPF controls, input-pipeline events and real process protocols; they do not establish physical keyboard/pointer fidelity. The Windows interaction driver failed to initialize even after a reset, so no new physical-input validation or portable executable startup timing was collected in this increment. Rapid boundary-Tab notification ordering, scrolling keyboard-focused controls into view, popup keyboard routing, IME, mixed DPI, docking and accessibility remain acceptance work. Focus-grant checks also do not make a cross-process check-and-focus operation atomic. See [interaction limits](docs/xaml-preview-interaction.md) and [the full ledger](docs/xaml-devtools-design.md#full-goal-acceptance-ledger).

## XAML DevTools: native preview interaction (2026-09-28)

**Interact / Inspect** now embeds the existing preview presentation source in an editor-owned native container. Source and compiled Window/Page/UserControl tests retain the same native surface, managed view and edited property state across attachment, detachment and capture. The actual compiled Window remains an ancestor. Inspect preserves the selected node/property and unapplied draft; source changes revoke interaction even when automatic refresh is paused. Native content uses actual size and physical-pixel scrolling, while bitmap zoom and layout overlays remain in Inspect mode. See [behavior and limitations](docs/xaml-preview-interaction.md).

The client and host verify pipe-peer process IDs, per-launch session IDs, per-render surface IDs, container ownership/lease properties, command sequences and matching PerMonitorV2 contexts. An independent background heartbeat bypasses both ordinary request gates; its process test blocks an application property callback and proves that only the pinned preview process ends while another preview remains usable. Tests also cover concurrent Abort/Stop, detach/re-entry, same-process source replacement, stale callbacks and container destruction during pending attachment. EOF starts an unconditional background exit deadline covering blocked dispatchers and cleanup callbacks.

The final Release solution build passed with zero warnings/errors (`native-interaction-final-build.log`). **653 affected-suite tests passed, with zero failures or skips in the final runs:**

| Suite | Passed | Result in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Preview | 181 | `native-interaction-preview-final.trx` |
| Shell | 463 | `native-interaction-shell.trx` |
| Native controls and real-host integration | 8 | `native-interaction-view-final2.trx` |
| Loaded app UI | 1 | `native-interaction-ui-final.trx` |

The native integration test uses a real PreviewClient, bound DesignerViewModel and NativePreviewPane on an owned offscreen PerMonitorV2 dispatcher. It checks actual child process/window ownership, a temporary Text override, Inspect capture and draft preservation, same-process/window re-entry, overlay suppression/resumption and source invalidation. These STA tests live in a separate test assembly because the loaded-shell test shuts down WPF's process-wide Application object. The final loaded app screenshot is `artifacts/screenshots/xaml-preview-snapshot.png`.

The win-x64 portable package was refreshed with the interaction guide (`native-interaction-publish.log`). **18 checks against its explicitly selected PreviewHost passed**: 17 host/client cases (`native-interaction-packaged-preview.trx`) and the real native-pane integration (`native-interaction-packaged-view.trx`). These overlap the suites above. Coverage includes process-image verification, source/compiled capture, native attachment/detachment, popup cleanup, sequence/lease rejection, watchdog recovery and both EOF hang paths. The legacy direct-process test cleanup now also pins its process handle and terminates only that process.

The packaged executable smoke passed with **1,979 ms** start-to-idle shell, **136 ms** graceful shutdown, exit code 0, an isolated saved profile and no remaining owned children. Measurement: `artifacts/performance/xaml-native-interaction-portable-smoke.json`. This is a warm-workstation functional sample, not a clean-machine or cold-start benchmark.

Early runs exposed and repaired a missing shadow-directory setup in the direct-host test fixture, native-parent checks performed before restoring top-level window styles, teardown paths that bypass `HwndHost.DestroyWindowCore`, and a late failed attach discarding a newer session's queued work. The local bridge may remain hidden after Window.Close for WPF reuse; its remote lease is revoked immediately and explicit disposal destroys the HWND. A view without keyboard tab stops returns traversal to the editor without terminating its preview.

The tests establish native lifetime and observation behavior, not natural input fidelity. Physical pointer/keyboard use, IME, visible popup positioning/capture, mixed-monitor DPI transitions, docking/floating and accessibility remain acceptance gates. The prior native probe measured approximately three-second editor focus stalls before watchdog recovery; cross-process native parenting still couples input queues. The broader designer goal is not complete.

## XAML DevTools: state-preserving snapshots and preview lifetime (2026-09-28)

**Update snapshot** captures the existing preview and rereads its selected properties without recreating the view. Tests cover retained process/control/DataContext/binding identities, source and compiled scenarios, temporary override/reset state, actual tree mutations, repaired binding failures, stale versions, disposal during dispatcher settling, newer property selection/drafts and disappearing selections. A loaded TreeView reset regression guards against transient null selection erasing the property draft. The loaded app screenshot is `artifacts/screenshots/xaml-preview-snapshot.png`.

The new binding-history regression found a genuine listener bug: WPF's first binding initialization could replace `DataBindingSource` when tracing had not been enabled, silently discarding the registered listener. Preview now opts in once with `PresentationTraceSources.Refresh()` before registration and retains the exact source instance for disposal. Tests use actual WPF failure/recovery and a fresh isolated process, without a debugger or injected trace messages. Historical traces are labeled warnings; current failures remain errors with node/property identities.

Stop now revokes active and queued requests before waiting for the transport gate. Subsequent renders wait for completed cleanup and use a fresh cancellation epoch. Tests exercise signaled hung callbacks, concurrent stops, immediate restart, canceled Stop and independent/descendant process survival. Cleanup uses the launched process's pinned handle and never recursively kills by parent PID.

The Release solution build passed with zero warnings/errors (`preview-state-final-build.log`). **623 affected-suite tests passed, zero failures or skips:**

| Suite | Passed | Result in `artifacts/TestResults/xaml-devtools` |
| --- | ---: | --- |
| Preview | 169 | `preview-state-preview-final.trx` |
| Shell | 453 | `preview-capture-shell.trx` |
| Loaded app UI | 1 | `preview-state-ui-final.trx` |

The portable package was refreshed (`preview-state-publish.log`). Six tests against its explicit packaged PreviewHost passed (`preview-state-packaged.trx`), covering source capture traces, ordinary/scenario compiled capture, cancellation/restart ordering and the process-image-verified render/edit/reset path. The portable executable smoke passed with **2,740 ms** start-to-idle shell, **242 ms** graceful shutdown, exit code 0, saved isolated profile and no remaining owned child processes. Measurement: `artifacts/performance/xaml-preview-state-portable-smoke.json`. This is a warm-workstation functional sample, not a clean-machine or cold-start benchmark.

The separate `tools/InteractivePreviewProbe` build also passed with zero warnings/errors. Both offscreen modes exited 0: a native child HwndSource and a reparented actual WPF Window. They verified pipe/ HWND ownership, matching PerMonitorV2 contexts at 96 DPI, preserved Window ancestry in the Window case, owned popup cleanup, exact child termination and parent-dispatcher recovery. The final probes measured **3,124 ms / 3,077 ms focus stalls** before the background watchdog stopped the hung child. Posted characters and Tab changed state; posted mouse messages did not activate the button. These observations do not certify physical input, IME, visible popups, mixed DPI or docking. Native interaction remains experimental and is not exposed in the app. Results: `artifacts/performance/xaml-native-preview-probe.json`; detailed logs: `artifacts/probes/interactive-preview/child-final.jsonl` and `window-final.jsonl`.

## XAML DevTools: exact binding declaration navigation (2026-09-28)

**Show binding XAML** now selects the actual binding declaration in source preview and live inspection. The selector includes root and composite child expressions, and live binding issues require the same expression identity before navigating. Actual `BindingExpressionBase` and `ParentBindingBase` identities are weakly tracked; repeated paths, target source tags and same-path code replacements never substitute for declaration evidence. Shared style/resource binding objects retain their own origin. Template clones without WPF binding-object metadata remain unavailable.

Source preview now loads the retained transformed XML tree with original positions and base URI. Namespace normalization preserves attribute annotations, generated nodes receive an out-of-document position, and design-only/external origins are withheld. Live navigation verifies module/resource/PDB identity, captured build bytes, a stable file lease and exact editor text. Both workflows revalidate the actual expression after asynchronous source checks. Final buffer identity/version guards reject activation callbacks that edit, replace, close or redirect the target editor. Binding selection preserves the property draft, and truncated declaration trees have a visible coverage notice.

| Check | Result | Coverage |
| --- | --- | --- |
| Core full suite | 183 passed | 34 new exact-span cases: repeated same-line paths, inline/object/style/resource/composite forms, namespace aliases, entities, UTF-16 positions, multiline/CRLF text, malformed markup and nearby/invalid hints |
| Preview full suite | 157 passed | Retained-reader probes, real source and compiled hosts, qualified attached attributes, shared styles, composite child identities, generated/design/template origins, stale/replaced/removed targets and bounded capture |
| Inspection full suite | 216 passed | Actual binding origins on .NET 8/9/10, shared declaration versus expression identity, no extra getter reads in the measured fixture, forged/stale/replaced/unloaded requests, limits and exact transport acknowledgements |
| Shell full suite | 443 passed | Composite selection spans, initial/final revalidation, draft preservation, unchanged polling, issue identity, newer user selections, source/property/scenario/pause/disconnect changes, buffer activation guards and explicit unavailable coverage |
| Runtime full suite | 17 passed | Existing launch/debugger/runtime regressions |
| Full loaded-window UI | 1 passed | Actual preview/live selectors and commands, issue navigation, highlighted binding text, draft/source preservation, both themes and existing editor/designer workflows |
| Packaged preview host | 5 passed | Exact process-image assertion plus real source/compiled binding declarations against the published executable |
| Packaged inspection agent | 8 passed | .NET 8/9/10 declaration cases and payload/lifetime regressions with the explicitly selected published agent path |
| Portable executable | Passed | First idle shell 1,756 ms, graceful shutdown 225 ms, exit code 0, isolated profile saved and no owned children left |
| Smoke process ownership | 26 assertions passed | Reused parent/child PIDs, immediate-parent creation ordering, microsecond precision, missing identity, pinned handles and nonrecursive termination |

The distinct affected-suite total is **1,017 passed**, with no failures or skips in the final runs. Reports under `artifacts/TestResults/xaml-devtools` are `binding-source-core-final.trx`, `binding-source-preview-final2.trx`, `binding-source-inspection-final.trx`, `binding-source-shell-final2.trx`, `binding-source-runtime-final.trx` and `binding-source-ui-final.trx`. Focused/probe runs overlap these suites. The final Release solution build completed with zero warnings and errors (`binding-source-final-build.log`).

The loader probe exposed shifted positions in serialized preview markup and missing binding-origin metadata in instantiated templates. Review repaired generated-node position inheritance, quadratic origin traversal and a race where a delayed issue inspection could override a newer declaration selection. Enabling XAML debug information on the compiled preview test fixture required a targeted rebuild: WPF's incremental BAML cache retained its earlier metadata until regenerated. The final preview run covers that rebuilt fixture.

Package reports are `binding-source-packaged-preview.trx` and `binding-source-packaged-inspection.trx`; these overlap the suite tests. The portable smoke script's first run exposed an existing process-ownership bug: a reused parent PID caused a pre-existing Discord process to be misidentified and terminated by cleanup. Discord was reopened. The script now pins actual process handles, verifies creation-time ordering and exact CIM/handle identity at microsecond precision, rejects stale parent IDs, checks the HWND owner before close and never requests unverified process-tree termination.

The corrected smoke run passed; its workstation sample is `artifacts/performance/xaml-binding-source-portable-smoke.json`. Caches may be warm, and empty-shell timing does not measure designer interaction or large-project analysis. The side-effect-free ownership regression is `tools/Test-SmokeProcessOwnership.ps1`, with output in `binding-source-smoke-ownership.log`. The refreshed win-x64 package is `artifacts/WpfStudio`, including the binding-navigation guide and this validation record.

Screenshots `xaml-preview-binding-source.png`, `xaml-preview-binding-source-light.png`, `live-xaml-binding-source.png` and `live-xaml-binding-source-light.png` are under `artifacts/screenshots`. See [usage and remaining limits](docs/xaml-binding-navigation.md). Compiled-preview source verification, binding origins for template clones, classless dictionary mappings and winning resource/trigger attribution remain acceptance work; the broader XAML DevTools goal is not complete.

## XAML DevTools: property appearance and resource evidence (2026-09-28)

Preview and running-app inspection now share an **Appearance** tab. It shows the selected dependency property's effective value (complex values by type), current WPF precedence category and flags, relevant Style/BasedOn/template setter and trigger declarations, available resource-dictionary keys, current local DynamicResource keys, and matching historical StaticResource resolutions. Candidate declarations, current state and historical evidence are presented separately. Source positions remain unverified hints; this increment does not authorize new navigation or shared-style/resource edits.

The shared reader runs on the owning dispatcher with bounded traversal, declarations, dictionary sizes, text and capture work. It skips oversized dictionaries before copying keys, avoids dictionary/setter value evaluation and custom formatting/ambient/type-metadata callbacks, and records resource notifications with bounded weak associations. Reading the selected effective value still uses WPF's normal dependency-property system. Live reads require an opaque property identity observed on the current element/tree revision, including read-only and noneditable properties. Preview reads reject obsolete renders and nodes. Both panels reject late replies and preserve unapplied drafts; live pause/disconnect and source/scenario/selection changes clear stale details.

| Check | Result | Coverage |
| --- | --- | --- |
| Preview full suite | 147 passed | 12 new cases: real source/compiled hosts, Style/BasedOn/trigger declarations, merged/static/dynamic resources, deferred setter/resource constructors and custom formatting counters, stale renders, key limits, custom ambient/type callbacks and nested control-template targets |
| Inspection full suite | 192 passed | 14 new cases: actual .NET 8/9/10 applications on multiple dispatchers, read-only/complex identities, historical resources, deferred construction, forged/stale/unobserved/unloaded targets, weak cleanup, exact transport acknowledgements and older-agent/cancellation behavior |
| Shell full suite | 420 passed | 18 new cases: on-demand loading, draft preservation, current property metadata, late responses, source/scenario/session/pause/edit transitions, capability gating and separate current/candidate/history presentation |
| Runtime full suite | 17 passed | Existing launch/debugger/runtime regressions |
| Full loaded-window UI | 1 passed | Actual preview and live Appearance controls, rapid property selection during an outstanding read, source/draft preservation, resource history, both themes, selected-header visibility, property-row space and existing application workflows |
| Packaged preview host | 3 passed | Real process-image path assertions and source/compiled appearance cases against the published executable |
| Packaged inspection agent | 7 passed | .NET 8/9/10 appearance plus early diagnostics/collectible-agent lifetime and large-payload cases against the explicitly selected published agent |
| Packaged-host loaded-window UI | 1 passed | Appearance workflow with explicitly selected published preview/workspace hosts; overlaps the full UI case |
| Portable executable | Passed | First idle shell 2,165 ms, graceful shutdown 143 ms, exit code 0 and isolated profile saved |

The distinct affected-suite total is **777 passed**, with no failures or skips in the final runs. Reports under `artifacts/TestResults/xaml-devtools` are `appearance-preview-final.trx`, `appearance-inspection-final.trx`, `appearance-shell-final.trx`, `appearance-runtime-final.trx` and `appearance-ui-final.trx`. Package reports are `appearance-packaged-preview.trx`, `appearance-packaged-inspection.trx` and `appearance-packaged-ui.trx`; focused and earlier UI reports overlap these tests. The final Release solution build completed with zero warnings and errors.

Review repaired preview cancellation that could stop an otherwise current host, omitted outer-template setters on child controls, unbounded property metadata and virtual custom-Type access. UI validation caught a wrapped tab strip reducing property rows below the existing minimum; the final strip scrolls horizontally and reveals the selected header. The full UI run asserts at least 100 px for property rows and a fully visible selected Appearance header. Screenshots `xaml-preview-appearance.png`, `xaml-preview-appearance-light.png`, `xaml-preview-appearance-resources.png`, `live-xaml-appearance.png` and `live-xaml-appearance-light.png` are under `artifacts/screenshots`.

The refreshed win-x64 package is `artifacts/WpfStudio`, including [appearance usage and limits](docs/xaml-appearance.md). Startup measurements are in `artifacts/performance/xaml-appearance-portable-smoke.json`; caches may be warm and these timings do not establish large-project or typing-to-diagnostic latency. Complete resource lookup reconstruction, active/winning setter attribution, verified style/resource navigation and full framework/template coverage remain acceptance work. The language engine remains modular inside the existing workspace worker. The full product goal remains active in `docs/xaml-devtools-design.md`.

## XAML DevTools: preview scenarios, design values and type context (2026-09-28)

The designer now reads named scenarios from a project-local `wpfstudio.preview.json`. Explicit public static factories supply data in source or compiled mode and compose views with constructor dependencies in compiled mode. Selection requires Refresh; each scenario runs in a fresh isolated host. Accepted results identify the selected scenario and loaded build. Configuration fingerprints, editor revisions and linked-project identity changes invalidate stale previews and pending source edits. The engine remains in the existing modular workspace/preview processes; this increment does not introduce a separate language-server deployment.

Source preview applies supported design-time literals and property elements, including explicit data objects and collection samples, with a toggle and source-located unsupported-declaration diagnostics. Property metadata reports the actual WPF value source alongside an authored design baseline, allowing for application callbacks that subsequently replace the value. Design baselines cannot be written back as runtime XAML. The language service now uses the collection shape for `DesignInstance.CreateList`, resolves supported design DataContext object property elements, and excludes ignored design-only binding expressions from speculative runtime diagnostics and navigation. Declarative DesignInstance/DesignData objects are not yet instantiated by the preview.

| Check | Result | Coverage |
| --- | --- | --- |
| Core full suite | 149 passed | 22 new catalog cases: schema/field/name/path limits, linked paths, comments/BOM, cancellation, no assembly loading, reload/deletion and byte fingerprints despite unchanged timestamps |
| Preview full suite | 135 passed | 54 new scenario/design cases: actual factory execution, constructor services, resource identity, null data, inherited/local contexts, source maps, invalid/async/wrong-dispatcher factories, fresh processes, cancellation/timeouts, temporary edit/reset, design baselines, custom namespaces and the bundled CounterApp's three scenarios in both modes |
| Workspace full suite | 294 passed | 30 new design-context cases: collection/member/item completion and diagnostics, direct design DataContexts, source navigation, conflicting/unknown boundaries and ignored-content behavior |
| Isolated SDK 8 check | 1 passed | Actual SDK 8.0.425 WPF/Toolkit build and generated-member language service; the regular suite's opt-in skip is exercised separately |
| Shell full suite | 402 passed | 16 new scenario and context cases: explicit Refresh, configuration changes/reload, same-time fingerprints, late replies, provenance, linked-project/framework selection, immediate invalidation and pending source-review rejection |
| Full loaded-window UI | 1 passed | Actual scenario combo, design toggle and Refresh command; four rendered data states, compiled factory view, source preservation, both themes and existing application workflows |
| Focused final layout UI | 1 passed | Collapsed settings, header at most 120 px, property grid at least 100 px with visible rows, preview states and both themes; overlaps the full UI case |
| Published preview host | 54 passed + 1 UI passed | Explicit absolute packaged host path, with exact process-image assertions in CounterApp cases; overlaps the full suites |
| Portable executable | Passed | First idle shell 2,343 ms, graceful shutdown 186 ms, exit code 0 and isolated profile saved |

The distinct affected-suite total is **982 passed**, with no failures in the final runs. Reports under `artifacts/TestResults/xaml-devtools` are `scenarios-core-final.trx`, `scenarios-preview-repaired.trx`, `scenarios-workspace-final.trx`, `scenarios-sdk8-final.trx`, `scenarios-shell-repaired.trx` and `scenarios-ui-full.trx`. Final UI layout is covered by `scenarios-ui-layout.trx`; package checks are `scenarios-packaged-preview.trx` and `scenarios-packaged-ui.trx`. Focused/earlier reports overlap these results. The final Release solution build completed with zero warnings and errors. The later layout change was XAML-only and was verified in the focused source and packaged-host UI runs; other production code remained unchanged after its passing full suites.

Validation found and repaired a CLR namespace normalization defect: rewriting an `xmlns` assembly URI without rewriting element/qualified-attribute names broke custom source controls. It also found a design-only binding expression leaking an ancestor-type warning. Review added immediate preview shutdown when the linked editor changes owning project/framework, accurate inherited scenario attribution and Loaded-handler regressions for replaced design values. Test repairs normalized linked fixture paths, explicitly omitted CounterApp's nonexistent compiled App.xaml resource, and awaited the designer-opening command instead of starting a second unobserved refresh. Empty application resources still require an explicitly blank App resources setting; missing configured resources are not silently ignored.

Visual inspection found that always-expanded settings left too little space for property rows. The final pane keeps rendering/scenario controls and active state visible, moves detailed settings into a collapsed area and exposes full value-source tooltips. Screenshots `artifacts/screenshots/xaml-preview-scenarios.png`, `xaml-preview-scenarios-light.png`, `xaml-design-time-values.png` and `xaml-preview-factory.png` were inspected. The refreshed win-x64 package is `artifacts/WpfStudio`, including the sample catalog/factories and `docs/xaml-preview-scenarios.md`.

The published-host UI sample rendered four fresh-process scenarios in 1,005–1,218 ms each. Raw measurements are `artifacts/performance/xaml-preview-scenarios.json` and `xaml-scenarios-portable-smoke.json`. These workstation samples may use warm caches and do not establish large-project or typing-to-diagnostic latency. Preview remains .NET 10 on Windows; broader runtime/architecture fidelity, native input, complete resource/style attribution, declarative sample construction and Hot Reload remain acceptance work. The full product goal remains active in `docs/xaml-devtools-design.md`.

## XAML DevTools: formatting and structural typing (2026-09-28)

**Ctrl+Shift+F** now formats XAML as well as C#, preserves the editor selection and unsaved changes, and creates one workspace undo. The formatter edits source whitespace without serializing XML: quoted values, entities, comments, inline text and `xml:space` content stay intact. Compiler metadata supplies custom content-property and collection semantics. Typing assistance closes tags, pairs attribute quotes, completes closing tags and indents between matching tags, including completion punctuation replay and normal editor undo/redo. The final Release solution build completed with zero warnings and errors.

| Check | Result | Coverage |
| --- | --- | --- |
| Core full suite | 127 passed | Linear copying for large edit batches, stable same-offset edit ordering and invalid/overflowing edit rejection; existing transaction/source-edit workflows |
| Workspace full suite | 264 passed | 25 new formatting cases covering lexical preservation, content policies, compiler authority, actual custom WPF content metadata and unsaved changes, idempotence, budgets, unavailable contexts and failed worker loading |
| Isolated SDK 8 check | 1 passed | Actual SDK 8.0.425 WPF/Toolkit build and generated-member language service; the regular suite's opt-in skip is exercised separately |
| Shell full suite | 386 passed | 81 new formatting/typing cases: selection direction, shared undo, stale source/context/disk guards, unsaved C# metadata synchronization, C# formatting, encoded markup-extension characters and real AvalonEdit input/undo/redo |
| Full loaded-window UI | 1 passed | Bound Ctrl+Shift+F command, unchanged disk bytes, selection/undo, actual WPF object serialization before/after formatting, compiler-resolved custom content, both themes and existing application workflows |
| Published workspace worker | 1 passed | Focused editing UI with the absolute published worker selected and a custom content collection requiring project metadata; overlaps the full UI case |
| Portable executable | Passed | First idle shell 2,271 ms, graceful shutdown 135 ms, exit code 0 and isolated profile saved |

The distinct total is **779 passed**, with no failures in the final runs. Reports under `artifacts/TestResults/xaml-devtools` are `editing-core-final.trx`, `editing-workspace-repaired.trx`, `editing-shell-repaired.trx`, `editing-sdk8-final.trx` and `editing-ui-full.trx`. Earlier focused/full reports overlap these results. The last rebuild changed only the failed-load regression and test cleanup helper; production code was unchanged after the passing full UI and SDK 8 runs. `editing-packaged-ui-final.trx` explicitly selects `artifacts/WpfStudio/WorkspaceHost/WpfStudio.WorkspaceHost.dll` through `WPFSTUDIO_TEST_WORKSPACE_HOST`, verifies a connected worker process and formats a compiler-resolved custom collection.

Initial focused checks exposed an incomplete namespace-prefix exception and incorrect redo caret restoration; both were fixed, along with one incorrect expected quote offset in a test. Review added entity-aware markup-extension quote handling and closed a workspace-load interval in which incomplete ownership could look like standalone content. Formatting now waits for completed inventory and rejects unavailable explicit project contexts. A later full run exposed two fixture issues: an in-process regression attempted to load worker-only MSBuild assemblies, and immediate temp-directory cleanup raced worker termination. The regression now runs through the real worker; cleanup retries transient I/O failures for a bounded 3.15 seconds and still fails on persistent errors. Both repaired full suites passed.

The refreshed win-x64 package is `artifacts/WpfStudio`. Screenshots `artifacts/screenshots/xaml-formatting.png` and `xaml-formatting-light.png` were visually inspected. The published-worker UI sample took 39 ms for the small unowned buffer and 607 ms for the custom project content after workspace load. Raw data is `artifacts/performance/xaml-formatting.json`; startup data is `artifacts/performance/xaml-editing-portable-smoke.json`. These small workstation samples may use warm caches and do not establish large-solution or typing-to-diagnostic latency.

Standalone formatting uses conservative known-framework content rules. When known project metadata is unavailable, local fallback preserves content spacing, while unavailable/ambiguous owning contexts can withhold worker formatting. Unknown or opaque custom content, meaningful text, malformed XML and bounded-analysis failures remain conservative. Format-selection, linked tag-name editing, structural move/wrap commands and broader markup-extension coverage remain future work. This increment does not establish full Visual Studio parity; the broader product goal remains active in `docs/xaml-devtools-design.md`.

## XAML DevTools: references and rename across XAML and C# (2026-09-28)

**Shift+F12** now finds compiler-resolved binding-property and event-handler references in C# and evaluated XAML, including closed files. **F2** reviews the combined C#/XAML rename, preserves unsaved work and applies one workspace undo without saving. Queries work from either language, distinguish unrelated same-named members and carry checked source locations/project contexts. The Release solution build completed with zero warnings and errors.

| Check | Result | Coverage |
| --- | --- | --- |
| Workspace full suite | 239 passed | 46 new symbol extraction/integration cases: scoped property paths, exact/entity spans, selected event overloads, linked/referenced projects, interface/override families, source overlays, coverage bounds, compiler conflicts and implicit disk-refresh notifications |
| Isolated SDK 8 check | 1 passed | Actual SDK 8.0.425 WPF/Toolkit build and generated-member language service; the regular suite's opt-in skip is exercised separately |
| Shell full suite | 305 passed | 22 new reference/rename cases: preview/cancel/apply, shared undo, unsaved edits, version/hash/disk/read-only/context guards, closed-file cache release, linked navigation and immediate cross-editor semantic invalidation |
| Full loaded-window UI | 1 passed | Real references, three-file binding rename and two-file event rename, cancel/apply/undo, preserved unsaved text and disk bytes, immediate F12 and both themes, plus existing application workflows |
| Published workspace worker | 1 passed | Focused symbol UI workflow with the absolute published worker path selected; overlaps the full UI case |
| Portable executable | Passed | First idle shell 2,130 ms, graceful shutdown 134 ms, exit code 0 and isolated profile saved |

The distinct total is **546 passed**, with no failures in the final runs. Reports under `artifacts/TestResults/xaml-devtools` are `symbols-workspace-final.trx`, `symbols-sdk8-final.trx`, `symbols-shell-reviewed.trx` and `symbols-ui-final.trx`. `symbols-packaged-ui-final.trx` explicitly selects `artifacts/WpfStudio/WorkspaceHost/WpfStudio.WorkspaceHost.dll` through `WPFSTUDIO_TEST_WORKSPACE_HOST`. Earlier focused/full reports overlap these results. The last rebuild changed only the watcher test; production code was unchanged after its passing Workspace/UI runs.

The workspace integration theory renames both a binding property and an event handler, then builds actual WPF projects targeting .NET 8, 9 and 10. Review caught a semantic hazard beyond edited references: renaming a derived property could capture an untouched base-property binding. Rename now re-resolves every previously resolved occurrence, including untouched files, and rejects changed identity. A separate regression verifies that an internal closed-C# refresh notifies editors even when rename subsequently rejects a compiler conflict. The shell settles its explicit refresh before capturing the query context.

Initial checks repaired two assertions tied to Roslyn's edit chunking and one incorrect diagnostic ID in a new fixture. A full Shell run also exposed an existing file-watcher test race: duplicate notifications for one legitimate source edit arrived after the test cleared its queue. The corrected test retains event history and still rejects every generated-output, ordinary-output or reload notification. Its final full suite passed; production watcher behavior was unchanged.

The refreshed win-x64 package is `artifacts/WpfStudio`. Screenshots `artifacts/screenshots/xaml-symbol-references.png`, `xaml-symbol-rename-review.png` and `xaml-symbol-rename-review-light.png` were visually inspected. The packaged-worker sample took 1,247 ms to find references across C# and two XAML files after workspace load; raw data is `artifacts/performance/xaml-symbol-references.json`. Portable timing is `artifacts/performance/xaml-symbols-portable-smoke.json`. These small workstation samples may use warm caches and do not establish large-solution latency.

Rename covers verified binding property segments and event-handler values. Static design-type assumptions and unresolved runtime paths remain explicit coverage notes. XAML type/namescope refactoring, generated names, arbitrary markup extensions and reflection/string references require further work. Scan limits, malformed or unavailable source and conflicting linked meanings withhold rename. Every scanned XAML file remains a hashed prerequisite during review, even if it has no edits. The broader product goal remains active in `docs/xaml-devtools-design.md`.

## XAML DevTools: event-handler language assistance and generation (2026-09-28)

XAML event values now offer compatible C# instance methods, live missing/incompatible/ambiguous-handler errors, hover and overload-specific F12 navigation. **Create event handler** inserts a compiler-checked method into authored code-behind and fills an empty event value with an available name. The action preserves unsaved source, opens the method, refreshes the language service and uses one workspace undo without saving files. The final Release solution build completed with zero warnings and errors.

| Check | Result | Coverage |
| --- | --- | --- |
| Core full suite | 124 passed | No-op XAML prerequisites retain source/disk guards without becoming undo dependencies; existing transactions, newly created empty files and source-edit workflows |
| Workspace full suite | 193 passed | 37 event semantic cases and 16 generation cases, including overloads, keywords, accessibility, variance, generic inference, ref signatures, EventSetter, attached/routed events, incomplete C# types, budgets and existing language workflows |
| Isolated SDK 8 check | 1 passed | Actual SDK 8.0.425 WPF/Toolkit generation and language service; this opt-in case is skipped in the regular suite and exercised separately |
| Shell full suite | 283 passed | 14 cross-file event-action cases plus existing designer/inspection workflows; version/hash/disk/read-only/selection/context guards, code-behind activation, immediate F12 and shared undo |
| Full loaded-window UI | 1 passed | Existing application workflows plus handler completion, hover/F12, actual bound quick-fix menu execution, diagnostics recovery, unchanged disk bytes and undo; both themes |
| Published workspace worker | 1 passed | Focused event UI workflow with the absolute published worker path explicitly selected; overlaps the UI case |
| Portable executable | Passed | First idle shell 2,041 ms, graceful shutdown 142 ms, exit code 0 and isolated profile saved |

The distinct full-suite total, including the separately exercised SDK 8 case, is **602 passed**, with no failures. Reports under `artifacts/TestResults/xaml-devtools` are `events-core-final.trx`, `events-workspace-reviewed.trx`, `events-sdk8-final.trx`, `events-shell-final.trx` and `events-ui-full.trx`. Earlier focused runs overlap these suites. The final `events-ui-reviewed.trx` rerun covers the last naming correction and improved screenshot timing; the Core/Shell implementation was unchanged after its full passing runs. `events-packaged-ui-final.trx` uses `WPFSTUDIO_TEST_WORKSPACE_HOST` pointing explicitly at `artifacts/WpfStudio/WorkspaceHost/WpfStudio.WorkspaceHost.dll`.

The real worker generation theory builds actual WPF projects targeting .NET 8, 9 and 10 twice each: named-handler creation shared by ordinary, owner-qualified routed, attached and EventSetter wiring, then empty-handler naming with fields introduced by unsaved XAML. It verifies namespaced/entity-encoded `x:Name`, unqualified `Name`, ignored design names, unsaved C# preservation, version/hash prerequisites and collision rejection. These target builds use the normal installed SDK; the separate isolated SDK 8 check verifies the older SDK itself.

Initial focused runs caught incorrect identifier validation and a synthetic compiler-probe statement error; both were repaired before the passing runs. Final review also caught a possible collision with fields introduced by unsaved XAML. Generation now reserves current authored names before choosing or accepting a method name. Names from nested scopes are conservatively reserved too. The UI fixture awaits the actual bound asynchronous command and reacquires current menu actions after selection/semantic refreshes.

The refreshed win-x64 package is `artifacts/WpfStudio`. Screenshots `artifacts/screenshots/xaml-event-diagnostics.png`, `xaml-event-actions.png`, `xaml-event-generated.png` and `xaml-event-generated-light.png` were visually inspected. The published-worker completion sample was 73 ms after workspace load and initial analysis; raw data is in `artifacts/performance/xaml-event-completion.json`. Portable timing is in `artifacts/performance/xaml-events-portable-smoke.json`. These are small workstation samples with potentially warm caches, not cold-start or large-workspace latency guarantees.

Generation requires an identifiable existing writable C# partial-class source file. Generated/ambiguous/linked destinations, existing conflicting members and unsupported signatures withhold the action. Missing or malformed C# declaration/type information remains unverified instead of producing a guessed incompatible-handler error. Analysis bounds authored events and compiler probes separately. Inline `x:Code`, `x:Subclass`, broader language/event forms and file-local extension-method behavior need further support. This increment does not establish full Visual Studio parity; the broader goal remains active in `docs/xaml-devtools-design.md`.

## XAML DevTools: shared layout inspection and overlays (2026-09-28)

The XAML Designer and Live XAML panes now share a **Layout** tab with cached desired/render/actual sizes, parent layout slot, dimensions, margins, alignment, padding/border values, transforms, visibility, DPI and clipping observations. A shared .NET 8 WPF diagnostics library supplies both the isolated preview and live .NET 8–10 agent. Toggleable overlays distinguish the slot, render box, supported margin outline and approximate cached clip bounds. The final Release solution build completed with zero warnings and errors.

| Check | Result | Coverage |
| --- | --- | --- |
| Preview full suite | 81 passed | Passive capture, distinct coordinate frames, RTL/transforms, padding/borders, cached clips, pending/hidden/collapsed layout, bounded unsupported cases, no virtual layout-clip/custom CLR getter invocation, strict finite JSON, real host RPC and existing preview/edit workflows |
| Inspection full suite | 178 passed | Actual adorner polygon coordinates across .NET 8/9/10, separate UI dispatchers and popups; normal-highlight compatibility, removal, picking, detach and abrupt EOF cleanup; forced-GC/lazy-library lifetime regression and existing runtime inspection/edit workflows |
| Shell full suite | 269 passed | Layout capabilities/toggles, late successful/failed replies, selection/source/session invalidation, unavailable state, pause/resume cleanup with auto-refresh off and existing language/designer/live workflows |
| Full loaded-window UI | 1 passed | Actual Layout tabs and overlay checkboxes, preview render-pixel changes, live layout facts, both themes and existing project diagnostics/source-edit workflows |
| Explicit published hosts | 8 passed | One preview RPC case and seven live layout/lifetime cases using absolute published executable/startup-hook paths; these overlap the full suites |
| Portable executable | Passed | First idle shell 2,195 ms, graceful shutdown 145 ms, exit code 0 and isolated profile saved |

The distinct full-suite total is **529 passed**, with no failures or skips. Reports under `artifacts/TestResults/xaml-devtools` are `layout-preview-first.trx`, `layout-shell-first.trx`, `layout-inspection-final.trx` and `layout-ui-final.trx`. The preview and Shell sources were unchanged after their passing runs. `layout-inspection-focused.trx` and `layout-packaged-inspection.trx` each contain the same seven focused cases; `layout-packaged-preview.trx` contains the single preview case. The earlier UI pass overlaps the final UI run.

The first runtime run exposed two repaired issues. The geometry-observation fixture assumed flattened paths always had a non-null transform; it now handles the identity case. Large property payloads also exposed premature finalization of the agent's collectible load context before the new diagnostics library was loaded. Startup transfers strong context ownership to the asynchronous session, which retains it through cleanup and then explicitly unloads it. The existing .NET 8/9/10 payload theory now forces target-side GC before first inspection, verifies the diagnostics assembly is still cold and no unload has started, verifies lazy inspection succeeds, and observes a single unload notification after disconnect. This confirms initiation of unloading, not complete collection of every framework cache.

The refreshed win-x64 package is `artifacts/WpfStudio`. Published-host tests explicitly check the launched preview executable and loaded agent paths and exercise the new shared library through real RPC. Raw startup measurements are in `artifacts/performance/xaml-layout-portable-smoke.json`; these are workstation samples with potentially warm caches, not cold-start or large-application latency guarantees. Screenshots `artifacts/screenshots/xaml-designer-layout-details.png`, `xaml-designer-layout-details-light.png`, `live-xaml-layout.png` and `live-xaml-layout-light.png` were visually inspected.

Facts and outlines are last-observed snapshots; refresh after layout changes. Capture never forces measure/arrange. It withholds unsupported, pending, collapsed or detached geometry and bounds ancestor/transform/clip traversal. Clip outlines approximate bounds; original measure constraints, complete clip/effect/occlusion regions and continuous layout-event tracing remain unavailable. Live outlines require an adorner layer. These checks do not establish full native-input/DPI fidelity or superiority to Visual Studio; the broader goal remains active in `docs/xaml-devtools-design.md`.

## XAML DevTools: project-wide language diagnostics (2026-09-28)

Evaluated project XAML is now checked in the background even when its files are closed. Linked files receive separate results for each owning project; the editor's Project selector controls completion, diagnostics, hover, definitions and fixes. Closed C# changes refresh the Roslyn solution, while open unsaved buffers retain precedence. The Release solution build completed with zero warnings and errors.

| Check | Result | Coverage |
| --- | --- | --- |
| Workspace full suite | 140 passed | Closed/linked XAML contexts, open overlays, closed C# refresh, referenced-project uncertainty, missing/locked source recovery, budgets/cancellation, bounded diagnostic construction and existing language/compiler workflows |
| Isolated SDK 8 check | 1 passed | Actual SDK 8.0.425 WPF/Toolkit build, generated members, diagnostics and completion through the .NET 10 worker; this check is opt-in and skipped in the regular suite |
| Shell full suite | 260 passed | Serial/debounced scans, stale results, linked/configuration/directory watchers, bounded diagnostic navigation, explicit project context, stale completion/fix rejection, healthy-project availability beside locked model sources, build-diagnostic preservation and existing designer/inspection workflows |
| Full loaded-window UI | 1 passed | Closed linked-file issue discovery, exact project selection on navigation, actual Project ComboBox, automatic diagnostics recovery after a closed model edit, both themes and existing designer/live-source-edit workflows |
| Packaged workspace worker | 1 passed | The focused project-analysis UI workflow with the absolute published worker path selected explicitly; overlaps the full UI case |
| Portable executable | Passed | First idle shell 2,693 ms, graceful shutdown 158 ms, exit code 0 and isolated profile saved |

The distinct total is **402 passed**, with no failures; the regular suite's one opt-in skip was exercised separately. Reports under `artifacts/TestResults/xaml-devtools` are `project-xaml-workspace-final.trx`, `project-xaml-sdk8-final.trx`, `project-xaml-shell-review-recovered.trx` and `project-xaml-ui-final.trx`. The later focused UI rerun, `project-xaml-ui-review-recovered.trx`, also passed after the availability correction; it overlaps the full UI case. Earlier focused runs overlap these suites. Initial Shell failures were Windows path-separator mismatches in fixture expectations; normalized expectations passed the final full run. The actual loaded UI screenshot is `artifacts/screenshots/xaml-project-diagnostics.png` and was visually inspected.

Final review found and corrected an availability issue: a locked model in one project could keep every editor in a global pending state even after disk reconciliation finished. A real two-project regression now confirms a watched change restores the healthy project's new completion member while the locked project's batch and on-demand analysis remain unavailable. One subsequent parallel MSBuild node exited unexpectedly; the final solution build with two nodes passed without warnings or errors. Interrupted test runs with missing/unreadable reports were not counted; their replacement runs are the `review-recovered` reports above.

The refreshed win-x64 package is under `artifacts/WpfStudio`. `project-xaml-packaged-ui-final.trx` sets `WPFSTUDIO_TEST_WORKSPACE_HOST` to its absolute `WorkspaceHost/WpfStudio.WorkspaceHost.dll` path; the test's client uses that explicit path. The two-project linked-file sample recorded a 507 ms explicit scan after workspace load and 673 ms from an external C# model edit to refreshed diagnostics. Raw measurements are `artifacts/performance/xaml-project-analysis.json` and `xaml-project-diagnostics-portable-smoke.json`. These are small workstation samples with potentially warm compilation/file caches, not cold-start or large-workspace latency guarantees.

Project diagnostics retain project identity and analyzed-text hashes. Navigation rechecks bounded current text and selects the corresponding editor context. Context changes invalidate pending completion, hover, definition and fix requests; a final edit transaction guard prevents an accepted old fix from modifying a changed context. Build diagnostics remain a separate input to the Problems view.

Scans have explicit limits: 512 XAML file/project contexts, 1,000,000 characters per file, 8,000,000 aggregate characters and 2,000 diagnostics. Closed C# refresh uses bounded reads and fair continuation across the known inventory. Missing or unreadable model source withholds affected semantic assistance, including transitive consumers, instead of treating old types as current. Partial coverage and unavailable files remain visible. Watchers cover known local/linked files and standard ancestor configuration files, with periodic reconciliation of known sources. Detected item/configuration changes and persistent known-file deletion require workspace reload; periodic reconciliation does not promise discovery of every missed project-membership change. The engine itself can recover a known deleted/recreated DocumentId, but the Shell conservatively retains its reload requirement after detected membership loss.

These checks do not establish unlimited workspace scalability, automatic project reevaluation, disambiguation of multiple target frameworks sharing one project path, or the actual runtime DataContext. The broader language/designer goal remains active in `docs/xaml-devtools-design.md`.

## XAML DevTools: reviewed live source edits (2026-09-28)

Live XAML can now prepare a local authored property edit from the inspected value/draft, show the exact source diff, and apply it to the editor buffer with workspace undo. Literal empty text, explicit XAML null and local removal are distinct actions. Runtime property validation and authored-type matching are independent of temporary override support. The Release solution build completed with zero warnings and errors; a later caption-only App/UI rebuild was also clean.

| Check | Result | Coverage |
| --- | --- | --- |
| Core suite | 121 passed | Existing lexical source edits, explicit null with scoped namespace aliases, exact element locations, and final transaction guards after asynchronous disk checks |
| Shell suite | 237 passed | Source command gating, full scalar drafts, normalization, null/removal, stale acknowledgement/session/selection/draft states, benign refresh, authored base types, attached identities and ambiguous module rejection |
| Inspection suite | 174 passed | Real .NET 8/9/10 source validation, inherited/AddOwner/attached metadata, custom wrapper/converter/provider rejection, nullable enum converter regression, invariant conversion, validation callbacks, stale tokens/revisions and existing live inspection/transport workflows |
| Preview suite | 66 passed | Existing compiled/source preview, property override/reset, source write-back and transport regressions |
| Full loaded-window UI | 1 passed | Live source diff cancellation, stale editor/external file rejection, literal/null/removal apply and undo, local style-binding override, composite-binding replacement, runtime binding replacement during review without inspector refresh, unchanged source disk bytes and existing designer/theme/runtime workflows |
| Packaged inspection agent | 3 passed | The .NET 8/9/10 source-validation cases against the explicitly selected published startup hook/agent; overlaps the full inspection suite |
| Portable executable | Passed | First idle shell 2,013 ms, graceful shutdown 156 ms, exit code 0 and isolated profile saved |

Reports are under `artifacts/TestResults/xaml-devtools` with the `live-source-*-final.trx` prefix. The distinct full-suite total is **599 passed**, with no failures or skips. Earlier focused runs overlap these checks. `artifacts/screenshots/live-xaml-source-review.png` shows the actual loaded diff and was visually inspected. Apply changes only the editor buffer; the test verifies the inspected binding remains unchanged and the disk bytes remain original until a separate save.

The final focused UI rerun is `live-source-ui-copy-final.trx` and overlaps the full UI workflow. The portable bundle was refreshed after the final caption-only wording adjustment. `live-source-packaged.trx` explicitly selects `artifacts/WpfStudio/Inspection/WpfStudio.Inspection.StartupHook.dll` through the absolute `WPFSTUDIO_INSPECTION_HOOK_UNDER_TEST` environment variable; fixture startup verifies the loaded agent path. The actual executable smoke report is `artifacts/performance/xaml-source-writeback-portable-smoke.json`. This is an empty-shell workstation sample with potentially warm caches, not a clean-machine or designer-interaction performance guarantee.

The runtime adapter captures bounded weak observations and verifies canonical property identity and eligible authored types using metadata. It does not invoke application CLR wrappers, custom converters, or type-description providers to normalize a source value. Explicit proposed-value validation may invoke the dependency property's validation callback; it rechecks observed state afterward. Verify-only and removal skip that callback. No source operation sets the live dependency property or updates a binding source/target.

The shell checks module identity, compiled resource/PDB mapping, captured build bytes, current disk and open buffer before review and again after acceptance. A short file read lease stabilizes each verification, but is released during the human review so edits and saves can proceed. A final transaction guard rejects changes to editor/selection/draft/session state after asynchronous checks. Actual UI coverage confirms an external save remains preserved and a changed runtime binding rejects the pending proposal even without an intervening inspector refresh.

Source edits remain local to the verified authored object. They do not edit originating shared style/resource declarations, implement generalized Hot Reload, or establish provenance for loose XAML that reused a compiled resource URI. Classless dictionaries without generated symbol mappings and unsupported/ambiguous source identities remain unavailable. Save and rebuild with inspection to establish a new baseline after applying a source change. The broader language/designer goal remains active in `docs/xaml-devtools-design.md`; older checkpoint sections below describe their state at those runs.

## XAML DevTools: binding evidence and checked source navigation (2026-09-27)

Live XAML now separates current binding configuration/validation from historical WPF failure evidence. **Show XAML** checks the running module, compiled resource mapping, matching portable/embedded PDB, captured build bytes, current file and open buffer before navigating to an exact object declaration. The final Release solution build has zero warnings and errors.

| Check | Result | Coverage |
| --- | --- | --- |
| Core suite | 106 passed | Read-only build source leases, exact byte hashes including BOM/UTF-16, cancellation/release, size limits, strict object locations and existing document/edit transactions |
| Shell suite | 165 passed | Source capability gating, stale module/callback responses, changed source identity, selection and pause/resume round trips, unchanged-source refreshes, existing designer and temporary edit workflows |
| Inspection suite | 171 passed | Actual .NET 8/9/10 module identities/source hints, portable/embedded symbols and mismatched identities, bounded symbol reads, compiled resource URI mappings, URI rejection, structured missing/null/conversion/validation evidence, historical recovery/source changes and existing transport/edit/lifecycle checks |
| Focused loaded Live XAML UI | 1 passed | Actual launch, binding issue selection, exact source jump, unsaved-buffer and external-file rejection, retained edits, loaded binding details and existing temporary editing |
| Final full loaded-window UI | 1 passed | Existing workspace/designer/theme/source-write workflows plus the new navigation/details workflow and pixel scrolling through expanded historical evidence |
| Packaged inspection agent | 7 passed | Actual .NET 8/9/10 module/source metadata and structured diagnostics against the explicitly selected published agent; overlaps the full inspection suite |
| Portable executable | Passed | First idle shell 1,920 ms; graceful shutdown 141 ms; exit code 0 and isolated profile saved |

Reports are in `artifacts/TestResults/xaml-devtools`: `inspection-source-core-final.trx`, `inspection-source-shell-final.trx`, `inspection-source-inspection-full.trx`, `inspection-source-ui-focused.trx` and `inspection-source-ui-full.trx`. The UI runs overlap. Earlier failed runs identified Windows sharing-denial exception variants in a lease test, generic WPF follow-up events displacing useful missing-member evidence, a fixture distinction between conversion and exception validation rules, and pending navigation status persisting after pause. These were corrected before the final runs. The diagnostic fixture verifies that repeated inspection does not add source getter, converter, source-write or opaque validation-content formatting calls.

Screenshots `artifacts/screenshots/live-xaml-source.png` and `live-xaml-binding-details.png` show the loaded source jump and historical evidence. Object-source navigation is narrower than binding/style declaration navigation or source editing. Classless dictionaries without generated symbol mappings, missing/ambiguous/truncated symbols, unsupported qualified/resource URIs and uncaptured build inputs remain unavailable. Public WPF hints cannot authenticate that loose XAML never reused a compiled resource's base URI. Historical evidence is not a current cause determination, even when its root object still matches.

The portable package at `artifacts/WpfStudio` was refreshed after the final build. `inspection-source-packaged.trx` uses an absolute `WPFSTUDIO_INSPECTION_HOOK_UNDER_TEST` pointing into that package; fixture startup reports verify its loaded agent path. `artifacts/performance/xaml-source-portable-smoke.json` records the real executable measurement with an isolated empty profile; caches may be warm and this is not a clean-machine startup claim.

**Verify source on build** holds known workspace XAML inputs stable through the inspected rebuild. Custom targets that rewrite these files can require disabling that option; runtime inspection remains available without verified source navigation. Full language/designer acceptance and live source write-back remain open in `docs/xaml-devtools-design.md`. The older checkpoint sections below describe their state at the time of each run.

## XAML DevTools: temporary runtime property editing (2026-09-27)

Preview and running-application inspection now share the `net8.0-windows` `WpfStudio.Wpf.PropertyEditing` library. The Live XAML Properties tab validates and tries explicit temporary values, restores owned overrides, reloads changed observations and queries uncertain edit outcomes. The Release solution builds with zero warnings and errors.

| Check | Result | Coverage |
| --- | --- | --- |
| Initial complete inspection suite | 99 passed | Existing runtime/debugger/picker checks plus real .NET 8/9/10 editing and mutation transport regressions |
| Final transport and stale-token guards | 14 passed | Includes two later malformed-status acknowledgement cases and unchanged tokens across tree revisions; overlaps the initial suite |
| Operation-history limit | 1 passed | 1,024 retained operation receipts, refusal of new mutations at capacity, retained replay/status and disconnect restoration |
| Final Shell suite | 143 passed | Full values/nulls, validation without mutation, stale and newer drafts, unknown outcomes, session replacement, paused edits and tree/grid selection restoration |
| Final Preview suite | 66 passed | Existing preview/compiled/source-write workflows plus shared-editor binding restoration, model setter counts, precedence, weak collection and unsupported/oversized value cases |
| Large runtime property payloads | 3 passed | .NET 8/9/10 fixtures with 160 large Unicode properties; full values within budget, explicit omissions beyond it and continued connection responsiveness |
| Focused loaded Live XAML UI | 1 passed | Real launch, picking, validation/apply/reset, unchanged TwoWay source, resumed model updates and IDE binding-error checks |
| Final full loaded-window UI | 1 passed | Existing workspace/designer/source-write/theme workflows plus Live XAML edits and usable selected-property table after the final layout fix |
| Packaged preview integration | 25 passed | Explicit portable host process, compiled resources, property/reset RPC and source-write round trips; includes three native WPF baseline cases |
| Complete inspection suite with packaged agent | 105 passed | All inspection checks, including final transport, operation-budget and large-value regressions, against the explicitly selected portable agent |

Reports under `artifacts/TestResults/xaml-devtools` are `inspection-editing-first.trx`, `inspection-editing-final-guards.trx`, `inspection-editing-operation-limit.trx`, `inspection-editing-shell-final.trx`, `inspection-editing-preview-bounded.trx`, `inspection-editing-payload.trx`, `inspection-editing-ui-focused.trx` and `inspection-editing-ui-full-final.trx`. Focused checks overlap the full suites and are not additional independent totals. `artifacts/screenshots/live-xaml-editing.png` was visually inspected; the UI test also verifies table height and selected-row realization.

Runtime fixtures verify local/style/default/inherited restoration, local/style TwoWay source setter counts, MultiBinding/PriorityBinding identity restoration, explicit null versus empty values, complete strings, invariant numeric conversion, attached-property name collisions, application replacements and edits on a second dispatcher. Lifecycle cases distinguish cancelled queued edits from callbacks already executing, query eventual outcomes without replay, and restore overrides after detach or abrupt EOF. A throwing callback is reported as an unknown outcome. Bounded weak storage does not keep edited targets alive.

The first full loaded UI check (`inspection-editing-ui-full.trx`) exposed a real selection bug: rebuilding TreeView containers briefly set the selection to null and erased the chosen property after an edit. The fix preserves selection/drafts through this transient state, with a shell regression and a successful focused UI rerun. Initial preview failures were fixture setup errors: the animation clock had not started, and a read-only test source accidentally inherited TwoWay binding mode. Corrected fixtures assert active animation before rejection and use a dependency-property source for the weak-reference test.

The refreshed portable executable passed isolated-profile startup, settings persistence and graceful shutdown: 2,236 ms to an idle empty shell, 168 ms to exit, exit code 0 (`artifacts/performance/xaml-editing-portable-smoke.json`). These workstation samples may use warm caches and initialize no workspace or optional feature.

`inspection-editing-preview-packaged.trx` explicitly selects `artifacts/WpfStudio/PreviewHost/WpfStudio.PreviewHost.exe`; the process check verifies its actual executable path. The shared property-editing DLL hashes match in the preview bundle, inspection bundle and tested build output.

`inspection-editing-packaged.trx` passes `WPFSTUDIO_INSPECTION_HOOK_UNDER_TEST=artifacts/WpfStudio/Inspection/WpfStudio.Inspection.StartupHook.dll` as an absolute path. Real .NET 8/9/10 fixtures verify the loaded agent path; the full 105 also include pure lifecycle and transport cases. This supersedes the earlier inspection totals without adding overlapping runs together.

OneWayToSource editing remains unsupported because restoring that binding can itself write to the model. Dynamic resources, animations, complex object content and rich TextBlock replacement are also rejected for temporary edits. Editable text is limited to 65,536 characters per property and 262,144 characters per live property snapshot; excess values are explicitly omitted and disabled, while owned overrides retain reset access. Inspector cleanup restores property configuration and cannot reverse arbitrary application callbacks; a blocked dispatcher defers restoration. Runtime source hints remain unverified, and these edits do not write XAML source. Broader design fidelity, source mapping and full language/designer acceptance remain recorded in `docs/xaml-devtools-design.md`.

## XAML DevTools: binding lifecycle and runtime picking (2026-09-27)

The Release solution builds with zero warnings and errors. Binding observations now cover the live tree, including explicit style and attached-property setters, with weak expression identities and bounded rotating scans. The Live XAML pane groups issues by failure while retaining individual elements, observed failure episodes, verified recovery, removal/unload states and ended sessions. Picking and adorner highlighting operate across the target's UI dispatchers.

| Check | Result | Coverage |
| --- | --- | --- |
| Initial expanded inspection suite | 68 passed | Previous runtime/debugger/transport checks plus real .NET 8/9/10 lifecycle and picker cases, a 600-binding scan stress case and pure lifecycle state checks |
| Final lifecycle/EOF guards | 26 passed | Overlapping tracker suite plus later conflicting duplicate observations in either order, clipped input evidence, and abrupt pipe EOF with actual input-handler/adorner cleanup |
| Final Shell full suite | 136 passed | Issue grouping and element navigation, history filtering, deferred real connection, fast completed picks, stale picker/highlight responses and paused interaction |
| Full loaded-window UI after connection fix | 1 passed | Existing workspace/designer workflow, actual inspection launch, issue navigation, both themes and picking the live fixture's button |
| Final focused Live XAML UI | 1 passed | The same inspection workflow plus selected-row visibility after the final automatic tree-scroll change; overlaps the full UI case |
| Final inspection suite with packaged agent | 72 passed | Complete inspection suite against the explicitly selected portable agent, including the later lifecycle guards and abrupt-disconnect test |

Reports under `artifacts/TestResults/xaml-devtools` are `inspection-lifecycle-first.trx`, `inspection-lifecycle-final-guards.trx`, `inspection-lifecycle-shell-final.trx`, `inspection-lifecycle-ui-full-final.trx` and `inspection-lifecycle-ui-scroll.trx`. Screenshots `live-xaml.png`, `live-xaml-light.png` and `live-xaml-picked.png` are under `artifacts/screenshots`. The loaded UI checks asserted no binding errors in the IDE's controls. The picked-row screenshot was visually inspected, and the UI test asserts that the selected tree row is inside its visible viewport.

The final complete inspection report is `inspection-lifecycle-packaged.trx`. It uses `WPFSTUDIO_INSPECTION_HOOK_UNDER_TEST` pointing to `artifacts/WpfStudio/Inspection/WpfStudio.Inspection.StartupHook.dll`; real application fixtures verify the loaded agent's assembly location against the selected package. Pure tracker and negative-protocol cases are included in the 72, and overlapping earlier runs are not additional independent coverage.

The first loaded UI run (`inspection-lifecycle-ui-full.trx`) exposed an actual connection-state bug: a tracker was marked ended while its new target was still connecting. The implementation now distinguishes waiting for the first connection from a disconnected session, with a dedicated shell regression and successful focused/full UI reruns. Review also caught fast picking completion before the arm response, duplicate binding observations with conflicting evidence, and loss of removal evidence when observations are capped; regressions cover each case.

Runtime input tests use actual WPF `InputManager.ProcessInput` staging and routing in the target process, with application handler counts and actual adorner-layer inspection. They cover normal input restoration after cancel, Escape, detach and abrupt EOF across a main window, second dispatcher and popup, plus an interrupted mouse-down/up sequence. These tests do not establish physical-pointer behavior through every native/custom input system or high-DPI configuration. Highlighting requires a visible supported adorner surface.

The republished win-x64 executable passed isolated-profile startup, settings persistence and graceful shutdown: 2,005 ms to an idle empty shell, 150 ms to exit, exit code 0 (`artifacts/performance/xaml-lifecycle-portable-smoke.json`). These workstation measurements may use warm caches and initialize no workspace or optional feature.

Polling reports observed failure episodes, not failures occurring entirely between polls. General nested-path/null/conversion attribution, runtime property mutation, verified current-source mapping, broader virtualization/native-input/DPI scenarios and large multi-root scalability remain acceptance work in `docs/xaml-devtools-design.md`. No parity or superiority claim is established by this increment.

## XAML DevTools: running-application inspection (2026-09-27)

The expanded Release solution builds with zero warnings and errors. Launch-time inspection now runs in a real target application through a separate .NET 8 bootstrap/agent/protocol; it does not instantiate the preview host in that application.

| Check | Result | Coverage |
| --- | --- | --- |
| Inspection integration suite | 38 passed | Real .NET 8/9/10 DLL and apphost launches, actual DataContexts and bindings, early traces, missing CLR properties, MultiBinding child failures, style values, multiple windows/popups/UI dispatchers, removed-node identities, runtime updates and disconnect responsiveness |
| Transport/debug integration in that suite | Included above | Bad protocol/token/process identities, malformed frames and missing peers, cancellation/late responses, timeouts, debugger pause, a real 11.5-second breakpoint during handshake, inherited/profile-cleared/profile-replaced startup hooks, and independent inspector disconnect |
| Shell full suite | 129 passed | Existing shell/designer behavior plus live inspector refresh, selection races, pause/resume, removed nodes, session replacement and cancelled connection cleanup |
| Existing debugger regressions | 7 passed | Managed launch/attach, stop/detach ownership, runtime compatibility and output behavior after the launch environment and detach-capability fixes |
| Full loaded-window UI | 1 passed | Existing workspace/docking/theme/designer checks plus the actual Run with inspection command against an isolated WPF project: rebuild with source information, preserve project-file bytes, honor launch arguments, display a runtime binding failure in both themes, and disconnect without stopping the target |
| Focused Live XAML UI | 1 passed | The same new launch/inspection workflow without unrelated UI scenarios; overlaps the full UI case |

Reports are `inspection-final.trx`, `inspection-shell-full.trx`, `inspection-debugger-regression.trx`, `inspection-ui-full.trx` and `inspection-ui.trx` under `artifacts/TestResults/xaml-devtools`. Screenshots are `artifacts/screenshots/live-xaml.png` and `live-xaml-light.png`. Both UI runs asserted no binding errors in the IDE's own controls.

The final win-x64 portable package passed all prerequisite checks, including the new three-DLL inspection bundle check. Its actual executable reached an idle empty shell in 2,151 ms, saved its isolated profile, and shut down gracefully in 162 ms with exit code 0. These are workstation samples with potentially warm caches and no workspace initialized; the raw report is `artifacts/performance/xaml-runtime-portable-smoke.json`.

All 38 inspection checks passed again with `WPFSTUDIO_INSPECTION_HOOK_UNDER_TEST` set to the absolute hook path under `artifacts/WpfStudio/Inspection`. The six .NET 8/9/10 DLL/apphost cases and five real debugger cases assert the target's loaded agent assembly location matches that exact package directory, preventing development-output fallback. The report is `inspection-packaged.trx`; this overlaps the source suite rather than adding independent coverage. The rebuilt package's preview host also passed the explicit process-path/render/pick/binding/override/reset regression (`preview-runtime-package.trx`).

The initial negative-protocol run exposed an uncaught `InvalidDataException` that left handshake completion pending; it was deliberately stopped after reproducing that failure, then corrected and rerun successfully. Real debugger testing also established that the pinned netcoredbg cannot detach a process it launched even though its DAP handler reports success. WpfStudio refuses that unsupported operation before teardown and keeps the debuggee/session alive; attached-process detach is still covered. Debugger launch now isolates inherited startup hooks from the adapter while forwarding the effective hook list to the target, avoiding the adapter's append behavior. See `docs/xaml-runtime-bootstrap.md` for upstream source references.

These checks establish read-only inspection of explicitly instrumented launches on this workstation. They do not establish arbitrary attachment to an already-running uninstrumented process, runtime property editing/picking, verified current-buffer source mapping, complete failure-resolution lifecycles, trimmed/AOT or other runtime/architecture support, or clean-machine installation.

## XAML DevTools: source edits and compiled views (2026-09-27)

The final Release solution build completed with zero warnings and errors. The following full suites passed on that implementation; the focused designer run is additional workflow evidence, not another distinct test total.

| Check | Result | Coverage |
| --- | --- | --- |
| Core full suite | 72 passed | Exact source identities, versions/hashes, escaped literals, attributes/property elements/implicit content, attached-property ownership, rejected ambiguity and preservation of unrelated source |
| Shell full suite | 123 passed | Inspector validation and reviewed buffer edits, cancellation/stale review, undo, selection races, preservation of newer input during pending Try/Reset, compiled-mode state and existing shell behavior |
| Preview full suite | 47 passed | Real WPF source-edit/reload round trips, rich content, property identity collisions, nonmutating validation, compiled Window/Page/UserControl activation, absolute application pack URIs and existing host lifecycle behavior |
| Loaded-window UI | 1 passed | Full workspace/docking/theme workflow plus real source-diff approval, unchanged disk content, undo, compiled preview and disabled unsupported source writes |
| Focused designer UI | 1 passed | Same designer workflow run without the unrelated workspace/performance fixture |

Reports are under `artifacts/TestResults/xaml-devtools`: `core-writeback.trx`, `shell-writeback.trx`, `preview-writeback-final.trx`, `designer-writeback-ui-full.trx` and `designer-writeback-ui.trx`. Screenshots include `artifacts/screenshots/xaml-designer-source-diff.png` and `xaml-designer-compiled.png`. Both UI runs asserted no binding errors in the IDE's own views.

The compiled fixture deliberately throws from its project App constructor and startup handler. Successful rendering verifies that compiled application resources are loaded without executing either path, while the selected view's constructor and Loaded handler do run. Assembly hash/module provenance, fresh-process refresh, missing view types and constructor failures are covered. Compiled snapshots do not claim current-buffer source locations.

Three absolute application resource regressions were demonstrated failing before the host identity fix. They now verify compiled merged dictionaries, view-local dictionaries and `Application.GetResourceStream` resolve against the project, including source-to-compiled process restart and a view without App resources. The fixture also checks the actual entry/resource assembly identities.

Round-trip regressions check the loaded control after applying the proposed source change. Three direct WPF baseline cases separately establish that WPF normalizes entity-encoded CRLF text to LF during XAML loading, and that rich Run content can render without populating `TextBlock.Text`. Rich-content checks therefore inspect the actual Run text and formatting. Exact escaping is checked in the source; no stronger loaded-text guarantee is claimed.

The republished win-x64 application passed actual executable startup, isolated-profile persistence and graceful shutdown. This workstation sample reached an idle empty shell in 1,900 ms and exited in 141 ms with exit code 0. The raw report is `artifacts/performance/xaml-writeback-portable-smoke.json`; caches may be warm and no project or optional feature was initialized during that measurement.

The final packaged-host run passed 24 selected checks with `WPFSTUDIO_PREVIEW_HOST_UNDER_TEST` pointing at `artifacts/WpfStudio/PreviewHost/WpfStudio.PreviewHost.exe`. The explicit-host test also verified the actual process image path, preventing source-tree fallback. Coverage includes compiled resources/code-behind/absolute pack URIs, source-write round trips and render/pick/binding/reset behavior. Three selected native-WPF baseline cases run within the test process. The report is `artifacts/TestResults/xaml-devtools/preview-writeback-packaged.trx`; these checks overlap the full preview suite above.

This increment does not establish running-application inspection, custom DI/sample factories, style/resource-declaration write targets, full compiled-design fidelity, other target runtimes/architectures, or a clean-machine installation. The full-goal acceptance ledger remains in `docs/xaml-devtools-design.md`; `docs/xaml-runtime-bootstrap.md` records the next runtime integration work.

## XAML DevTools: language and isolated preview (2026-09-27)

The expanded Release solution builds with zero warnings and errors. Verification used the following full suites and targeted follow-up regressions; focused runs overlap the full suites and should not be added to them as independent test totals.

| Check | Result | Coverage |
| --- | --- | --- |
| Workspace full suite | 122 passed, 1 opt-in SDK 8 skip | Existing workspace behavior plus schema, binding, navigation and real generated/unsaved type information |
| Final XAML-focused suite | 88 passed | Includes subsequent style-setter source fixes, POCO DataContext handling and getter-only attached collections |
| Final Shell full suite | 113 passed | Editor completion/navigation/hover/checked fixes, undo, stale results and designer state tests |
| Final designer state tests | 9 passed | Includes pathless bindings and cancellation while automatic refresh is paused |
| Final preview engine/client | 18 passed | Real WPF/STA and subprocess RPC, binding status, source maps including templates, picking, reversible properties, malformed source recovery, crash/hang/cancellation/disposal and explicit-host verification |
| Loaded-window UI | 1 passed | Real workspace loading/building, both themes, docked preview, selection/property scrolling, temporary edits/reset, malformed-XAML recovery and graceful disposal |
| Published preview host | 1 focused check passed | Exact published process image, real RPC rendering/picking/source maps, binding failures, style/binding override/reset and process exit |

Reports for root-driven checks are under `artifacts/TestResults/xaml-devtools`. The preview suite also verifies that an active host does not lock project outputs: the project DLL and a referenced DLL are overwritten successfully while their shadow copies remain loaded. It verifies dependency/assets copying, rebuilt-assembly detection, incomplete-host discovery and removal of owned temporary files. A style-binding reset regression was demonstrated failing before the fix; reset now preserves `Style` precedence and subsequent source updates.

The loaded-window check captures `artifacts/screenshots/xaml-designer.png`, `xaml-designer-light.png` and `xaml-designer-layout.png`. It verifies the designer shares the bottom tool area while the source remains visible and introduces no UI binding errors.

The updated win-x64 portable package was published successfully. Its actual executable passed isolated-profile startup, settings persistence and graceful shutdown: 1,816 ms to an idle empty shell and 137 ms to exit, with exit code 0. These are workstation samples with potentially warm caches; the raw report is `artifacts/performance/xaml-portable-smoke.json`. The packaged-preview check explicitly selected `artifacts/WpfStudio/PreviewHost/WpfStudio.PreviewHost.exe` and verified the running process image, preventing silent fallback to a source-tree build.

The source-preview process runs on .NET 10 and explicitly omits code-behind handlers/application startup. These checks do not establish compiled-view fidelity, arbitrary pack-resource behavior, running-application attachment, older-runtime/architecture compatibility, full Hot Reload, or a clean-machine portable installation. Those broader acceptance items remain recorded in `docs/xaml-devtools-design.md`.

## Initial XAML language assistance (2026-09-27)

The Release solution build completed with zero warnings and errors. Validation covered the affected Workspace, Shell, and App UI suites: **176 passed**, with the existing opt-in SDK 8 check skipped (Workspace 74 passed / 1 skipped, Shell 101 passed, App UI 1 passed). Reports are under `artifacts/TestResults/xaml-language`; `shell-final.trx` includes the final saved-and-closed XAML issue regression.

- 31 semantic cases cover scoped design/runtime declarations, typed templates, inherited/partial/referenced members, exact UTF-16 and entity-encoded spans, unfinished binding completion, replacement in the middle of a member, and conservative handling of unknown sources, collections, styles, converters, and unavailable generator output.
- Real worker tests exercise actual Toolkit-generated properties and commands, unsaved C# changes, restart/replay, discard/close restoration, and linked XAML with different owning project types.
- Editor/shell tests cover semantic completions, immediate stale-diagnostic removal, refresh after C# changes, disconnection, offline framework/resource suggestions, Problems/WPF Issues projection, and removal of obsolete index errors after save/close. The loaded-window UI smoke check passed.
- Release output was used because an existing running app held the Debug output files open. Suite invocations disabled MSBuild node reuse to avoid background build nodes retaining redirected output streams. These initial checks covered source-language assistance; the later isolated-preview increment is recorded above.

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
