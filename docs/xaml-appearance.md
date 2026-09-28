# Inspect property appearance

Select an element in **XAML Designer** or **Live XAML**, open **Appearance**, and choose a property. The selector shares the selection in **Properties**. **Refresh appearance** reads another observation without applying an edit or changing the value draft.

The panel shows:

- The effective dependency-property value, WPF base value source, and expression, animation and coercion flags. Complex values are identified by type.
- Relevant declarations from the applied style, its `BasedOn` chain and supported control-template triggers. These are candidates; their presence does not establish which trigger is active or which setter supplies the value.
- Keys in available element, ancestor, style, template, application and merged resource dictionaries. Values are not opened, so enumerating keys does not instantiate deferred resources. Available scopes do not reconstruct WPF's complete lookup order.
- The key in a current local `DynamicResource` expression, where available. This does not identify its resolving dictionary.
- Matching historical `StaticResource` resolution notifications captured since the inspected launch or preview render. A later dictionary or property change does not rewrite that history. Missing history does not prove that no resource was used.

Source positions, when supplied by WPF, are unverified hints. Appearance does not navigate to or edit those declarations. Existing **Show XAML** and reviewed local property edits retain their separate source-verification requirements.

Appearance is read on demand while its tab is active or when explicitly refreshed. Selection changes, preview revisions, live tree refresh, debugger pause and disconnect invalidate stale observations. Live inspection requires a current observed property identity and an agent advertising Appearance support; read-only and complex properties can be inspected too.

Captures limit time, declarations, scope depth, key counts and text. Oversized dictionaries are skipped before copying their keys. Historical capture keeps bounded weak associations instead of retaining application objects. The panel reports omitted details. It does not inspect resource values, invoke custom formatting, or evaluate a separate binding path to guess resource provenance. Reading the selected dependency property's effective value still follows WPF's normal property system.
