
namespace WpfStudio.CodeAssist.Tests;

public sealed class AssistModelCatalogTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("WpfStudio-CodeAssist-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteManifest(string kind, string folderVersion, string version, int format = 1, string? id = null)
    {
        string folder = Path.Combine(_directory, kind, folderVersion);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "model.onnx"), "weights");
        File.WriteAllText(Path.Combine(folder, "manifest.json"), $$"""
            { "formatVersion": {{format}}, "id": "{{id ?? "code-assist-" + kind}}", "kind": "{{kind}}", "version": "{{version}}",
              "files": { "model.onnx": "0000000000000000000000000000000000000000000000000000000000000000" },
              "members": { "featureVersion": 2, "contextSize": 1024, "candidateSize": 512, "auxiliarySize": 8, "lookbackTokens": 100, "commentWords": 50 },
              "lines": { "featureVersion": 1, "vocabSize": 2048, "hiddenSize": 192, "contextTokens": 256, "padId": 0, "bosId": 1, "eosId": 2, "contextCacheWeight": 0.6 } }
            """);
        return folder;
    }

    [Fact]
    public void ShippedModelsAreVersionedAndIntact()
    {
        var catalog = new AssistModelCatalog(AssistModelCatalog.DefaultDirectory);
        Assert.Empty(catalog.Problems);
        Assert.Equal(["2.0.0", "1.0.0"], catalog.Versions(AssistModelKind.Members).Select(model => model.Manifest.Version));
        Assert.Equal(["4.0.0", "3.0.0", "2.0.0", "1.0.0"], catalog.Versions(AssistModelKind.Lines).Select(model => model.Manifest.Version));
        foreach (var model in catalog.Versions(AssistModelKind.Members).Concat(catalog.Versions(AssistModelKind.Lines)))
        {
            model.VerifyFiles();
            Assert.Equal("csharp", model.Manifest.Language);
            Assert.StartsWith("code-assist-", model.Name, StringComparison.Ordinal);
            // The Microsoft bundles beside the training code may only be used with its IntelliCode extensions.
            Assert.DoesNotContain("microsoft", model.Manifest.Provenance!.TrainedAs, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No Microsoft model weights", model.Manifest.Provenance.Corpus, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryShippedVersionSaysWhatItIsAndWhereItCameFrom()
    {
        var catalog = new AssistModelCatalog(AssistModelCatalog.DefaultDirectory);
        foreach (var model in catalog.Versions(AssistModelKind.Members).Concat(catalog.Versions(AssistModelKind.Lines)))
        {
            var facts = model.Facts().ToDictionary(fact => fact.Label, fact => fact.Value);
            Assert.Equal(["How it scored", "Learned from", "Network", "What it shows", "Trained", "Size", "Identity"], facts.Keys);
            Assert.All(facts.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
            Assert.Contains(model.Name, facts["Identity"], StringComparison.Ordinal);
            Assert.Matches(@"^\d+(\.\d)? (KB|MB)$", facts["Size"]);
        }
    }

    [Fact]
    public void FactsComeFromTheManifestAndTheFilesOnDisk()
    {
        var facts = new AssistModelCatalog(AssistModelCatalog.DefaultDirectory).Resolve(AssistModelKind.Lines, "3.0.0")!.Facts().ToDictionary(fact => fact.Label, fact => fact.Value);
        Assert.Equal("2-layer GRU with 384 units. It knows 4,096 tokens and reads the last 256.", facts["Network"]);
        Assert.Equal("As much of the line as it is at least 70% sure of, one token at a time.", facts["What it shows"]);
        Assert.Equal("3 November 2026", facts["Trained"]);
        Assert.Equal("9.9 MB", facts["Size"]);
        Assert.Equal("code-assist-lines 3.0.0, trained as lines-pre, dataset 0eb8e2b2fff5", facts["Identity"]);
        Assert.Contains("6,306 C# files", facts["Learned from"], StringComparison.Ordinal);
        Assert.Contains("1,500 places", facts["How it scored"], StringComparison.Ordinal);
    }

    [Fact]
    public void AGatedTransformerDescribesItselfDifferently()
    {
        var facts = new AssistModelCatalog(AssistModelCatalog.DefaultDirectory).Resolve(AssistModelKind.Lines, "4.0.0")!.Facts().ToDictionary(fact => fact.Label, fact => fact.Value);
        Assert.Equal("6-layer transformer, 384 wide, that can copy from earlier in the file. It knows 4,096 tokens and reads the last 460.", facts["Network"]);
        Assert.Equal("A whole line when a small learned gate trusts it. Otherwise the part it is at least 90% sure of, if that comes to 8 characters or closes a bracket left open on the line.", facts["What it shows"]);
        Assert.Equal("18.2 MB", facts["Size"]);
    }

    [Fact]
    public void AManifestWithLittleInItStillDescribesItself()
    {
        WriteManifest("lines", "1.0.0", "1.0.0");
        var model = new AssistModelCatalog(_directory).Versions(AssistModelKind.Lines).Single();
        var facts = model.Facts().ToDictionary(fact => fact.Label, fact => fact.Value);
        Assert.Equal(["Network", "What it shows", "Size", "Identity"], facts.Keys);
        Assert.Equal("Only suggestions that reach the end of the line.", facts["What it shows"]);
        Assert.Equal("1 KB", facts["Size"]);
        Assert.Equal("code-assist-lines 1.0.0", facts["Identity"]);
        Assert.Null(model.TrainedOn);
    }

    [Fact]
    public void VersionsSortByNumberNotText()
    {
        WriteManifest("members", "9.0.0", "9.0.0");
        WriteManifest("members", "10.0.0", "10.0.0");
        var catalog = new AssistModelCatalog(_directory);
        Assert.Equal(["10.0.0", "9.0.0"], catalog.Versions(AssistModelKind.Members).Select(model => model.Manifest.Version));
    }

    [Fact]
    public void ResolveFindsTheVersionAskedForAndOtherwiseTheNewest()
    {
        WriteManifest("members", "1.0.0", "1.0.0");
        WriteManifest("members", "2.1.0", "2.1.0");
        var catalog = new AssistModelCatalog(_directory);
        Assert.Equal("1.0.0", catalog.Resolve(AssistModelKind.Members, "1.0.0")!.Manifest.Version);
        Assert.Equal("2.1.0", catalog.Resolve(AssistModelKind.Members, null)!.Manifest.Version);
        Assert.Equal("2.1.0", catalog.Resolve(AssistModelKind.Members, "3.0.0")!.Manifest.Version);
        Assert.Null(catalog.Resolve(AssistModelKind.Lines, null));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", 1, null, "must live in")]
    [InlineData("2.0.0", "2.0", 1, null, "major.minor.patch")]
    [InlineData("2.0.0", "2.0.0", 2, null, "format 2")]
    [InlineData("2.0.0", "2.0.0", 1, "code-assist-lines", "does not name")]
    public void AFolderThatCannotBeUsedIsReportedAndNotOffered(string folder, string version, int format, string? id, string problem)
    {
        WriteManifest("members", folder, version, format, id);
        WriteManifest("members", "1.5.0", "1.5.0");
        var catalog = new AssistModelCatalog(_directory);
        Assert.Equal("1.5.0", Assert.Single(catalog.Versions(AssistModelKind.Members)).Manifest.Version);
        Assert.Contains(problem, Assert.Single(catalog.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDirectoryIsAProblemNotAFailure()
    {
        var catalog = new AssistModelCatalog(Path.Combine(_directory, "absent"));
        Assert.Empty(catalog.Versions(AssistModelKind.Members));
        Assert.Single(catalog.Problems);
    }

    [Fact]
    public void FilesThatDoNotMatchTheManifestAreRefused()
    {
        WriteManifest("members", "1.0.0", "1.0.0");
        var model = Assert.Single(new AssistModelCatalog(_directory).Versions(AssistModelKind.Members));
        Assert.Contains("does not match", Assert.Throws<InvalidDataException>(model.VerifyFiles).Message, StringComparison.Ordinal);
        File.Delete(model.PathOf("model.onnx"));
        Assert.Contains("missing", Assert.Throws<InvalidDataException>(model.VerifyFiles).Message, StringComparison.Ordinal);
    }
}
