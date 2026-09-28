using WpfStudio.Inspection.Protocol;
using WpfStudio.Runtime.Inspection;

namespace WpfStudio.Inspection.Tests;

public sealed class InspectionSourceResolverTests
{
    private static readonly InspectionModule Application = new("Demo", "Demo, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
        @"C:\workspace\bin\Demo.dll", Guid.NewGuid(), true);
    private static readonly InspectionSourceDocument Document = new(@"C:\workspace\Shared\ActualFile.xaml",
        new("8829d00f-11b8-4213-878b-770e8597ac16"), new string('A', 64));
    private static InspectionSourceSymbolsResult Symbols(InspectionModule module, string resource = "/Demo;component/Views/Main Window.xaml") =>
        new(module, [Document], Resources: [new(resource, Document.Path)]);

    [Theory]
    [InlineData("pack://application:,,,/Demo;component/Views/Main%20Window.xaml")]
    [InlineData("/Demo;component/Views/Main%20Window.xaml")]
    [InlineData("pack://application:,,,/Views/Main%20Window.xaml")]
    [InlineData("/Views/Main Window.xaml")]
    [InlineData("Views/Main%20Window.xaml")]
    [InlineData("/demo;COMPONENT/views/main%20window.XAML")]
    public async Task ExplicitCompiledResourceMappingResolvesQualifiedAndApplicationUris(string uri)
    {
        int reads = 0;
        var unrelated = Application with { AssemblyName = "Other", IsResourceAssembly = false };
        var resolved = await InspectionSourceResolver.ResolveAsync(new(uri, 10, 5), new([unrelated, Application]),
            readSymbols: (module, _) =>
            {
                reads++;
                Assert.Equal(Application, module);
                return Task.FromResult(Symbols(module));
            });
        Assert.Equal(Document, resolved.Document);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.test/Window.xaml")]
    [InlineData("file:///C:/workspace/Window.xaml")]
    [InlineData("pack://siteoforigin:,,,/Window.xaml")]
    [InlineData("//server/share/Window.xaml")]
    [InlineData("/Demo;v1.0.0.0;component/Window.xaml")]
    [InlineData("/Demo;31bf3856ad364e35;component/Window.xaml")]
    [InlineData("/Demo;component/../Window.xaml")]
    [InlineData("/Demo;component/%2e%2e/Window.xaml")]
    [InlineData("/Demo;component/%252e%252e/Window.xaml")]
    [InlineData("/Demo;component/Views%2fWindow.xaml")]
    [InlineData("/Demo;component/Views%5cWindow.xaml")]
    [InlineData("/Demo;component/Views/%00Window.xaml")]
    [InlineData("/Demo;component/Views/%xxWindow.xaml")]
    [InlineData("/Demo;component/Views/Window%.xaml")]
    [InlineData("/Demo;component/Views/Window.xaml?query=1")]
    [InlineData("/Demo;component/Views/Window.xaml#fragment")]
    [InlineData("/Demo;component/Views//Window.xaml")]
    [InlineData("/Demo;component/Views/Window.cs")]
    public async Task UnsupportedOrAmbiguousUriSyntaxNeverOpensSymbols(string uri)
    {
        int reads = 0;
        var result = await InspectionSourceResolver.ResolveAsync(new(uri, 1, 1), new([Application]),
            readSymbols: (module, _) => { reads++; return Task.FromResult(Symbols(module)); });
        Assert.Null(result.Document);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("line")]
    [InlineData("column")]
    [InlineData("truncated")]
    [InlineData("missing-owner")]
    [InlineData("duplicate-name")]
    [InlineData("duplicate-default")]
    public async Task IncompleteOrAmbiguousInputNeverOpensSymbols(string problem)
    {
        var hint = new InspectionSourceHint("/Demo;component/Views/Main%20Window.xaml", 1, 1);
        var catalog = new InspectionModuleCatalog([Application]);
        switch (problem)
        {
            case "oversize": hint = hint with { Uri = new string('a', 8193) + ".xaml" }; break;
            case "line": hint = hint with { Line = 0 }; break;
            case "column": hint = hint with { Column = 0 }; break;
            case "truncated": catalog = catalog with { Truncated = true }; break;
            case "missing-owner": catalog = new([]); break;
            case "duplicate-name": catalog = new([Application, Application with { ModuleVersionId = Guid.NewGuid() }]); break;
            case "duplicate-default":
                hint = hint with { Uri = "pack://application:,,,/Views/Main%20Window.xaml" };
                catalog = new([Application, Application with { AssemblyName = "Other" }]);
                break;
        }
        int reads = 0;
        var result = await InspectionSourceResolver.ResolveAsync(hint, catalog,
            readSymbols: (module, _) => { reads++; return Task.FromResult(Symbols(module)); });
        Assert.Null(result.Document);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("different-module")]
    [InlineData("truncated")]
    [InlineData("missing-checksum")]
    [InlineData("missing-mapping")]
    [InlineData("basename-only")]
    [InlineData("different-resource-owner")]
    [InlineData("ambiguous-mapping")]
    [InlineData("duplicate-document")]
    public async Task IncompleteSymbolsNeverProduceAConfidentSourceLocation(string problem)
    {
        var symbols = Symbols(Application);
        symbols = problem switch
        {
            "different-module" => symbols with { Module = Application with { ModuleVersionId = Guid.NewGuid() } },
            "truncated" => symbols with { Truncated = true },
            "missing-checksum" => symbols with { Documents = [] },
            "missing-mapping" => symbols with { Resources = [] },
            "basename-only" => symbols with { Resources = [new("/Demo;component/Other/Main Window.xaml", Document.Path)] },
            "different-resource-owner" => symbols with { Resources = [new("/Other;component/Views/Main Window.xaml", Document.Path)] },
            "ambiguous-mapping" => symbols with { Resources = [new("/Demo;component/Views/Main Window.xaml", Document.Path),
                new("/Demo;component/Views/Main Window.xaml", @"C:\workspace\Other.xaml")] },
            "duplicate-document" => symbols with { Documents = [Document, Document with { ChecksumHex = new string('B', 64) }] },
            _ => throw new InvalidOperationException()
        };
        int reads = 0;
        var result = await InspectionSourceResolver.ResolveAsync(new("/Demo;component/Views/Main%20Window.xaml", 1, 1), new([Application]),
            readSymbols: (_, _) => { reads++; return Task.FromResult(symbols); });
        Assert.Null(result.Document);
        Assert.Equal(1, reads);
        Assert.False(string.IsNullOrWhiteSpace(result.Status));
    }

    [Fact]
    public async Task CancellationWhileSymbolsAreReadCannotReturnACompletedMapping()
    {
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource<InspectionSourceSymbolsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = InspectionSourceResolver.ResolveAsync(new("/Demo;component/Views/Main%20Window.xaml", 1, 1), new([Application]),
            cancellation.Token, (_, token) => { Assert.Equal(cancellation.Token, token); return ready.Task; });
        cancellation.Cancel();
        ready.SetResult(Symbols(Application));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
