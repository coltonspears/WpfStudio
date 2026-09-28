using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionSourceSymbolsTests
{
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net9.0-windows")]
    [InlineData("net10.0-windows")]
    public async Task RealWpfPortableSymbolsMatchTheObservedModuleAndAuthoredXamlBytes(string framework)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", framework, "WpfStudio.InspectionFixture.dll");
        var module = Observe(path);
        var result = await InspectionSourceSymbols.ReadAsync(module);
        Assert.Equal(module, result.Module);
        Assert.False(result.Truncated);
        var document = Assert.Single(result.Documents, document => Path.GetFileName(document.Path) == "PrimaryWindow.xaml");
        Assert.True(Path.IsPathFullyQualified(document.Path));
        byte[] actual = document.ChecksumAlgorithm == Sha256
            ? SHA256.HashData(await File.ReadAllBytesAsync(document.Path))
            : SHA1.HashData(await File.ReadAllBytesAsync(document.Path));
        Assert.Equal(Convert.ToHexString(actual), document.ChecksumHex);
        Assert.Contains("Matched portable symbols", result.Status);
        var resource = Assert.Single(result.Resources ?? [], resource => resource.DocumentPath == document.Path);
        Assert.Equal("/WpfStudio.InspectionFixture;component/primarywindow.xaml", resource.Uri, ignoreCase: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableAndEmbeddedDocumentsKeepTheirCompiledChecksumAfterTheSourceChanges(bool embedded)
    {
        using var fixture = new SymbolsFixture(embedded: embedded);
        await File.WriteAllTextAsync(fixture.SourcePath, "<Button Content=\"Changed after compilation\" />");
        var result = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        var document = Assert.Single(result.Documents);
        Assert.Equal(fixture.SourcePath, document.Path);
        Assert.Equal(fixture.SourceChecksum, document.ChecksumHex);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourcePath))), document.ChecksumHex);
        Assert.Contains(embedded ? "embedded" : "beside", result.Status);
    }

    [Fact]
    public async Task StaleModuleOrAssemblyIdentityCannotUseOtherwiseValidSymbols()
    {
        using var fixture = new SymbolsFixture();
        Assert.Empty((await InspectionSourceSymbols.ReadAsync(fixture.Module with { ModuleVersionId = Guid.NewGuid() })).Documents);
        Assert.Empty((await InspectionSourceSymbols.ReadAsync(fixture.Module with { AssemblyName = "Other" })).Documents);
        Assert.Empty((await InspectionSourceSymbols.ReadAsync(fixture.Module with { AssemblyFullName = "Symbols, Version=2.0.0.0, Culture=neutral, PublicKeyToken=null" })).Documents);
    }

    [Fact]
    public async Task AdjacentPdbFromAnotherBuildIsRejectedEvenWithTheSameDocumentChecksum()
    {
        using var fixture = new SymbolsFixture();
        using var other = new SymbolsFixture();
        File.Copy(other.PdbPath, fixture.PdbPath, overwrite: true);
        var result = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        Assert.Empty(result.Documents);
        Assert.Contains("does not match", result.Status);
    }

    [Fact]
    public async Task EmbeddedPdbIsStillAvailableWhenAnAdjacentPdbBelongsToAnotherBuild()
    {
        using var fixture = new SymbolsFixture(embedded: true);
        using var other = new SymbolsFixture();
        File.Copy(other.PdbPath, fixture.PdbPath);
        var result = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        Assert.Single(result.Documents);
        Assert.Contains("embedded", result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonInitialCodeViewAgeIsRejected(bool embedded)
    {
        using var fixture = new SymbolsFixture(embedded: embedded, age: 2);
        var result = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        Assert.Empty(result.Documents);
        Assert.Contains("does not match", result.Status);
    }

    [Fact]
    public async Task MissingOrWindowsOrCorruptSymbolsReturnAReasonWithoutThrowing()
    {
        using var fixture = new SymbolsFixture();
        File.Delete(fixture.PdbPath);
        var missing = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        Assert.Empty(missing.Documents);
        Assert.False(string.IsNullOrWhiteSpace(missing.Status));
        await File.WriteAllTextAsync(fixture.PdbPath, "Microsoft C/C++ MSF 7.00\r\n\u001aDS");
        var windows = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        Assert.Empty(windows.Documents);
        Assert.Contains("portable PDB", windows.Status);
        await File.WriteAllBytesAsync(fixture.Module.Path, [1, 2, 3, 4]);
        var corrupt = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        Assert.Empty(corrupt.Documents);
        Assert.Contains("could not be verified", corrupt.Status);
    }

    [Fact]
    public async Task RecordedDebugPathOutsideTheModuleDirectoryIsNeverFollowed()
    {
        using var external = new SymbolsFixture();
        using var fixture = new SymbolsFixture(recordedPdbPath: external.PdbPath);
        File.Copy(fixture.PdbPath, external.PdbPath, overwrite: true); // An exact matching PDB really exists at the recorded path.
        File.Delete(fixture.PdbPath);
        var result = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        Assert.Empty(result.Documents);
        Assert.Contains("No matching", result.Status);
    }

    [Theory]
    [InlineData(@"\\server\share\Symbols.dll")]
    [InlineData("https://example.invalid/Symbols.dll")]
    [InlineData("relative/Symbols.dll")]
    public async Task NonLocalModulePathsAreUnavailableWithoutOpeningThem(string path)
    {
        var result = await InspectionSourceSymbols.ReadAsync(new("Symbols", "Symbols", path, Guid.NewGuid()));
        Assert.Empty(result.Documents);
        Assert.Contains("absolute local", result.Status);
    }

    [Fact]
    public async Task AdvertisedEmbeddedExpansionIsBoundedBeforeDecompression()
    {
        using var fixture = new SymbolsFixture(embedded: true);
        int dataPointer;
        using (var stream = File.OpenRead(fixture.Module.Path))
        using (var reader = new PEReader(stream))
            dataPointer = Assert.Single(reader.ReadDebugDirectory(), entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).DataPointer;
        var bytes = await File.ReadAllBytesAsync(fixture.Module.Path);
        BitConverter.GetBytes(32 * 1024 * 1024 + 1).CopyTo(bytes, dataPointer + 4);
        await File.WriteAllBytesAsync(fixture.Module.Path, bytes);
        var result = await InspectionSourceSymbols.ReadAsync(fixture.Module);
        Assert.Empty(result.Documents);
        Assert.Contains("size limit", result.Status);
    }

    [Fact]
    public async Task DocumentCountIsBoundedAndUnknownChecksumAlgorithmsAreNotGuessed()
    {
        using var many = new SymbolsFixture(documentCount: 513);
        var bounded = await InspectionSourceSymbols.ReadAsync(many.Module);
        Assert.Equal(512, bounded.Documents.Count);
        Assert.True(bounded.Truncated);
        using var unknown = new SymbolsFixture(checksumAlgorithm: Guid.NewGuid());
        var omitted = await InspectionSourceSymbols.ReadAsync(unknown.Module);
        Assert.Empty(omitted.Documents);
        Assert.Contains("unsupported", omitted.Status);
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoASymbolFailure()
    {
        using var fixture = new SymbolsFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InspectionSourceSymbols.ReadAsync(fixture.Module, cancellation.Token));
    }

    private static InspectionModule Observe(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var identity = AssemblyName.GetAssemblyName(path);
        return new(identity.Name!, identity.FullName, path, metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
    }

    // Emits actual PE and portable-PDB structures without loading or executing
    // application code and without a compiler/build process inside a test run.
    private sealed class SymbolsFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "WpfStudio-SymbolTests", Guid.NewGuid().ToString("N"));
        public string SourcePath { get; }
        public string SourceChecksum { get; }
        public string PdbPath { get; }
        public InspectionModule Module { get; }

        public SymbolsFixture(bool embedded = false, int age = 1, string? recordedPdbPath = null,
            int documentCount = 1, Guid? checksumAlgorithm = null)
        {
            Directory.CreateDirectory(_directory);
            SourcePath = Path.Combine(_directory, "View.xaml");
            byte[] source = Encoding.UTF8.GetBytes("<Button Content=\"Compiled original\" />");
            File.WriteAllBytes(SourcePath, source);
            byte[] checksum = SHA256.HashData(source);
            SourceChecksum = Convert.ToHexString(checksum);
            string modulePath = Path.Combine(_directory, "Symbols.dll");
            PdbPath = Path.ChangeExtension(modulePath, ".pdb");
            var metadata = new MetadataBuilder();
            var mvid = Guid.NewGuid();
            metadata.AddModule(0, metadata.GetOrAddString("Symbols.dll"), metadata.GetOrAddGuid(mvid), default, default);
            metadata.AddAssembly(metadata.GetOrAddString("Symbols"), new Version(1, 0, 0, 0), default, default,
                (AssemblyFlags)0, AssemblyHashAlgorithm.None);
            metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
            var pdbMetadata = new MetadataBuilder();
            for (int index = 0; index < documentCount; index++)
            {
                string path = index == 0 ? SourcePath : Path.Combine(_directory, $"View{index}.xaml");
                pdbMetadata.AddDocument(pdbMetadata.GetOrAddDocumentName(path), pdbMetadata.GetOrAddGuid(checksumAlgorithm ?? Sha256),
                    pdbMetadata.GetOrAddBlob(checksum), default);
            }
            var pdb = new BlobBuilder();
            var id = new PortablePdbBuilder(pdbMetadata, metadata.GetRowCounts(), default).Serialize(pdb);
            var debug = new DebugDirectoryBuilder();
            debug.AddCodeViewEntry(recordedPdbPath ?? PdbPath, id, 0x0100, age);
            if (embedded) debug.AddEmbeddedPortablePdbEntry(pdb, 0x0100);
            else File.WriteAllBytes(PdbPath, pdb.ToArray());
            var image = new BlobBuilder();
            new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata),
                new BlobBuilder(), debugDirectoryBuilder: debug).Serialize(image);
            File.WriteAllBytes(modulePath, image.ToArray());
            Module = new("Symbols", "Symbols, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", modulePath, mvid);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
