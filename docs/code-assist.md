# Code Assist

An experimental replacement for the parts of Visual Studio IntelliCode this IDE can use: starred member completions and whole-line suggestions, from small models that run on the CPU. Nothing leaves the machine.

It is **off by default**. Turn it on in **Settings > Experimental > Code Assist**. While it is off, no model file is read and the ONNX runtime stays out of the process.

## What it does

**Member ranking.** After a dot in C#, the members you are likely to want move to the top of Roslyn's completion list with a star, and the first is selected. Tab or Enter takes it. Typed punctuation does not, so `x.)` still types `)`. Only Roslyn's own entries are shown and inserted, through Roslyn's own change: the model only chooses an order. If ranking takes more than 150 ms (usually only while a model loads) or fails, the popup opens in Roslyn's order.

**Line suggestions.** A separate switch, off even when Code Assist is on. After a pause at the end of a C# line, a grey continuation appears after the caret, as far as the model is confident. Where it stops being sure, the suggestion stops too, and if it is unsure from the start nothing appears. Tab inserts it as one undoable edit. Typing, moving the caret or Esc dismisses it.

## Quality: read this before relying on it

**The member models** are useful within limits. On 2,012 held-out cases from 50 source files, `code-assist-members` scores:

| Version | Top-1 | Top-5 | MRR@5 | Reads comments |
| --- | --- | --- | --- | --- |
| 2.0.0 | 42.10% | 79.87% | 0.5610 | Yes |
| 1.0.0 | 41.95% | 80.72% | 0.5646 | No |

Replaying those cases through the C# path shipped here reproduces these numbers exactly. On the same cases the training sandbox measured Microsoft's model, through its approximate adapter, at 18.34% / 49.30% / 0.2896. That comparison is not a measurement of Visual Studio.

Those cases come from **the repository the models were trained on**, so they do not show how well the models work on other code. The corpus also offered a narrower member list than Roslyn does. Extension methods were not in it, so they are not ranked or starred: when they were, they pushed real members off the list. Scores compare names within one request and are never shown as confidence.

**The line model is quiet, and right about 3 times in 4 when it speaks.** Scored at the places ghost text can really appear (after a space, a bracket or an operator, or on an indented empty line, since letters and dots open the completion popup instead), on 1,500 stops from 679 files in 8 private repositories it never trained on:

| Version | Offers | Right when it offers | Wrong per 100 requests | Whole lines | Characters saved |
| --- | --- | --- | --- | --- | --- |
| 3.0.0 | 11.0% | 74.5% | 2.8 | 1.4% | 1.9% |
| 2.0.0 | 12.1% | 69.2% | 3.7 | 1.6% | 2.0% |
| 1.0.0 as first shipped | 82.2% | 1.5% | 81.0 | 1.1% | 0.4% |

"Right" means the suggestion is exactly what came next, ignoring whitespace, so a sensible but different name counts as wrong. It saves few characters: most of the time it declines, and most of what it knows is C# idiom such as `{ get; set; }`, `(Exception ex)` and `CancellationToken cancellationToken)`. The gap between 3.0.0 and 2.0.0 here is small enough to be noise, and those repositories have one author.

Version 3.0.0 is meant for code by other people. On AvalonEdit, at 600 stops, the recipe behind 3.0.0 trained without AvalonEdit for a fair test is right 69.5% of the time and saves 2.0% of characters, against 51.0% and 1.0% for 2.0.0. That is one repository and 50 to 60 offers, so it shows a direction rather than a measure. On 2 public repositories by the owner that neither version trained on, 3.0.0 saves less than 2.0.0, 2.4% against 3.2%, at about the same accuracy.

More general C# will not take it much further. On those same 2 repositories, 27% of stops are followed by a name already in the file, where 2.0.0 offers only 8% of the time and is right 92% of the time when it does, and another 11% by a name in neither the file nor the training data. Knowing which name comes next takes the project's symbols, which these models do not see.

## Versions

Each model version is a folder holding its files and a manifest:

```
src/WpfStudio.CodeAssist/Models/
  members/1.0.0/   manifest.json  model.onnx
  members/2.0.0/   manifest.json  model.onnx
  lines/1.0.0/     manifest.json  model.onnx  tokenizer.json
  lines/2.0.0/     manifest.json  model.onnx  tokenizer.json
  lines/3.0.0/     manifest.json  model.onnx  tokenizer.json
```

Models are named `code-assist-<kind>` and shown with their version, such as `code-assist-members 2.0.0`. The settings page offers each installed version plus **Latest**, which is the default. A saved version that is no longer installed falls back to the newest.

Version numbers mean:

- **Major:** the inputs changed (`featureVersion`, the tokenizer or the graph), so the model needs matching code in `WpfStudio.CodeAssist`.
- **Minor:** retrained weights with the same inputs.
- **Patch:** the manifest changed, not the weights.

The manifest records provenance, evaluation and limitations, which the settings page shows, plus a SHA-256 for every file. A model whose files do not match is refused when it loads, and its status appears on the settings page. A folder whose name disagrees with its manifest, or whose `formatVersion` this build cannot read, is not offered, and other versions keep working.

### Adding a version

1. Train and export the model in the sandbox described below.
2. Add `Models/<kind>/<version>/` with the files and a manifest. `.gitattributes` keeps line endings out of these files, so the hashes hold on any checkout.
3. If the inputs changed, add the new `featureVersion` to `MemberFeatures` or `LineGenerator`. `MemberRanker.Load` and `LineGenerator.Load` refuse versions they cannot compute, because a model fed the wrong inputs still runs and ranks by noise.
4. Run `tests/WpfStudio.CodeAssist.Tests/Golden/generate_golden.py` and add the version to `GoldenParityTests`. The models learned exact hash buckets and token ids, so any drift from the training code lowers quality without an error, and these tests are what catches it.

## Where the models come from

They were trained in a separate sandbox, `intellicode`. The manifests record each dataset's SHA-256. No Microsoft weights or predictions were used. The member models and line model 1.0.0 learned from WpfStudio's own C# source.

Line model 2.0.0 learned from 8 of the owner's public repositories, was tuned on 2 other public ones it never trained on, and was tested on 8 private ones it also never trained on (`sandbox/multi_corpus.py`, `train_lines_v2.py`, `evaluate_lines.py`).

Line model 3.0.0 has the same tuning and test repositories. It read those 8 public repositories plus 3 MIT-licensed ones, about 9 times as much source: `dotnet/wpf`, `icsharpcode/AvalonEdit` and `MaterialDesignInXAML/MaterialDesignInXamlToolkit`. It was then fine-tuned on the owner's code alone, because 83% of that source is framework internals in an older style, and training on all of it without fine-tuning did worse on the owner's code (`multi_corpus.py --oss`, `train_lines_v2.py --tokenizer-own-weight 4 --finetune-epochs 12`). Their licenses are in `tools/licenses`.

**Only public repositories whose terms allow it may be trained on**, because these weights ship from a public repository and a line model can memorize text. Private ones are test data, reported as totals only.

**Microsoft's IntelliCode bundles must not be added here.** The sandbox also holds `all_line-completion2`, `csharp_intellisense-members-lstm2` and others for comparison. Their metadata limits them to the "Visual Studio IntelliCode Extension" and "Visual Studio Code IntelliCode Extension", and this repository is public.

## Design notes

`WpfStudio.CodeAssist` has no UI dependency. `CodeAssistEngine` owns the models, `CodeAssistPreferences` maps settings to it, `EditorViewModel.CompleteAsync` applies ranking, and `EditorLineSuggestion` draws the suggestion.

Inference runs **in the IDE process**, on the thread pool, one request at a time per model, with no thread pool inside ONNX Runtime. That keeps it simple and works without a loaded workspace, but a native crash in ONNX Runtime would take the IDE down rather than a worker. The library could move to a worker without changing how it is used, if that proves worth the extra protocol.

Before a line model reads code, carriage returns become bare newlines and leading tabs become four spaces, because that is nearly all it trained on. A space just before the caret is taken off, because a token owns the space before it, so the model predicts that space and the suggestion drops it again.

Only Windows x64's `onnxruntime.dll` is copied. The package's other platforms would add about 220 MB to every build output.

## Testing

```
dotnet test tests/WpfStudio.CodeAssist.Tests
```

`WPFSTUDIO_TEST_CODEASSIST_ONLY=1 dotnet test tests/WpfStudio.App.Tests` drives the real settings page, popup and editor, and writes screenshots to `artifacts/screenshots`.
