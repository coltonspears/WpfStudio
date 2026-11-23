using System.Reflection;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

/// <summary>Which accepted C# updates announce a model change, against a stand-in worker that can hold a request until it is cancelled.</summary>
public sealed class WorkspaceClientNotificationTests
{
    [Fact]
    public async Task AResendOfTheAcceptedTextCancelledInTheWorkerAnnouncesNothingButACancelledEditStillDoes()
    {
        await using var client = new WorkspaceClient();
        var worker = HeldWorker.Connect(client);
        string path = Path.GetFullPath("Model.cs");
        int models = 0;
        client.CSharpModelChanged += (_, changed) => { if (changed == path) Interlocked.Increment(ref models); };

        await client.UpdateDocumentAsync(new(path, "class Model { }", 1, Analyze: false));
        Assert.Equal(1, models);

        // An analysis or a XAML scan sends the unchanged text again and is superseded while the worker has it.
        await worker.CancelInWorkerAsync(client, new(path, "class Model { }", 1, Analyze: false));
        await client.UpdateDocumentAsync(new(path, "class Model { }", 1, Analyze: false));
        Assert.Equal(1, models);

        // An edit cancelled in the worker may have been committed there, so going back to the accepted text is announced.
        await worker.CancelInWorkerAsync(client, new(path, "class Model { int Value; }", 2, Analyze: false));
        await client.UpdateDocumentAsync(new(path, "class Model { }", 3, Analyze: false));
        Assert.Equal(2, models);
    }

    /// <summary>Answers every update, or holds the next one, as the worker does while it computes, until its caller gives up.</summary>
    public class HeldWorker : DispatchProxy
    {
        private TaskCompletionSource? _received;

        public static HeldWorker Connect(WorkspaceClient client)
        {
            var rpc = Create<IWorkspaceRpc, HeldWorker>();
            var field = typeof(WorkspaceClient).GetField("_proxy", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("WorkspaceClient no longer keeps its worker connection in _proxy.");
            field.SetValue(client, rpc);
            return (HeldWorker)(object)rpc;
        }

        public async Task CancelInWorkerAsync(WorkspaceClient client, UpdateDocumentRequest request)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _received = received;
            using var cancel = new CancellationTokenSource();
            var update = client.UpdateDocumentAsync(request, cancel.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method is { Name: nameof(IWorkspaceRpc.UpdateDocumentAsync) } && args?[0] is UpdateDocumentRequest request)
            {
                if (Interlocked.Exchange(ref _received, null) is { } received)
                {
                    received.TrySetResult();
                    return new TaskCompletionSource<DocumentUpdateResult>().Task; // Never answers: the caller's cancellation ends the wait.
                }
                return Task.FromResult(new DocumentUpdateResult(true, request.Version, []));
            }
            if (method?.Name.StartsWith("add_", StringComparison.Ordinal) == true || method?.Name.StartsWith("remove_", StringComparison.Ordinal) == true) return null;
            throw new NotSupportedException(method?.Name);
        }
    }
}
