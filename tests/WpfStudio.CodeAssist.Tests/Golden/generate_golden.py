"""Writes golden.json: what the Python training code computes, for GoldenParityTests to compare with the C# port.

Run it with the sandbox's interpreter whenever a model version or feature version is added:

    <sandbox>/.venv/Scripts/python.exe -X utf8 generate_golden.py <sandbox> <WpfStudio.CodeAssist/Models>
"""
import hashlib
import json
from pathlib import Path
import sys

sandbox = Path(sys.argv[1]).resolve()
models = Path(sys.argv[2]).resolve()
sys.path.insert(0, str(sandbox))

import numpy as np  # noqa: E402
from tokenizers import Tokenizer  # noqa: E402

from sandbox.compact import CompactRunner  # noqa: E402
from sandbox.evaluate_lines import Generator, editor_stops, offered_text  # noqa: E402
from sandbox.features import FEATURE_SPECS, make_features  # noqa: E402
from sandbox.line_corpus import line_cases, load_sources  # noqa: E402

CONTEXTS = [
    ("", ["Add", "Clear"]),
    ("names.", ["Add", "AddRange", "Clear", "Contains", "Count", "IndexOf", "Insert", "Remove", "Sort", "ToArray"]),
    ("// Clear out the array\nnames.", ["Add", "AddRange", "Clear", "Contains", "Count", "IndexOf", "Insert", "Remove", "Sort", "ToArray"]),
    ("var names = new List<string>();\nnames.Add(\"a\"); names.Add(@\"b\"\"c\");\nint n = 3 + 4.5;\n/* Remove the\n   old entries */\nnames.", ["Add", "Remove", "RemoveAll", "Count", "Name", "URLParser", "_private", "x1"]),
    ("public sealed class Person\n{\n    public string Name { get; set; }\n    public int Age;\n    // Écrire le nom déjà connu\n    void Run(char c = 'x') { this.", ["Name", "Age", "Run", "ToString", "Équipe", "GetHashCode", "Equals"]),
    ("foreach (var item in items.Where(x => x.IsReady).Select(x => x.Value))\n{\n    Console.", ["WriteLine", "Write", "ReadLine", "Beep", "Clear", "Error", "Out"]),
    ("string text = \"unterminated\nvar s = $\"{a}\";\ns.", ["Length", "Trim", "Split", "Substring", "StartsWith", "ToUpperInvariant"]),
    (" ".join(f"token{i} ." for i in range(150)) + "\n// " + " ".join(f"word{i}" for i in range(70)) + "\nvalue.", ["token149", "word69", "Word", "Value", "Other"]),
]

LINE_TEXTS = [
    "",
    "public sealed class Person\n{\n    public string Name { get; set; }\n",
    "var text = \"héllo wörld\"; // ünïcode ✓ and 😀\r\n\tint x = 10_000;\r\n",
    "it's they're we've I'm you'll he'd 'quoted'",
    "   spaces   then\n\n\ntabs\t\t\tend   ",
    "List<string> a = new(); <BOS> a <EOS> b <PAD>",
    "foreach (var item in items.Where(x => x.IsReady))\n{\n    Console.WriteLine($\"{item.Name}: {item.Value:0.00}\");\n}\n",
]

LINE_CONTEXTS = [
    "",
    "using System;\n\nnamespace Demo;\n\npublic sealed class Person\n{\n    public string Name { get; set; }\n",
    "public sealed class Person\n{\n    public string Name { get; set; }\n    public string Email { get; set; }\n    public int Age { get; ",
    "foreach (var item in items)\n{\n    Console.WriteLine(item.Name);\n    Console.",
    "// Clear out the array\nnames.",
    "private readonly List<string> _items = new();\n\npublic void Add(string value)\n{\n    _items",
    "public sealed class Person\n{\n\tpublic string Name { get; set; }\n\tpublic string Email { get; ",
    "class Demo\n{\n\tvoid Run(CancellationToken cancellationToken = default)\n\t{\n\t\ttry\n\t\t{\n\t\t}\n\t\tcatch ",
    "class Demo\n{\n\tvoid Run()\n\t{\n\t\t",
]


def sparse(row):
    return [[int(i), float(row[i])] for i in np.flatnonzero(row)]


def member_cases(version, folder):
    spec = FEATURE_SPECS[version]
    runner = CompactRunner(folder, spec)
    cases = []
    for code, names in CONTEXTS:
        feed, _, _ = make_features(code, names, spec)
        scores = runner.session.run(["scores"], feed)[0].reshape(-1)
        cases.append({"code": code, "names": names, "context": sparse(feed["context"][0]),
                      "candidates": [sparse(row) for row in feed["candidates"]],
                      "auxiliary": [[float(v) for v in row] for row in feed["auxiliary"]],
                      "scores": [float(v) for v in scores]})
    return cases


def tokens(folder):
    tokenizer = Tokenizer.from_file(str(folder / "tokenizer.json"))
    return [{"text": text, "ids": tokenizer.encode(text).ids,
             "decoded": tokenizer.decode(tokenizer.encode(text).ids, skip_special_tokens=False)} for text in LINE_TEXTS]


def lines_v1():
    spec = json.loads((models / "lines" / "1.0.0" / "manifest.json").read_text(encoding="utf-8"))["lines"]
    generator = Generator(sandbox / ".sandbox-cache" / "compact-csharp-lines-v1")
    result = []
    for code in LINE_CONTEXTS:
        run = generator.generate(code, 48, spec["contextCacheWeight"])
        result.append({"code": code, "text": offered_text(generator, *run, 0.0), "finished": run[2]})
    return result


def gated_lines(folder, shipped):
    assert hashlib.sha256((folder / "model.onnx").read_bytes()).digest() == hashlib.sha256((shipped / "model.onnx").read_bytes()).digest()
    generator = Generator(folder)
    spec = generator.spec
    # Real code from public repositories the model never trained on, so the gate and word trimming get exercised.
    rows = load_sources(sandbox / ".sandbox-cache" / "corpus" / "multi-lines.jsonl")
    replay = [case["code"][-800:] for case in line_cases(rows, "validation", 20)] + [case["code"][-800:] for case in editor_stops(rows, "validation", 40)]
    result = []
    for code in LINE_CONTEXTS + replay:
        run = generator.generate(code, 48, spec["context_cache_weight"])
        _, probabilities, ended, _ = run
        complete = ended and all(p >= spec["token_confidence"] for p in probabilities)
        result.append({"code": code, "text": offered_text(generator, *run, spec["token_confidence"]), "finished": complete})
    return result


v2 = sandbox / ".sandbox-cache" / "experiments" / "code-assist-lines-2.0.0"
v3 = sandbox / ".sandbox-cache" / "experiments" / "code-assist-lines-3.0.0"
golden = {
    "members": {version: member_cases(int(version[0]), models / "members" / version) for version in ("1.0.0", "2.0.0")},
    "tokens": {version: tokens(models / "lines" / version) for version in ("1.0.0", "2.0.0", "3.0.0")},
    "lines": {"1.0.0": lines_v1(), "2.0.0": gated_lines(v2, models / "lines" / "2.0.0"), "3.0.0": gated_lines(v3, models / "lines" / "3.0.0")},
}

out = Path(__file__).with_name("golden.json")
out.write_text(json.dumps(golden, ensure_ascii=False, separators=(",", ":")), encoding="utf-8", newline="\n")
print(out, out.stat().st_size)
