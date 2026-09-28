# Named elements in the XAML editor

WpfStudio uses the owning project's current compilation to connect named elements with `Binding.ElementName`. This assistance runs in the workspace worker, without constructing controls or evaluating application code.

```xml
<TextBox x:Name="CustomerName" Text="Ada" />
<TextBlock Text="{Binding ElementName=CustmerName, Path=Text}" />
```

The editor warns that `CustmerName` has no declaration in the authored scope. Completion offers matching names, including forward declarations. When a spelling correction has a unique closest candidate within its distance and work limits, **Ctrl+.** offers the checked edit. The correction changes the buffer and supports **Undo workspace edit**; it does not save the file automatically.

After correction, **F12** on `CustomerName` navigates to its declaration. Hover describes the named object's type and authored scope. The binding path then uses that same resolved object type, so `Path=Text` receives ordinary member completion and diagnostics.

## Supported declarations and references

- `x:Name`, including alternate prefixes mapped to the XAML language namespace.
- A type's runtime-name property, proven from framework/compiler metadata. This normally means `Name` on WPF elements; an unrelated custom property merely called `Name` does not declare a name.
- Inherited runtime-name metadata and the actual root type named by `x:Class`, when that type resolves and derives from the lexical root type.
- Inline bindings such as `{Binding ElementName=CustomerName, Path=Text}` and object bindings such as `<Binding ElementName="CustomerName" Path="Text" />`.
- Literal quoted markup arguments and the whitespace trimming used by `ElementName`. Completion replaces the whole current name, including an entity-encoded suffix, while preserving surrounding quotes, spaces and binding options.
- Separate local scopes for standard WPF templates, including `DataTemplate`, `ControlTemplate` and `ItemsPanelTemplate`. Identical names in sibling templates do not become duplicate page names. The template object's own name is withheld from `ElementName` navigation; it is never treated as a name declared inside that template's content. Its possible C# field has the separate rules described below.

The same name resolution supplies editor diagnostics, project-wide scans, completion, hover, definitions and binding-path inference. Unsaved XAML is analyzed as the current buffer; linked files use the selected owning project.

## Diagnostics and uncertainty

| Code | Meaning |
| --- | --- |
| `XAMLNAME001` | A literal declaration contains an invalid name. |
| `XAMLNAME002` | A name has duplicate declarations in a proven authored scope. |
| `XAMLNAME003` | One element declares both `x:Name` and its runtime-name alias. |
| `XAMLNAME004` | An `ElementName` has no declaration in a known authored page scope. |

The first three are declaration errors. A missing authored name is a **warning**: application code can register names, and WPF can search outer runtime scopes. A name missing from a template therefore does not become a definitive missing-name diagnostic. This distinction follows [WPF namescope semantics](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-xaml-namescopes) and the runtime's [ElementName lookup](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/Data/ObjectRef.cs).

Custom or explicit namescopes, unresolved types, shared resource contexts, unsupported declarations and conflicting binding source selectors can prevent static resolution. Ignored/design-only content is excluded. These uncertain scopes withhold candidates, definitions and missing-name warnings where resolution cannot be proved; they do not by themselves mean that analysis exceeded its limits. Duplicate or invalid declarations can still be reported where the declarations are known.

Incomplete XML postpones name diagnostics, definitions and spelling fixes. A bounded index also stops before returning partial name candidates or corrections. Its per-document limits are 1,000,000 characters, 32,768 elements, 65,536 attributes, depth 256, 8,192 declarations and 8,192 `ElementName` references. A declaration longer than 512 characters is an analysis limit, not an invalid-name error. The editor and project scan report incomplete analysis when these limits are reached.

Completion returns at most 250 names. Spelling correction considers names up to 128 characters, requires a unique best match within edit distance one for names of three characters or fewer, or two for longer names, and has a shared comparison-work budget. A tie or an exhausted search produces no correction. Name lookup is case-sensitive. An empty name alone does not trigger an invalid-name error while typing.

## References and reviewed rename

**Find References** connects a supported name declaration with its `Binding.ElementName` values. For an evaluated WPF page with `x:Class`, current-source field declarations also connect authored C# uses, including when the query starts in C#. Results retain source hashes and project context so navigation can reject changed text. Template-local names have independent identities and do not imply page fields.

**Rename** uses the existing change preview. It can update the declaration, its supported name references and matching authored C# field uses as one buffer transaction. Cancel preserves the original buffers; **Undo workspace edit** restores the complete transaction. Files are saved only through the ordinary save commands. Linked XAML contexts must agree on the declaration, types, scope and replacement spans. The proposed XAML is re-indexed to check that edited and untouched references retain their intended declarations; Roslyn checks the temporary C# result for new compiler conflicts.

The connection requires a unique current page field with the expected type and synthetic document identity, together with the exact current authored name span and owning project. A spelling match alone is insufficient. Unsupported, stale or ambiguous field evidence blocks the entire name rename. A XAML-origin reference query can still return its established XAML subset with coverage warnings. A template-local or classless name can be refactored without a page field. The older checksum-backed compiler bridge remains a fallback for actual compiler output outside the current-page model; its checksum checks do not authenticate editor-derived declarations.

<a id="live-compiler-fields-after-a-reviewed-rename"></a>

### Current XAML fields while editing

The worker derives C# page partials from current XAML for evaluated `Page` items in C# WPF projects. Direct changes to names, element types, added elements and removed elements refresh C# completion and diagnostics. **F12** on a derived field selects the exact current XAML name, including its original entity spelling. A new evaluated page can supply fields before its first build. A newly created file still needs workspace evaluation before it belongs to a project; reload when source membership changes.

For example, changing `<TextBox x:Name="Input"/>` to `<Button x:Name="SaveButton"/>` introduces a `Button` field named `SaveButton`. C# that still uses `Input` receives an ordinary missing-member error; C# completion on `SaveButton` uses `Button` members. This field update does not rewrite existing C# references. Use reviewed **Rename** when the intended change should update references together.

The semantic page supports resolved WPF element and resource-dictionary roots, supported custom subclasses, `x:Name`, verified runtime-name aliases, a name on the root itself, and C# `x:FieldModifier` values `private`, `internal`, `public`, `protected` and `protected internal`. Field generation and `ElementName` lookup have different boundaries: a named resource object can produce a C# field without belonging to its containing page's binding namescope. A nested `INameScope` object's own name can produce a field, while its descendants do not; ordinary template content remains separate. These additional C# fields do not expand name-refactoring coverage into unsupported resource/template consumers.

The worker retains authored C# and actual MSBuild output as inputs, then creates a separate derived solution for editor queries. It replaces an existing generated page document only after verifying WPF producer metadata, the component connector and a unique mapped evaluated XAML path. A previous source checksum is not a prerequisite for current-buffer semantics. The replacement removes stale field assignments and event-wiring bodies, supplies inert initializer/connector methods, and preserves authenticated compiler style-connector/delegate-helper signatures. No view, converter, getter or application assembly is executed by this process. `.g.cs` and `.g.i.cs` files remain unchanged on disk and never appear as authored edits.

Open XAML and C# changes, closed-source refresh, reviewed edits, undo, discard and worker restart rebuild the model from current sources. Discarding only C# can therefore reveal a real mismatch with the still-edited XAML. Linked pages receive independent fields in each owning project; their synthetic document paths cannot silently link different compiler contexts. A reviewed rename commits its checked authored snapshots and derived fields together. Older compiler-baseline rename plans are rejected with an instruction to reopen the review; no bounded rename history is required for current fields.

Malformed XAML withholds its fields and reports incomplete coverage. Unresolved types, duplicate/invalid names, unsupported generic declarations, `x:Code`, `x:Subclass`, nested page classes and custom serialization can also limit coverage. Positively identified stale compiler documents are withheld even when current source is missing or over budget. Unauthenticated generator documents are preserved with an incomplete-coverage explanation. Classless dictionaries are complete no-ops for page-field generation; `ApplicationDefinition` items are outside this model.

The model is bounded to 512 class-bearing page contexts and 16,000,000 source characters per workspace snapshot. Classless resource dictionaries do not consume the class count. Per-page limits are 1,000,000 characters, 32,768 elements, 65,536 attributes, depth 256 and 8,192 fields; field names are limited to 512 characters. Compiler-document ownership checks are bounded to 2,000,000 characters and 16,384 directives per document. These bounds have explicit coverage statuses. Current validation results and remaining acceptance work are recorded in [VALIDATION.md](../VALIDATION.md).

Editor-derived declarations are authoring assistance. A normal build still compiles saved XAML, and preview/runtime inspection retains its separate source and assembly verification. Current field completion does not establish runtime object identity or perform Hot Reload.

Name rename requires complete supported consumers in the selected document and every linked context. An unresolved name, unsupported name consumer or incomplete scan can prevent the operation. Runtime registration, `FindName`/reflection strings and application-specific lookup cannot be inferred and are called out in the coverage notes; matching strings are not rewritten automatically.

## Remaining name features

Broader generic/custom-serialization forms, application-definition semantics, unbuilt style/event helper inference, and additional languages still need support. The existing property/event refactoring features have separate coverage. Larger class-heavy solutions and end-to-end typing latency need broader measurement.

`Storyboard.TargetName`, template setter/trigger name references, property-element name declarations and `x:Reference` need their own complete scope rules. Qualified runtime-name attributes such as `FrameworkElement.Name` and qualified `Binding.ElementName` forms (inline or object syntax) are also unsupported by this name service. Existing limited `x:Reference` binding-source inference remains separate from this `ElementName` service. WPF's [x:Name documentation](https://learn.microsoft.com/en-us/dotnet/desktop/xaml-services/xname-directive) explains generated fields and the runtime-name alias.

See the [feature tour](xaml-feature-tour.md) for actual app captures and [validation record](../VALIDATION.md) for the tested payload and remaining acceptance work.
