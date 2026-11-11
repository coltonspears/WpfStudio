using System.Runtime.CompilerServices;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Diagnostics;

namespace WpfStudio.CodeAssist;

/// <summary>What the user asked for. A null version means the newest one installed.</summary>
public sealed record CodeAssistOptions(bool Enabled, string? MemberModelVersion = null, bool LineSuggestions = false, string? LineModelVersion = null)
{
    public static CodeAssistOptions Off { get; } = new(false);
}

/// <summary>
/// The experimental Code Assist feature: ranks member completions and suggests line continuations with small local
/// models. While it is off nothing is loaded: no model file is read and the ONNX runtime stays out of the process. Models
/// load on first use, on a pool thread. A failure is reported in <see cref="Status"/> and leaves completion as Roslyn
/// returned it; this feature can never cost the user the completions they already had.
/// </summary>
public sealed class CodeAssistEngine(AssistModelCatalog catalog, PerfMonitor? perf = null) : IDisposable
{
    private readonly PerfMonitor _perf = perf ?? new PerfMonitor();
    /// <summary>How many names get a star. The evaluation's top-5 recall measures this list.</summary>
    public const int MaxRecommendations = 5;
    /// <summary>The most a line may add. A line that runs longer is usually the model repeating itself.</summary>
    public const int MaxLineTokens = 48;
    // Only the end of a long file matters: 100 tokens and 50 comment words, or 256 tokens for lines.
    private const int MaxMemberContextChars = 64 * 1024;
    private const int MaxLineContextChars = 8 * 1024;
    // The popup should not wait for this. Past the budget it opens with Roslyn's order, and the model stays loaded for next time.
    private static readonly TimeSpan RankingBudget = TimeSpan.FromMilliseconds(150);
    // What the rankers were trained and measured on. Extension methods were not among them, and they push real members off the list when ranked.
    private static readonly string[] MemberTags = ["Method", "Property", "Field", "Event", "Constant", "EnumMember"];

    private readonly ModelSlot<MemberRanker> _members = new("member ranking", MemberRanker.Load);
    private readonly ModelSlot<ILineGenerator> _lines = new("line", LoadLines);
    private volatile CodeAssistOptions _options = CodeAssistOptions.Off;

    public AssistModelCatalog Catalog { get; } = catalog;
    public bool RanksMembers => _options.Enabled;
    public bool SuggestsLines => _options is { Enabled: true, LineSuggestions: true };
    /// <summary>Raised on whichever thread loaded or failed to load a model.</summary>
    public event EventHandler? StatusChanged;

    public string Status
    {
        get
        {
            if (!_options.Enabled) return "Off. No model is loaded.";
            var parts = new List<string> { _members.Describe(Member()) };
            if (SuggestsLines) parts.Add(_lines.Describe(Line()));
            return string.Join(" ", parts);
        }
    }

    /// <summary>Applies a change of setting. Models no longer needed are released, and the ones that are load in the background.</summary>
    public async Task ConfigureAsync(CodeAssistOptions options)
    {
        _options = options;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        await Task.Run(() =>
        {
            bool changed = _members.Prepare(WantedMember);
            changed |= _lines.Prepare(WantedLine);
            if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the likely members first and marks them, when the completion list is for a member access. Any other list, and
    /// any failure, comes back as it went in.
    /// </summary>
    public async Task<CompletionResult> RankCompletionsAsync(string text, CompletionResult completions, CancellationToken token = default)
    {
        if (!RanksMembers || !IsMemberAccess(text, completions.Start)) return completions;
        var names = completions.Items.Where(IsMember).Select(MemberName).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count < 2) return completions;
        try { return Reorder(completions, await RankMembersAsync(text, completions.Start, names, token).WaitAsync(RankingBudget, token).ConfigureAwait(false)); }
        catch (TimeoutException) { return completions; }
    }

    /// <summary>The names ranked best first against the code before <paramref name="end"/>; empty while ranking is off or has failed. Only cancellation throws.</summary>
    public async Task<IReadOnlyList<RankedMember>> RankMembersAsync(string text, int end, IReadOnlyList<string> names, CancellationToken token = default) =>
        await RunAsync(_members, WantedMember, ranker => ranker.Rank(Tail(text, end, MaxMemberContextChars), names), token).ConfigureAwait(false) ?? [];

    /// <summary>What the line model would add at <paramref name="caret"/>, or null when it has nothing whole to offer. Only cancellation throws.</summary>
    public async Task<string?> SuggestLineAsync(string text, int caret, CancellationToken token = default)
    {
        return await RunAsync(_lines, WantedLine, generator =>
        {
            var line = generator.Generate(Tail(text, caret, MaxLineContextChars), MaxLineTokens, token);
            return (line.Finished || generator.OffersPartialLines) && WorthOffering(line.Text) ? line.Text : null;
        }, token).ConfigureAwait(false);
    }

    private static ILineGenerator LoadLines(AssistModel model) =>
        model.Manifest.Lines!.FeatureVersion == TransformerLineGenerator.SupportedFeatureVersion ? TransformerLineGenerator.Load(model) : LineGenerator.Load(model);

    // A failure stops here, so a request abandoned at the ranking budget cannot leave a fault nobody observes.
    // A model runs on a pool thread for as long as it takes, also after a caller stopped waiting at its budget, so the run is timed here and not at the call.
    private Task<TResult?> RunAsync<T, TResult>(ModelSlot<T> slot, Func<AssistModel?> model, Func<T, TResult?> run, CancellationToken token, [CallerMemberName] string operation = "") where T : class, IDisposable where TResult : class => Task.Run(() =>
    {
        long started = PerfMonitor.Now;
        try
        {
            var result = slot.Use(model, run, out bool changed);
            if (changed) StatusChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            slot.ReportFailure(exception);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }
        finally { _perf.RecordSince(PerfAreas.Assist, operation, started); }
    }, token);

    // A lone ";" costs more attention than it saves, and it is not counted as an offer when models are scored.
    private static bool WorthOffering(string text) => text.Count(character => !char.IsWhiteSpace(character)) >= 2;

    public void Dispose()
    {
        _options = CodeAssistOptions.Off;
        _members.Unload();
        _lines.Unload();
    }

    private AssistModel? Member() => Catalog.Resolve(AssistModelKind.Members, _options.MemberModelVersion);
    private AssistModel? Line() => Catalog.Resolve(AssistModelKind.Lines, _options.LineModelVersion);
    private AssistModel? WantedMember() => RanksMembers ? Member() : null;
    private AssistModel? WantedLine() => SuggestsLines ? Line() : null;

    private CompletionResult Reorder(CompletionResult completions, IReadOnlyList<RankedMember> ranked)
    {
        // Never star every entry: that would say nothing, and the list would still need reading.
        var top = ranked.Take(Math.Min(MaxRecommendations, ranked.Count - 1)).Select(member => member.Name).ToList();
        if (top.Count == 0) return completions;
        string note = $"Recommended by Code Assist ({Member()?.Name}).";
        var recommended = new List<CompletionEntry>(top.Count);
        foreach (string name in top)
            if (completions.Items.FirstOrDefault(item => IsMember(item) && MemberName(item) == name) is { } entry)
                recommended.Add(entry with { Recommended = true, Description = string.IsNullOrEmpty(entry.Description) ? note : $"{note}\n{entry.Description}" });
        var ids = recommended.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        return completions with { Items = [.. recommended, .. completions.Items.Where(item => !ids.Contains(item.Id))] };
    }

    private static bool IsMember(CompletionEntry entry) => entry.Tags.Any(tag => MemberTags.Contains(tag, StringComparer.Ordinal));
    private static string MemberName(CompletionEntry entry) => entry.InsertText.TrimStart('@');

    // The list starts just after a dot, not a range's "..". Roslyn offers no list inside strings or comments, so text alone can decide.
    private static bool IsMemberAccess(string text, int start)
    {
        int index = Math.Min(start, text.Length) - 1;
        while (index >= 0 && char.IsWhiteSpace(text[index])) index--;
        return index >= 0 && text[index] == '.' && (index == 0 || text[index - 1] != '.');
    }

    private static string Tail(string text, int end, int length)
    {
        end = Math.Clamp(end, 0, text.Length);
        return text.Substring(Math.Max(0, end - length), Math.Min(end, length));
    }

    /// <summary>Holds one loaded model. The lock also keeps a session from being disposed during a run, which would crash in native code.</summary>
    private sealed class ModelSlot<T>(string label, Func<AssistModel, T> load) where T : class, IDisposable
    {
        private sealed record Snapshot(AssistModel? Model, bool Loaded, string? Failure);

        private readonly Lock _gate = new();
        private T? _runner;
        // Published for reading without the lock: the settings page must not wait for a model that is loading or running.
        private volatile Snapshot _state = new(null, false, null);

        /// <summary>
        /// Holds the model <paramref name="wanted"/> names, or none when it names none; true when that changed anything. It is
        /// asked inside the lock, so a request already behind an unload cannot undo it.
        /// </summary>
        public bool Prepare(Func<AssistModel?> wanted)
        {
            lock (_gate) return wanted() is { } model ? Switch(model) : Release();
        }

        public TResult? Use<TResult>(Func<AssistModel?> wanted, Func<T, TResult?> run, out bool changed) where TResult : class
        {
            lock (_gate)
            {
                changed = Prepare(wanted);
                if (_runner is not { } runner) return null;
                var result = run(runner);
                // One bad input is not a broken model.
                if (_state.Failure is not null) { _state = _state with { Failure = null }; changed = true; }
                return result;
            }
        }

        public void Unload()
        {
            lock (_gate) Release();
        }

        public void ReportFailure(Exception exception)
        {
            lock (_gate) _state = _state with { Failure = exception.Message };
        }

        public string Describe(AssistModel? model)
        {
            if (model is null) return $"No {label} model is installed.";
            var state = _state;
            if (state.Model != model) return $"{model.Name} loads on first use.";
            return state.Failure is { } failure ? $"{model.Name} failed: {failure}" : state.Loaded ? $"{model.Name} is loaded." : $"{model.Name} is loading.";
        }

        private bool Switch(AssistModel model)
        {
            if (model == _state.Model) return false;
            Release();
            _state = new(model, false, null);
            // A model that cannot load is not retried on every keystroke; choosing another version or restarting tries again.
            try { _runner = load(model); _state = new(model, true, null); }
            catch (Exception exception) { _state = new(model, false, exception.Message); }
            return true;
        }

        private bool Release()
        {
            if (_state.Model is null) return false;
            _runner?.Dispose(); _runner = null;
            _state = new(null, false, null);
            return true;
        }
    }
}
