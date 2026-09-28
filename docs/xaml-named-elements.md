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
- Separate local scopes for standard WPF templates, including `DataTemplate`, `ControlTemplate` and `ItemsPanelTemplate`. Identical names in sibling templates do not become duplicate page names. A template object's own name is withheld from navigation; it is never treated as a name declared inside that template's content.

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

**Find References** connects a supported name declaration with its `Binding.ElementName` values. For a page with `x:Class`, a verified WPF-generated field also connects authored C# uses, including when the query starts in C#. Results retain source hashes and project context so navigation can reject changed text. Template-local names have independent identities and do not imply page fields.

**Rename** uses the existing change preview. It can update the declaration, its supported name references and verified authored C# field uses as one buffer transaction. Cancel preserves the original buffers; **Undo workspace edit** restores the complete transaction. Files are saved only through the ordinary save commands. Linked XAML contexts must agree on the declaration, types, scope and replacement spans. The proposed XAML is re-indexed to check that edited and untouched references retain their intended declarations; Roslyn checks the temporary C# result for new compiler conflicts.

The field bridge requires more than a matching name. To establish the initial compiler baseline, it checks the actual page class and element type, a unique instance field in the current generated document, WPF producer metadata, the mapped name-attribute line and the compiler's SHA1 or SHA256 source checksum. Bounded reads compare the original XAML bytes, including encoding/BOM, with that checksum, then compare their decoded text with the current buffer. Unsupported, stale or ambiguous page-field evidence blocks the entire name rename. A XAML-origin reference query can still return its verified XAML subset with coverage warnings; a generated-field query starting in C# cannot supply verified source rows without that bridge. A template-local or classless name can be refactored without a page field.

### Live compiler fields after a reviewed rename

Requesting a rename prepares its review without changing compiler state. After **Apply changes**, the worker commits the synchronized authored buffers and derived generated-field metadata together in its in-memory Roslyn solution. C# completion, diagnostics, definitions, references and a subsequent reviewed rename can then use the new field name without a build. `.g.cs` and `.g.i.cs` files remain unchanged on disk and are never returned as authored edits.

The projection retains the original compiler checksum proof and exact generated-file hashes. It re-derives a bounded sequence of checked name renames; it does not accept replacement generated code or rewrite checksum pragmas. Every linked project must be covered. Projected navigation rechecks the current declaration, field type and namescope metadata, so an unsaved change introducing a custom `INameScope` can withhold a formerly valid definition. An ordinary authored C# field that reuses a retired generated name remains a separate symbol.

**Undo workspace edit** reconciles the final buffer contents with the recognized rename states. Discarding XAML selects the saved state; discarding C# alone does not roll the XAML field back to conceal a real compiler error. A language-worker **Restart** replays accepted plans against freshly loaded, unchanged compiler baselines, including saved and closed XAML that matches a recognized state. If loading the saved project legitimately regenerates WPF output, every current page field must instead pass the ordinary compiler bridge in every owning project before that fresh baseline retires the old projection history. Unverifiable changed output rejects replay rather than being overwritten. This is worker restart within the current IDE session, not persistent history across an IDE restart. Explicit workspace load/reload or a configuration change clears the plans.

Arbitrary XAML edits are not generated live. If an unsaved buffer differs from every exact verified rename state, the worker restores its original generated baseline and reports that projection is unavailable. Undo to a recognized state, or **save, build and reload** to establish a new baseline. Changed or unreadable saved/compiler inputs can also prevent reconciliation; the status explains the need to refresh. A failed semantic synchronization does not undo the already reviewed authored-buffer transaction.

Projection is bounded to 32 rename steps and 32 generated documents per page model, 32 projected pages per workspace, 32,000,000 retained characters per model and 64,000,000 across the workspace. Reaching a limit requires a fresh build/reload baseline. These limits support a checked rename history; they do not provide general XAML compilation as you type. Baseline field verification also has a 32,000,000-character aggregate work budget to bound repeated source/generated-text checks. Verified coverage is recorded in [VALIDATION.md](../VALIDATION.md).

Name rename requires complete supported consumers in the selected document and every linked context. An unresolved name, unsupported name consumer or incomplete scan can prevent the operation. Runtime registration, `FindName`/reflection strings and application-specific lookup cannot be inferred and are called out in the coverage notes; matching strings are not rewritten automatically.

## Remaining name features

Direct name and element-type edits, new or removed named elements, and arbitrary XAML changes outside the bounded reviewed rename states still need continuous compiler-state updates. The existing property/event refactoring features have separate coverage.

`Storyboard.TargetName`, template setter/trigger name references, property-element name declarations and `x:Reference` need their own complete scope rules. Qualified runtime-name attributes such as `FrameworkElement.Name` and qualified `Binding.ElementName` forms (inline or object syntax) are also unsupported by this name service. Existing limited `x:Reference` binding-source inference remains separate from this `ElementName` service. WPF's [x:Name documentation](https://learn.microsoft.com/en-us/dotnet/desktop/xaml-services/xname-directive) explains generated fields and the runtime-name alias.

See the [feature tour](xaml-feature-tour.md) for actual app captures and [validation record](../VALIDATION.md) for the tested payload and remaining acceptance work.
