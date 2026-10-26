using Microsoft.CodeAnalysis;

namespace WpfStudio.Workspace.Tests;

public sealed class SymbolNavigationTests
{
    private const string Services = """
        namespace App.Services;
        public interface IStore { void Save(); }
        public abstract class StoreBase : IStore { public abstract void Save(); public virtual int Count => 0; }
        public sealed class FileStore : StoreBase { public override void Save() { } public override int Count => 1; }
        public sealed class MemoryStore : IStore { public void Save() { } }
        public class CustomerViewModel { public string CustomerName { get; set; } = ""; public void SaveCustomer() { } }
        """;

    [Fact]
    public async Task SearchRanksExactAndPrefixMatchesFirstAndSkipsAccessors()
    {
        using var fixture = RoslynFixture.Create(Services);
        var results = await SymbolNavigation.SearchAsync(fixture.Solution, "Save", 50, CancellationToken.None);
        Assert.NotEmpty(results);
        Assert.Equal("Save", results[0].Symbol.Name);
        Assert.Contains(results, r => r.Symbol.Name == "SaveCustomer");
        Assert.DoesNotContain(results, r => r.Symbol is IMethodSymbol { MethodKind: MethodKind.PropertyGet });
        Assert.True(results.ToList().FindIndex(r => r.Symbol.Name == "SaveCustomer") > results.ToList().FindLastIndex(r => r.Symbol.Name == "Save"));
    }

    [Fact]
    public async Task CamelHumpsAndQualifiedQueriesFindMembers()
    {
        using var fixture = RoslynFixture.Create(Services);
        var humps = await SymbolNavigation.SearchAsync(fixture.Solution, "CVM", 10, CancellationToken.None);
        Assert.Contains(humps, r => r.Symbol.Name == "CustomerViewModel");
        var qualified = await SymbolNavigation.SearchAsync(fixture.Solution, "FileStore.Save", 10, CancellationToken.None);
        var match = Assert.Single(qualified);
        Assert.Equal("FileStore", match.Symbol.ContainingType.Name);
    }

    [Fact]
    public async Task EmptyQueriesReturnNothing()
    {
        using var fixture = RoslynFixture.Create(Services);
        Assert.Empty(await SymbolNavigation.SearchAsync(fixture.Solution, "  ", 10, CancellationToken.None));
        Assert.Empty(await SymbolNavigation.SearchAsync(fixture.Solution, "Store.", 10, CancellationToken.None));
    }

    [Fact]
    public async Task InterfaceMemberListsEveryImplementation()
    {
        using var fixture = RoslynFixture.Create(Services, "class Use { void M(App.Services.IStore store) => store.Sa$$ve(); }");
        var results = await SymbolNavigation.FindImplementationsAsync(fixture.Document, fixture.Position, CancellationToken.None);
        var owners = results.Select(r => r.ContainingType.Name).Order().ToArray();
        Assert.Equal(["FileStore", "MemoryStore"], owners);
    }

    [Fact]
    public async Task InterfaceAndBaseTypesListImplementingAndDerivedTypes()
    {
        using var fixture = RoslynFixture.Create(Services, "class Use { App.Services.IS$$tore? store; }");
        var results = await SymbolNavigation.FindImplementationsAsync(fixture.Document, fixture.Position, CancellationToken.None);
        Assert.Equal(["FileStore", "MemoryStore", "StoreBase"], results.Select(r => r.Name).Order().ToArray());
    }

    [Fact]
    public async Task VirtualMembersListOverridesAndConcreteMembersNavigateToThemselves()
    {
        using var fixture = RoslynFixture.Create(Services, "class Use { int M(App.Services.StoreBase store) => store.Co$$unt; }");
        var overrides = await SymbolNavigation.FindImplementationsAsync(fixture.Document, fixture.Position, CancellationToken.None);
        Assert.Equal("FileStore", Assert.Single(overrides).ContainingType.Name);

        using var concrete = RoslynFixture.Create(Services, "class Use { void M(App.Services.CustomerViewModel model) => model.SaveCus$$tomer(); }");
        var self = await SymbolNavigation.FindImplementationsAsync(concrete.Document, concrete.Position, CancellationToken.None);
        Assert.Equal("SaveCustomer", Assert.Single(self).Name);
    }
}
