using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.Contracts;
using WpfStudio.Runtime.Debugging;

namespace WpfStudio.Runtime.Tests;

public sealed class DebuggerAdapterTests
{
    private const string Missing = @"Z:\no-such-folder\netcoredbg.exe";

    [Fact]
    public async Task ALocatedAdapterBecomesTheSessionsAdapter()
    {
        var located = Environment.ProcessPath!;
        var session = new DebugSession { AdapterPath = Missing };
        await using var model = new DebuggerViewModel(session, new Inline(), new FakeAdapters { Located = located }, new WeakReferenceMessenger());
        Assert.Equal(located, session.AdapterPath);
        Assert.False(model.IsAdapterMissing);
    }

    [Fact]
    public async Task EnsureInstallsOnlyAfterTheUserAgrees()
    {
        var adapters = new FakeAdapters { Installed = Environment.ProcessPath! };
        var session = new DebugSession { AdapterPath = Missing };
        await using var model = new DebuggerViewModel(session, new Inline(), adapters, new WeakReferenceMessenger());
        Assert.Equal("the fake adapter", model.AdapterDescription);
        Assert.True(model.IsAdapterMissing);

        Assert.False(await model.EnsureAdapterAsync(() => Task.FromResult(false)));
        Assert.Equal(0, adapters.Installs);

        Assert.True(await model.EnsureAdapterAsync(() => Task.FromResult(true)));
        Assert.Equal(1, adapters.Installs);
        Assert.Equal(Environment.ProcessPath, session.AdapterPath);
    }

    [Fact]
    public async Task TheAttachListNamesRunningProcessesAndFollowsTheTypedId()
    {
        await using var model = new DebuggerViewModel(new DebugSession { AdapterPath = Missing }, new Inline(), new NoDebugAdapter(), new WeakReferenceMessenger());
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true })!;
        try
        {
            model.AttachProcessId = other.Id;
            await model.RefreshProcessesCommand.ExecuteAsync(null);
            Assert.DoesNotContain(model.Processes, process => process.Id == Environment.ProcessId);
            Assert.Equal(model.Processes.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id), model.Processes);
            Assert.Equal((other.Id, "cmd"), (model.SelectedProcess?.Id, model.SelectedProcess?.Name));

            model.AttachProcessId = -1;
            Assert.Null(model.SelectedProcess);
            model.SelectedProcess = model.Processes.First(process => process.Id == other.Id);
            Assert.Equal(other.Id, model.AttachProcessId);
        }
        finally { other.Kill(true); }
    }

    private sealed class FakeAdapters : IDebugAdapterProvider
    {
        public string? Located { get; init; }
        public string Installed { get; init; } = "";
        public int Installs { get; private set; }
        public string Description => "the fake adapter";
        public string? Locate() => Located;
        public Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) { Installs++; return Task.FromResult(Installed); }
    }

    private sealed class Inline : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { action(); return Task.CompletedTask; }
    }
}

/// <summary>For tests that never reach the install path.</summary>
internal sealed class NoDebugAdapter : IDebugAdapterProvider
{
    public string Description => "no adapter";
    public string? Locate() => null;
    public Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Tests never install an adapter.");
}
