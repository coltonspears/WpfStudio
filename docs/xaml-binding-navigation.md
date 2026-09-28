# Binding declaration navigation

The **Bindings** tab in the XAML preview and live inspector has a **Binding declaration** selector and **Show binding XAML** action. Select a root binding or a child of a `MultiBinding` or `PriorityBinding` to highlight its complete declaration. Live binding issues also expose this action. The existing **Show XAML** action still opens the selected element.

Inline expressions select the attribute value, including its original entity encoding and line breaks. Object-element bindings select the complete `<Binding>`, `<MultiBinding>`, or `<PriorityBinding>` element. A style setter or shared binding resource uses the declaration's own source position when that source can be verified. Repeated paths on adjacent attributes do not affect the selection.

Navigation does not apply or reset a temporary property edit, change a binding, or write source. Selecting a declaration preserves the property edit draft.

The same exact declaration selection drives the shared [binding explanation panel](xaml-binding-diagnostics.md). Current preview diagnostics and live issues select an observed expression by ID after re-inspection, not by matching its path or property name. A root or composite child's selection survives refresh only while its identities still match; a replacement is reported rather than silently selected. Explanation availability does not establish source provenance, and source availability does not establish a failure's cause.

## Verification

The runtime catalog records weak identities for the actual `BindingExpressionBase` and its `ParentBindingBase`, with a bounded tree of composite children. It reads WPF's loader source metadata on the binding object. It does not infer origin from the target element, binding path, or property name. A same-path binding installed later by application code is a different expression and fails validation.

Live navigation checks the selected node, observed dependency property, current binding identities, and declaration source. It then uses the existing loaded-module, compiled-resource, portable/embedded PDB, stable build-input checksum, file lease, and exact editor-buffer verification. After asynchronous verification, it checks the actual binding again before synchronously activating the editor and selecting the expression. Buffer version, document identity, session, selection and workspace guards reject stale operations.

Source preview retains the authored XML positions through preview transformations rather than using positions in serialized transformed markup. It only accepts an unchanged authored declaration in the current preview document. Render identity, source text/version and actual binding identity are checked before navigation. Relative resources keep their original base URI.

## Limits

- WPF does not provide binding-object source metadata for the tested instantiated control/data-template clones, code-created bindings, or same-path replacements. Their source stays unavailable.
- Live navigation requires a verified compiled resource-to-document mapping. Classless dictionaries, missing/mismatched symbols, uncaptured build inputs, and changed source cannot be opened by this action.
- Source preview does not verify bindings loaded from external dictionaries. Compiled-preview declaration navigation is not yet connected to the live build-source verification chain.
- A loader hint is matching-source evidence, not general proof of runtime construction provenance. Application code can load custom XAML with a supplied base URI; this feature does not authenticate arbitrary loaders.
- Navigation is bounded to 65 declarations per binding and 256 per selected element, with additional text limits. The selector reports omitted declarations.

This capability complements live binding diagnostics and semantic source analysis. It does not add shared-style/resource editing or automatic correction of a running binding.
