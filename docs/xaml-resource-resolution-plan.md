# Project-aware XAML resource resolution

**Status: resource resolution and bounded dependency capture implemented and validated.** Ordinary editor requests retain the evaluated URI catalog separately from the texts they read. The scalability increment passed 876 distinct affected tests and the final packaged worker returned correct results in all 20/200/600-file benchmark fixtures. See [worker measurements](xaml-language-performance.md) for latency and its limits, and [VALIDATION.md](../VALIDATION.md) for exact test reports. These bounded results do not establish arbitrary solution scale or runtime resource equivalence.

## Intended result

The implementation supplies the declared object type for bindings such as `Source="{StaticResource Customer}"` and local `DataContext="{StaticResource Customer}"` from evaluated project dictionaries, including explicit unsaved dictionary overlays. Completion, diagnostics, hover, definition navigation, spelling fixes, and symbol occurrence analysis receive the same resource evidence. Resolution reads source and compiler symbols; it does not create resource objects, execute application constructors or markup extensions, invoke getters, or load project assemblies into the IDE.

Inference describes authored declarations and current compiler symbols. It does not establish the runtime identity of an object or account for resources replaced by application code.

## Current architecture

- `ProjectDiscovery.cs` retains an internal evaluated resource inventory, separate from the file display model. It preserves build action, physical path, and logical resource or supported content output mapping.
- `WorkspaceEngine.XamlResources.cs` captures evaluated identities and available project-reference contexts from one Roslyn solution. Ordinary requests read the current source, its unique application document, and their transitive dictionary imports. Captured text, hashes, and overlay versions are separate from the complete identity catalog. Each consumer receives its own project-scoped `XamlResourceContext`.
- `Xaml/XamlResourceIndex.cs` owns immutable physical/logical identity lookup. `XamlResourceDependencies.cs` discovers direct imports using the same URI resolver and framework schema checks as semantic lookup; it performs no file access.
- `Xaml/SchemaTypeResolver.cs` weakly caches immutable assembly/XML namespace indexes by exact Roslyn compilation identity. Changed source, generated output and references get their own metadata; cancellation and mutable lookup state remain request-local.
- `Xaml/XamlResourceGraph.cs` performs bounded resource lookup without file access. `XamlBindingSources.cs` uses its declaration evidence for explicit binding sources and declared data contexts, retaining unknown results for runtime-provided values.
- `WorkspaceEngine.Xaml.cs` passes the context to analysis, completion, hover, definition, and spelling actions. `XamlDocumentRequest` and `XamlCompletionRequest` now accept optional `XamlOverlays`.
- `WorkspaceEngine.XamlProject.cs` retains bounded full-scan capture for batch analysis and adds resource dependency fingerprints to cache keys. Each immutable snapshot memoizes its fingerprint once per project instead of recomputing it for every consumer.
- `WorkspaceEngine.XamlSymbols.cs` uses the same context for references and rename. It plans all XAML replacements before rebinding consumers against the complete proposed dictionary texts.
- The Shell supplies user-open XAML buffers and a resource generation. Dictionary edits, close/discard, save, and observed disk changes invalidate dependent editor work even when the consuming buffer is unchanged.

## Components

### Immutable worker snapshot

A resource snapshot is built from evaluated project inventory, one immutable Roslyn solution, the caller's explicit XAML overlays, and bounded reads of known closed files. Its identity catalog retains physical paths, logical resource paths, build actions, and declaring assemblies independently of loaded text. Duplicate identities remain in the catalog even when neither candidate is read, so an unloaded resource cannot make an ambiguous URI look unique. The semantic graph parses source-preserving syntax within the operation; there is no shared mutable request context on the language-service instance.

For ordinary analysis, completion, hover, definition, and spelling requests, the worker seeds a breadth-first dependency capture with the current source and the unique current-project application document, where one is established. It discovers literal dictionary `Source` imports and follows their transitive closure. It deduplicates byte reads by physical path while discovering imports separately for each logical identity: a linked file can have different relative URI bases. Unrelated views are retained as metadata but their contents are not read. A consulted identity without captured text remains unknown rather than definitely absent.

The selected document's text/version must agree with a matching overlay. Conflicting duplicate overlays reject the request. Closed dependencies are read by content on each operation, not trusted solely by timestamp and length. Unreadable, missing, malformed, or oversized consulted dependencies remain unavailable. An unrelated locked or missing view does not consume the ordinary request's text budget or invalidate an otherwise established import closure.

Each external document keeps its own lexical XML namespace scope and declaring assembly identity. Its syntax root is not reparented beneath an importer. Resource object types are resolved into the consumer's current compilation using that assembly identity; binding-path prefixes continue to use the binding's lexical scope. Ambiguous or missing assembly/type identities remain unknown.

### Evaluated URI resolver

URI lookup selects from evaluated resource identities. Dependency discovery and semantic lookup share this resolver and their structural framework-type checks, including inherited `xml:base` barriers. They do not enumerate directories, fetch network resources, or read an arbitrary path obtained from a dictionary's `Source` value.

The resolver handles:

- Relative paths to same-project `Page` or `Resource` dictionaries, including nested imports.
- Application pack paths and unqualified `Assembly;component/...` paths to uniquely identified evaluated referenced projects.
- Linked files whose evaluated logical path identifies the resource uniquely.
- A uniquely identified current-project `ApplicationDefinition` as application resource scope where that application ownership is established.

For `Page`, `ApplicationDefinition`, and `Resource` items, discovery follows evaluated `LogicalName`, then `Link`, then WPF's project-relative resource naming, including its outside-project basename fallback. It rejects unsupported mappings. `Content` additionally requires an explicit evaluated `TargetPath` and a supported `CopyToOutputDirectory` value; a physical source path alone does not establish deployment. Referenced-assembly content is unsupported.

Application scope is supplied only for the current evaluated executable project with a unique `ApplicationDefinition`. A library does not borrow an executable consumer's application resources. The selected component assembly must be unique within the current project-reference context. Version/key-qualified component URIs are currently unavailable rather than matched by simple name.

Network URIs, site-of-origin paths, unknown `xml:base`, custom URI providers, binary-only resources, ambiguous resource names, and unsupported deployment mappings remain explicit unknowns. Normalize supported URI paths and escapes consistently, but do not interpret an unverified path as permission to read arbitrary files.

### Resource lookup graph

Lookup distinguishes **found**, **definitely absent within known declaration scope**, and **unknown**, with declaration/source evidence and an explanatory reason.

The implementation is structured around these invariants; validation scope is recorded separately below:

1. Direct dictionary entries take precedence over merged dictionaries. Merged dictionaries are searched from last to first.
2. An unknown higher-precedence dictionary blocks selection from lower-precedence dictionaries or an outer scope. A known direct match may still win over an unknown merge in the same dictionary.
3. Duplicate direct keys, unsupported keys that could collide, malformed dictionaries, and unresolved custom resource providers must never select an arbitrary winner.
4. Resource object namespace resolution uses the declaration's lexical scope. Binding path namespace resolution continues to use the binding's lexical scope.
5. No inferred resource source may fall back to the surrounding `DataContext` when resolution fails.
6. Declaration order matters for static-resource references. Unsupported forward references remain unknown rather than being made valid by scanning the whole document.
7. Resource declaration origin and ambient lookup scope are separate. External dictionaries, deferred templates, and styles can have different lookup contexts; do not infer a consuming element's runtime tree from XML import containment.
8. `Style.BasedOn` participates in WPF resource lookup. Resolve a proven declared chain with cycle guards, or stop before selecting an outer resource that it could shadow.
9. Lookup, dictionary, import, alias, and style recursion use operation-local active query guards. Parsed external documents are cached only within the operation. Reusing the same dictionary through separate valid branches is not itself a cycle.
10. Resource aliases may resolve only through the same guarded lookup. A markup extension object's CLR type is not the type returned by `ProvideValue`.
11. Application-supplied constructors, factories, type converters, `DataSourceProvider`, dynamic-resource expressions, and custom markup extensions are never executed or guessed.
12. `x:Shared="False"` does not establish shared runtime identity. If a declared value type is otherwise known, inference can describe that type without claiming object identity.

The supported declaration slice includes literal string keys, ordinary typed objects, and object-element `<StaticResource ResourceKey="..."/>` aliases through the same guarded lookup. A declared static `Style.BasedOn` chain can contribute resource scope with cycle guards. Object-element or runtime `BasedOn`, type/custom keys, source-plus-inline composition, code-backed/custom dictionaries, and uncertain deferred ambient cases remain unsupported. `x:FactoryMethod`, constructor arguments, custom markup extensions, `DataSourceProvider`, and dynamic resources do not establish an inferred result type.

With a project context, structural declarations such as `ResourceDictionary`, `Style`, and `Application` must resolve to the supported WPF framework types. Matching an element's spelling is insufficient: unqualified external roots and custom same-name types remain unknown. Valid CLR namespace aliases to the framework types are supported. A runtime `Resources` value on a resolved base style also blocks fallback rather than being treated as an empty scope.

## Freshness, bounds, and publication

All binding-language operations accept an optional immutable resource context after their existing cancellation token. Standalone APIs remain usable without an external project context.

Batch cache keys include project identity, consumer path/text hash, and the captured resource dependency fingerprint. The immutable snapshot memoizes fingerprints per project with thread-safe lookup, preserving assembly/URI identities, captured hashes, overlay versions, and availability states. Closed dictionary bytes are reread within bounds; timestamps and lengths are not treated as content identity. Missing/unavailable states participate in fingerprints. Roslyn solution changes still invalidate semantic caches and final publication.

The Shell's resource generation cancels or discards pending editor results after relevant buffer or observed disk changes. Periodic reconciliation need not increment that generation when it observes no change. Spelling actions therefore also contain hashed/versioned no-op `AdditionalEdits` for the resource documents consulted to prove the selected binding, including higher-priority dictionaries searched before a match. Unrelated unavailable markup does not globally disable a local or design-context spelling fix.

Batch analysis and rename retain their bounded full evaluated scans; ordinary dependency capture does not relax these limits. Rename requires a complete workspace scan, guards scanned XAML documents including unchanged dictionaries, and rebinds both edited and untouched occurrences against the changed compilation and all proposed resource texts. The existing atomic workspace transaction checks these prerequisites before applying edits. Read-only prerequisite dictionaries are not modified, opened as editor tabs, or given undo entries.

The evaluated catalog is bounded independently at 16,384 identities and 16 million metadata characters, with limits on individual identity fields. Text capture remains bounded at 512 file/context identities, 8 million decoded characters, and 1 million characters per file. Ordinary dependency traversal checks its identity budget before enqueueing work and limits import depth to 64. Overlay requests are limited to 2,048 entries and 16 million characters. The semantic graph independently bounds imports/aliases to depth 64, XML depth to 256, and lookup work to 65,536 steps. Cancellation is checked during reads and traversal, including work that produces no diagnostics.

Missing or locked consulted documents remain explicit unavailable entries. Catalog truncation, omitted inventory, or unproven output mappings make external identity resolution incomplete, rather than allowing selection from a deceptively unique partial set. Text-budget exhaustion makes the omitted dependency unavailable without changing the catalog's identity evidence. Existing local declarations can still be analyzed where they do not require that missing evidence. Status messages expose incomplete capture coverage; they do not mark an unexamined dictionary healthy.

## Files and contracts

- Worker: `WorkspaceEngine.XamlResources.cs`, with integration in `WorkspaceEngine.Xaml.cs`, `.XamlProject.cs`, and `.XamlSymbols.cs`; internal evaluated metadata comes from `ProjectDiscovery.cs`.
- Semantic layer: `Xaml/XamlResourceContext.cs`, `Xaml/XamlResourceIndex.cs`, `Xaml/XamlResourceDependencies.cs`, `Xaml/XamlResourceSchema.cs`, `Xaml/XamlResourceGraph.cs`, and the binding source, navigation, and occurrence services.
- Context API: `XamlResourceContext(SourcePath, SourceAssembly, Documents, ApplicationPath, IsComplete, Status, Index)`. `Documents` contains captured snapshots; `Index` supplies evaluated identities independently. Each `XamlResourceDocument` carries `Path`, nullable `Text`, `AssemblyName`, `ResourcePath`, `Kind`, `Status`, and nullable overlay `Version`. The index retains identity fields only.
- RPC requests: optional `IReadOnlyList<XamlDocumentOverlay>? XamlOverlays` on ordinary XAML document/completion requests; project scans and symbol requests retain their existing overlay fields. Spelling action guards use the existing `XamlCodeAction.AdditionalEdits` contract.
- Editor integration: all-open overlay capture, resource-generation guards, dependent reanalysis, and atomic no-op prerequisite handling.
- Test sources: resource graph unit cases, `XamlResourceBindingIntegrationTests`, `XamlResourceMetadataIntegrationTests`, `XamlResourceIndexTests`, `XamlResourceScaleIntegrationTests`, `XamlSchemaMetadataCacheTests`, and Shell request/transaction cases. The semantic engine itself does not instantiate WPF objects.

## Scalability validation

The Release solution build completed with zero warnings/errors (`resource-scale-cache-build.log`). The full affected suites passed: Workspace 400, Shell 474, loaded-app UI 1, and isolated SDK 8 compatibility 1. The SDK 8 run covers the regular Workspace suite's opt-in skip; total distinct passes are 876. The 86 focused schema/resource cases overlap that count. Seven overlapping resource/scale/qualified-binding integration checks also passed with the final packaged worker explicitly selected.

The 21 new Workspace cases comprise 14 index/discovery cases, three real-worker scale workflows, and four schema-cache cases. They verify lookup beyond 512 unrelated views, unread duplicate identities, locked unrelated versus consulted files, direct-key/reverse-merge precedence, logical origins for linked imports, overlays with unchanged consumers, content freshness independent of timestamps and lengths, and stricter batch/rename completeness. Compiler metadata tests cover changed, generated and replaced-reference snapshots, cancellation and concurrent requests.

The same benchmark client exercised prior and final packaged workers with 20, 200 and 600 evaluated XAML files and 30 iterations. The prior 600-file fixture produced only incomplete answers; the final worker passed all 456 observations across the three sizes. Those include 366 RPC requests and 90 combined-cycle timings. Final warm completion p95 was 4.99, 10.05 and 12.37 ms, with first completion still 466–500 ms after workspace loading. See [the full performance report](xaml-language-performance.md) for payload hashes, distributions and limits. No editor typing, native input, concurrent workload or Visual Studio comparison was measured.

## Prior increment validation

These results precede the catalog/closure scalability change:

- Release solution build: zero warnings/errors in `resources-build-final2.log`.
- Workspace suite: 379 passed, with one opt-in SDK 8 case skipped in `resources-workspace-full.trx`. The passing total includes all 42 new resource cases: 38 graph cases, three real-worker cases, and one evaluated metadata case.
- The opt-in SDK 8 regression passed separately in `resources-sdk8-final.trx`. This is an existing SDK compatibility check, not a new resource-resolution matrix across all target frameworks.
- Shell suite: 474 passed in `resources-shell-final.trx`. The focused resource rerun passed all 11 cases in `resources-shell-focused-final2.trx`.

The new cases cover declared resource lookup and conservative failures, unsaved dictionary/model changes, language-operation integration, dependency cache freshness, referenced/linked/application scope, missing/locked recovery, evaluated metadata, and guarded editor transactions. The loaded-app UI check passed (`resources-ui-final.trx`). Four overlapping integration cases passed with the explicitly selected packaged workspace worker (`resources-packaged-workspace.trx`). These results do not establish exhaustive graph stress, dedicated restart/replay coverage for the new graph, or complete runtime resource-lookup equivalence.

The scalability results above supersede these prior counts for the affected suites. The opt-in tool documented in `tools/XamlLanguageBench/README.md` records worker payload hashes and treats a non-null response status as incorrect for its fully known fixtures. Correctness passed at every final fixture size before the latency comparison was accepted.

## Acceptance checklist and remaining validation

This checklist retains the intended evidence and limits for the feature. The confirmed test results above do not mark every statement below as exhaustively tested.

1. A same-project external dictionary supplies `Customer`; binding completion offers `Name`, a typo gets its exact UTF-16 span, and F12 navigates to the actual Roslyn declaration.
2. An unsaved dictionary changes the resource object to unrelated `Order`; completion and errors change immediately without editing the consumer or saving either file. Closing the overlay restores disk behavior.
3. Unsaved C# changes to the declared resource type are reflected through the current compilation. Missing/unavailable model context suppresses dependent claims.
4. Local keys, direct keys, and reversed merge ordering select the correct unrelated same-key types. A missing/locked later merge blocks an earlier match; a direct key in the owning dictionary still wins.
5. Nested relative imports, linked logical paths, component URIs, and referenced-project dictionaries resolve to the intended evaluated identity. Ambiguous assembly/resource mappings stay unknown.
6. Identical XML prefixes in different documents resolve independently. An unqualified `clr-namespace:` in a library dictionary uses that library's assembly.
7. Current application resources work only with proven application scope. A class library does not acquire resources from an unrelated executable project.
8. Duplicate keys, malformed XML, unsupported key forms, unsupported providers, forward references, and uncertain `BasedOn`/deferred scopes produce no fabricated source type or outer-context fallback.
9. Import and alias cycles terminate; repeated acyclic dictionary reuse remains usable. Work/file/text/depth limits and cancellation produce explicit incomplete results.
10. Same-timestamp/same-length dictionary edits, deletion/recreation, file locking/recovery, and changing only an imported dependency invalidate caches correctly.
11. An in-flight overlay or solution change prevents stale publication. Restart/replay reproduces the same current-buffer result.
12. References distinguish unrelated same-spelled properties selected by different resources. Rename guards dependency files and rebinds all affected/untouched occurrences against the changed compilation and the same resource evidence.
13. User constructors, factory methods, converters, getters, and resource values are never invoked during any semantic operation.
14. Spelling fixes guard only their consulted dictionaries, including exact unsaved versions and unchanged higher-priority scopes; an unrelated locked file does not suppress a local/design-context fix. A changed prerequisite rejects the transaction without partial edits or resource undo entries.
15. Evaluated `LogicalName`/`Link` precedence and supported content deployment metadata select the intended URI; unsupported mappings remain unknown.

## Primary references

- [WPF merged resource dictionaries](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/systems/xaml-resources-merged-dictionaries): direct/merged precedence, reverse merge search, source and build-action behavior.
- [WPF StaticResource markup extension](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/staticresource-markup-extension): load-time lookup, object-element syntax, key forms, and unsupported forward references.
- [WPF pack URIs](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/app-development/pack-uris-in-wpf): application/component identities, relative paths, resource/content distinctions, and referenced-assembly restrictions.
- [Official StaticResourceExtension source](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/StaticResourceExtension.cs): ambient resource properties, `Style.BasedOn`, deferred lookup, and application/system fallback.
- [Official ResourceDictionary source](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/ResourceDictionary.cs): source loading, merged dictionaries, and runtime lookup implementation.
- [Official ResourcesGenerator source](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationBuildTasks/Microsoft/Build/Tasks/Windows/ResourcesGenerator.cs): logical resource names, linked aliases, and project-relative/outside-project naming.
- [Official MarkupCompilePass1 source](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationBuildTasks/Microsoft/Build/Tasks/Windows/MarkupCompilePass1.cs): propagation of `LogicalName` and `Link` metadata to generated BAML items.
