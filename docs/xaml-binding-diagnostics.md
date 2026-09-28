# Binding explanations in preview and Live XAML

The **Bindings** tab uses the same explanation panel for an isolated preview and an application launched with XAML inspection. It describes one observed binding expression. These are facts from the last inspection, not continuous monitoring or a fresh evaluation of the source path. See the [preview and live screenshots](xaml-feature-tour.md#inspect-the-failed-binding-step). Measured compatibility and acceptance results belong in [VALIDATION.md](../VALIDATION.md).

## Choose the expression

Select an element, then choose a **Binding declaration**. The selector includes the root binding and captured children of `MultiBinding` and `PriorityBinding`. Each child has its own status, source and details; the parent's observation is not substituted when child details are unavailable. Omission notices identify bounded or incomplete declaration coverage.

In preview, **Explain binding** on a current binding diagnostic opens the exact observed expression. A live binding issue likewise selects its expression after a fresh inspection. The action checks the issue's binding ID, node and property; a replacement with the same path or target name is not the old expression. If it disappeared or changed while the request was pending, the pane reports that instead of selecting a lookalike.

Diagnosis selection is independent of the Properties edit draft. Re-inspection preserves that draft and the selected root or child when its identities still match. **Show binding XAML** uses the selected declaration and its separate [source-verification checks](xaml-binding-navigation.md). Explaining a binding does not apply a temporary value, change source or force a binding update.

## Read the evidence

| Panel content | What it establishes |
| --- | --- |
| Status, category and explanation | WPF's status for this expression at inspection, with a more specific explanation only where the observation supports it. |
| Observed source and property target value | The cached source type and the owning dependency property's observed value. For a composite child, the target value is still the property's value, not that child's intermediate transfer value. |
| Cached path steps | The cached resolved prefix and first unresolved step, when available, with owner type, accessor kind and declared value type. Step numbers in the UI start at one. |
| Current binding details | Configured source kind, mode, converter type and available resolved owner/member. `Default` remains the configured mode; it does not claim an independently resolved effective mode. |
| Current validation | Current rule type, validation step, safely formatted scalar error content and exception type. Custom error objects are identified by type rather than evaluated for display. |
| Historical WPF evidence | Earlier notifications, with timestamps, WPF codes, status at notification and available source/member/owner facts. They are not the current failure cause. |

A matching root source identity on a historical event does not establish that a nested object or its value is unchanged. A missing-member, null-item or conversion notification can remain visible after recovery. Preview trace messages are also labeled historical and remain separate from current diagnostics. Re-inspecting a repaired preview binding replaces its current errors without deleting that trace history. Live issue recovery requires a later successful observation of the same expression; a missing poll, removed binding, unloaded target or ended session has a different meaning.

A composite parent's raw error status can coexist with active children and a successfully displayed value. Tested `PriorityBinding` and `MultiBinding` cases retain an earlier parent `UpdateTargetError` after a child succeeds. The inspector preserves that raw status and the children's separate observations. Inactive, unused PriorityBinding candidates do not establish failure of the selected child. Select a child declaration to inspect its own path and validation rows.

## What cached path state can prove

For `Customer.Name`, WPF may still retain the resolved `Customer` accessor and show that the next step is unresolved. The panel can identify that boundary without invoking `Customer` again. It cannot always say whether the failed step has a missing member or a null intermediate owner: WPF can erase the failed owner's cached reference. That qualification stays beside the first unresolved step. A specific missing-CLR-property explanation is limited to supported simple paths whose observed source surface proves it; it is not applied to an arbitrary nested or dynamic path.

Path inspection reads cached WPF fields on the target's dispatcher. It does not invoke application source getters, converters, custom descriptor metadata, `UpdateTarget` or `UpdateSource`, and does not reconstruct a raw source value. Declared value types describe cached accessor metadata, not current values. The fallback indicator reports WPF's cached flag, not why fallback was chosen. Ordinary application binding activity can still execute application code; the inspector does not stop it.

The adapter uses private WPF implementation fields, guarded by assembly major version 8–10, exact field/type/enum shapes and before/after state checks. It is not a supported public WPF path-inspection API. Unsupported versions or shapes, absent CLR workers, XPath and inconsistent observations return an explicit unavailable reason. Public binding status and other supplied details can remain useful when cached steps are unavailable. Pending, deferred, inactive or updating bindings suppress downstream interpretation rather than displaying an earlier evaluation as current. Path, child, history, validation and text budgets report omissions.

Validation capture also reads bounded cached entries instead of invoking composite aggregate getters that can traverse every child. Each expression's validation rows remain separate from its children's rows. Unsupported validation cache shapes report incomplete coverage. The implementation guards account for the [WPF binding expression implementation](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Data/BindingExpressionBase.cs); future WPF implementation changes can require adapter updates.

## Refresh and lifetime

Live inspection can poll automatically; with automatic refresh disabled, refresh after application changes. Preview inspection and **Update snapshot** observe the existing preview instance; **Refresh** rebuilds its preview state. Each accepted explanation must match the current session or render, node, property and expression identities and revision. Selection changes, source/render invalidation, debugger pause, disconnect and property operations clear or withhold details until a matching observation arrives. Late replies cannot restore a previous selection, including one changed away and back. A removed selected expression is not silently replaced by another binding.

Older hosts can omit the optional details or child observation. The pane explains the missing coverage rather than inferring it from a parent's path or a similarly named property. Complete current null/missing/conversion attribution, raw source values and failures occurring entirely between polls remain outside this capability.

## Implementation map

- `WpfStudio.Wpf.Diagnostics/BindingReader` and `BindingEvidenceCollector` provide shared current observations and bounded historical notifications to preview and live inspection.
- `BindingPathStateReader` implements the guarded cached-field adapter; protocol details keep path state optional.
- `BindingSourceCatalog` associates observations with the actual root/child expression and declaration identities.
- `Features/BindingDiagnostics` contains the shared presentation model and panel. Designer and Inspection parents own request, selection, draft and lifetime guards.

See the [runtime bootstrap design](xaml-runtime-bootstrap.md) for launch, dispatcher and retention boundaries, and the [DevTools design ledger](xaml-devtools-design.md#full-goal-acceptance-ledger) for remaining work.
