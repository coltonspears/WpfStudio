using System.Globalization;
using System.Net;
using System.Text;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.App.Features.Profiling;

/// <summary>A self-contained HTML report of an investigation: headline numbers, findings, the biggest owners and types,
/// and, when snapshots were compared, what changed. Opens in any browser and can be attached to a bug report.</summary>
public static class MemoryReport
{
    public sealed record Input(HeapSummary Summary, string SourceName, IReadOnlyList<FindingRow> Findings, IReadOnlyList<MemoryTypeRow> Types,
        IReadOnlyList<SnapshotCard> Snapshots, SnapshotCard? Current, SnapshotCard? Baseline, IReadOnlyList<ComparisonRow> Comparison, string ComparisonHeadline);

    public static string Html(Input input)
    {
        var s = input.Summary;
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Memory report</title><style>
            :root{--bg:#f6f7f9;--panel:#fff;--text:#1b1d22;--muted:#5d6370;--border:#e1e3e8;--accent:#4a5fd6;--danger:#d23f3f;--success:#178a5a;--warning:#b26b00;--track:#eceef2}
            @media (prefers-color-scheme:dark){:root{--bg:#18191d;--panel:#1f2026;--text:#e7e8ec;--muted:#9aa0ab;--border:#30323a;--accent:#8c9cff;--danger:#f06a6a;--success:#3fbf86;--warning:#e0a23c;--track:#2b2d34}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI",system-ui,sans-serif}
            main{max-width:1080px;margin:0 auto;padding:28px 16px 48px}h1{font-size:24px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 10px}
            .muted{color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px}
            .panel{background:var(--panel);border:1px solid var(--border);border-radius:10px;padding:12px 14px}
            .label{font-size:11px;font-weight:600;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}.value{font-size:22px;font-weight:600;margin:2px 0}
            .finding{border-left:4px solid var(--muted);margin-bottom:10px}.High{border-left-color:var(--danger)}.Medium{border-left-color:var(--warning)}.Low{border-left-color:var(--accent)}
            .finding h3{margin:2px 0 4px;font-size:15px}.tag{font-size:11px;font-weight:600;color:var(--muted)}
            table{width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--border);border-radius:10px;overflow:hidden}
            th,td{padding:7px 10px;text-align:left;border-bottom:1px solid var(--border);vertical-align:middle}th{font-size:11px;color:var(--muted);text-transform:uppercase}
            td.n,th.n{text-align:right;white-space:nowrap}.bar{height:6px;border-radius:3px;background:var(--track);min-width:80px}.bar i{display:block;height:100%;border-radius:3px;background:var(--accent)}
            .div{position:relative;height:8px;min-width:120px;background:linear-gradient(var(--border),var(--border)) center/1px 100% no-repeat}
            .div i{position:absolute;top:0;height:100%;border-radius:2px}.up{color:var(--danger);font-weight:600}.down{color:var(--success)}
            .stack{display:flex;height:12px;border-radius:6px;overflow:hidden;gap:2px}.ns{font-size:12px;color:var(--muted)}
            .wrap{overflow-x:auto}svg text{fill:var(--muted);font-size:11px}footer{margin-top:32px;font-size:12px;color:var(--muted)}
            </style></head><body><main>
            """);
        html.Append($"<h1>Memory report · {E(input.SourceName)}</h1>");
        html.Append($"<div class=\"muted\">{E(s.Runtime)} · {E(s.Architecture)} · captured {s.CapturedAt.LocalDateTime:G}" + (s.IsComplete ? "" : " · <b>incomplete heap data</b>") + "</div>");

        html.Append("<div class=\"grid\" style=\"margin-top:16px\">");
        Tile("Managed heap", Size(s.ManagedBytes), $"{s.ObjectCount:N0} objects · {s.Types.Count:N0} types");
        Tile("Kept alive", Size(s.ReachableBytes), $"{Share(s.ReachableBytes, s.ManagedBytes)} of the heap");
        Tile("Collectible now", Size(s.UnreachableBytes), "Garbage the next GC frees");
        Tile("GC roots", s.RootCount.ToString("N0", CultureInfo.CurrentCulture), $"{s.ReferenceCount:N0} references");
        Tile("Heap free space", Size(s.FreeBytes), "Gaps inside GC segments");
        html.Append("</div>");

        if (s.Generations is { Count: > 0 } generations)
        {
            var colors = new[] { "#3987E5", "#D95926", "#199E70", "#C98500", "#D55181", "#008300" };
            html.Append("<h2>Heap by generation</h2><div class=\"panel\"><div class=\"stack\">");
            var i = 0;
            foreach (var g in generations.Where(g => g.Bytes > 0))
                html.Append($"<span title=\"{E(MemoryLabels.GenerationName(g.Generation))}: {Size(g.Bytes)}\" style=\"flex:{g.Bytes};background:{colors[i++ % colors.Length]}\"></span>");
            html.Append("</div><div class=\"muted\" style=\"margin-top:8px;font-size:12px\">");
            html.Append(string.Join(" · ", generations.Where(g => g.Bytes > 0).Select(g => $"{E(MemoryLabels.GenerationName(g.Generation))} {Size(g.Bytes)}")));
            html.Append("</div></div>");
        }

        html.Append($"<h2>Findings ({input.Findings.Count})</h2>");
        if (input.Findings.Count == 0) html.Append("<div class=\"panel muted\">No leak or waste patterns were detected in this snapshot.</div>");
        foreach (var finding in input.Findings)
        {
            html.Append($"<div class=\"panel finding {E(finding.Severity)}\"><div class=\"tag\">{E(finding.CategoryText)}" + (finding.BytesText.Length > 0 ? $" · {E(finding.BytesText)}" : "") + "</div>");
            html.Append($"<h3>{E(finding.Title)}</h3><div>{E(finding.Summary)}</div><div class=\"muted\" style=\"margin-top:6px\">{E(finding.Guidance)}</div>");
            if (finding.Items.Count > 0)
            {
                html.Append("<ul style=\"margin:8px 0 0;padding-left:18px\">");
                foreach (var item in finding.Items.Take(10)) html.Append($"<li><b>{E(item.Label)}</b> <span class=\"muted\">{E(item.Detail)} · {E(item.BytesText)}</span></li>");
                html.Append("</ul>");
            }
            html.Append("</div>");
        }

        if (s.TopRetainers is { Count: > 0 } retainers)
        {
            var max = Math.Max(1, retainers.Max(r => r.RetainedBytes));
            html.Append("<h2>Biggest owners</h2><div class=\"wrap\"><table><tr><th>Object</th><th class=\"n\">Own</th><th class=\"n\">Retains</th><th></th></tr>");
            foreach (var r in retainers.Take(12))
                html.Append($"<tr><td><b>{E(MemoryLabels.ShortType(r.Type))}</b><div class=\"ns\">{E(r.Address)} · {r.RetainedCount:N0} objects kept alive</div></td><td class=\"n\">{Size(r.ShallowBytes)}</td><td class=\"n\">{Size(r.RetainedBytes)}</td><td>{Bar(r.RetainedBytes, max)}</td></tr>");
            html.Append("</table></div>");
        }

        var types = input.Types.Where(t => t.Count > 0).OrderByDescending(t => t.RetainedBytes).Take(25).ToArray();
        if (types.Length > 0)
        {
            var max = Math.Max(1, types.Max(t => t.RetainedBytes));
            html.Append("<h2>Types by retained size</h2><div class=\"wrap\"><table><tr><th>Type</th><th class=\"n\">Objects</th><th class=\"n\">Own</th><th class=\"n\">Retained</th><th></th></tr>");
            foreach (var t in types)
                html.Append($"<tr><td><b>{E(t.ShortName)}</b><div class=\"ns\">{E(t.Namespace)} · {E(t.Module)}</div></td><td class=\"n\">{t.Count:N0}</td><td class=\"n\">{Size(t.Bytes)}</td><td class=\"n\">{Size(t.RetainedBytes)}</td><td>{Bar(t.RetainedBytes, max)}</td></tr>");
            html.Append("</table></div>");
        }

        var series = input.Current is null ? Array.Empty<SnapshotCard>() : input.Snapshots.Where(x => x.SourceKey == input.Current.SourceKey).ToArray();
        if (series.Length > 1) html.Append("<h2>Managed heap across snapshots</h2><div class=\"panel\">").Append(Trend(series, input.Baseline)).Append("</div>");

        if (input.Comparison.Count > 0 && input.Baseline is not null)
        {
            var rows = input.Comparison.Take(40).ToArray();
            var max = Math.Max(1, rows.Max(r => Math.Abs(r.BytesDelta)));
            html.Append($"<h2>What changed</h2><div class=\"muted\" style=\"margin-bottom:8px\">{E(input.ComparisonHeadline)}</div>");
            html.Append("<div class=\"wrap\"><table><tr><th>Type</th><th class=\"n\">Objects</th><th class=\"n\">Change</th><th>Bytes change</th><th class=\"n\"></th></tr>");
            foreach (var r in rows)
            {
                var width = 50.0 * Math.Abs(r.BytesDelta) / max;
                var bar = r.BytesDelta == 0 ? "" : r.BytesDelta > 0
                    ? $"<i style=\"left:50%;width:{width.ToString("0.#", CultureInfo.InvariantCulture)}%;background:var(--danger)\"></i>"
                    : $"<i style=\"right:50%;width:{width.ToString("0.#", CultureInfo.InvariantCulture)}%;background:var(--success)\"></i>";
                var cls = r.BytesDelta > 0 ? "up" : r.BytesDelta < 0 ? "down" : "";
                html.Append($"<tr><td><b>{E(r.ShortName)}</b>" + (r.Badge.Length > 0 ? $" <span class=\"tag\">{E(r.Badge)}</span>" : "") + $"<div class=\"ns\">{E(r.Namespace)}</div></td>" +
                    $"<td class=\"n\">{E(r.CountsText)}</td><td class=\"n {cls}\">{E(r.CountDeltaText)}</td><td><div class=\"div\">{bar}</div></td><td class=\"n {cls}\">{E(r.BytesDeltaText)}</td></tr>");
            }
            html.Append("</table></div>");
        }

        if (s.CoverageNotes.Count > 0) html.Append("<h2>Coverage notes</h2><div class=\"panel muted\">").Append(string.Join("<br>", s.CoverageNotes.Select(E))).Append("</div>");
        html.Append($"<footer>Generated by WpfStudio on {DateTime.Now:G}. Sizes are managed heap bytes read from an immutable snapshot; the target was not modified and no collection was forced.</footer>");
        html.Append("</main></body></html>");
        return html.ToString();

        void Tile(string label, string value, string detail) =>
            html.Append($"<div class=\"panel\"><div class=\"label\">{E(label)}</div><div class=\"value\">{E(value)}</div><div class=\"muted\" style=\"font-size:12px\">{E(detail)}</div></div>");
    }

    private static string Trend(IReadOnlyList<SnapshotCard> series, SnapshotCard? baseline)
    {
        const double width = 1000, height = 160, pad = 28;
        var max = Math.Max(1, series.Max(x => x.Summary.ManagedBytes)) * 1.1;
        string X(int i) => (pad + (width - 2 * pad) * i / Math.Max(1, series.Count - 1)).ToString("0.#", CultureInfo.InvariantCulture);
        string Y(long v) => (height - pad - (height - 2 * pad) * v / max).ToString("0.#", CultureInfo.InvariantCulture);
        var svg = new StringBuilder($"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" role=\"img\" aria-label=\"Managed heap per snapshot\">");
        svg.Append($"<polyline fill=\"none\" stroke=\"var(--accent)\" stroke-width=\"2.5\" points=\"{string.Join(' ', series.Select((x, i) => X(i) + "," + Y(x.Summary.ManagedBytes)))}\"/>");
        for (var i = 0; i < series.Count; i++)
        {
            var card = series[i];
            var color = ReferenceEquals(card, baseline) ? "var(--warning)" : "var(--accent)";
            svg.Append($"<circle cx=\"{X(i)}\" cy=\"{Y(card.Summary.ManagedBytes)}\" r=\"5\" fill=\"var(--panel)\" stroke=\"{color}\" stroke-width=\"2.5\"><title>{E(card.Title)} · {E(card.SizeText)}</title></circle>");
            svg.Append($"<text x=\"{X(i)}\" y=\"{height - 8}\" text-anchor=\"middle\">{E(card.Title)} · {E(card.SizeText)}</text>");
        }
        return svg.Append("</svg>").ToString();
    }

    private static string Bar(long value, long max) =>
        $"<div class=\"bar\"><i style=\"width:{(100.0 * value / Math.Max(1, max)).ToString("0.#", CultureInfo.InvariantCulture)}%\"></i></div>";
    private static string Share(long part, long whole) => whole <= 0 ? "0%" : ((double)part / whole).ToString("P0", CultureInfo.CurrentCulture);
    private static string Size(long bytes) => E(MemorySize.Format(bytes));
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
}
