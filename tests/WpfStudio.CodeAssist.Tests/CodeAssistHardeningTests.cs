namespace WpfStudio.CodeAssist.Tests;

public sealed class CodeAssistHardeningTests
{
    [Fact]
    public void TokenizerReadsAddedSpecialTokenIdsMatchesLongestFirstAndSafelyDecodesOutOfRangeIds()
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["a"] = 0,
            ["b"] = 1
        };
        // Special tokens not present in model.vocab, with shorter token listed before longer token.
        var special = new (string Content, int Id)[]
        {
            ("<|end|>", 10),
            ("<|endoftext|>", 11)
        };
        var tokenizer = new ByteLevelBpeTokenizer(vocab, new Dictionary<(string, string), int>(), special);

        var encoded = tokenizer.Encode("a<|endoftext|>b<|end|>");
        Assert.Equal([0, 11, 1, 10], encoded);

        // Out-of-range, negative, and sparse null IDs must not throw during Decode.
        Assert.Equal("ab", tokenizer.Decode([-1, 0, 5, 1, 999]));
    }

    [Fact]
    public void TransformerChooseHandlesZeroAndNonFiniteProbabilitiesWithoutNaNOrForbiddenTokens()
    {
        var (tokenZero, confZero, gapZero) = TransformerLineGenerator.Choose([0.9f, 0.1f, 0f, 0f], bosId: 0, padId: 1, eosId: 2);
        Assert.Equal(2, tokenZero);
        Assert.Equal(0d, confZero);
        Assert.Equal(0d, gapZero);

        var (tokenNan, confNan, gapNan) = TransformerLineGenerator.Choose([float.NaN, float.PositiveInfinity, float.NaN, float.NegativeInfinity], bosId: 0, padId: 1, eosId: 2);
        Assert.Equal(2, tokenNan);
        Assert.False(double.IsNaN(confNan));
        Assert.False(double.IsNaN(gapNan));
    }

    [Fact]
    public void LineGeneratorSoftmaxAndSelectionGuardLengthMismatchNaNAndForbiddenTokens()
    {
        Assert.Throws<InvalidDataException>(() => LineGenerator.SoftmaxLogits([1f, 2f], vocabSize: 3, bosId: 0, padId: 1));

        var allNan = LineGenerator.SoftmaxLogits([float.NaN, float.NaN, float.NaN], vocabSize: 3, bosId: 0, padId: 1);
        Assert.All(allNan, p => Assert.False(double.IsNaN(p)));

        int selected = LineGenerator.SelectNextToken([0.9, 0.8, double.NaN, double.NaN], bosId: 0, padId: 1, eosId: 2);
        Assert.Equal(2, selected);
    }

    [Fact]
    public async Task CodeAssistEngineDisposePreventsSubsequentConfigurationAndIsIdempotent()
    {
        var catalog = new AssistModelCatalog(Path.Combine(Path.GetTempPath(), "wpfstudio-empty-models-" + Guid.NewGuid().ToString("N")));
        var engine = new CodeAssistEngine(catalog);
        engine.Dispose();
        engine.Dispose();

        await engine.ConfigureAsync(new CodeAssistOptions(Enabled: true, LineSuggestions: true));
        Assert.False(engine.RanksMembers);
        Assert.False(engine.SuggestsLines);
        Assert.Empty(await engine.RankMembersAsync("x.", 2, ["A", "B"]));
        Assert.Null(await engine.SuggestLineAsync("var x = ", 8));
    }
}
