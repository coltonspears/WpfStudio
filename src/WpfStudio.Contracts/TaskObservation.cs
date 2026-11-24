namespace WpfStudio.Contracts;

public static class TaskObservation
{
    /// <summary>
    /// For a task nobody awaits. Its fault is always observed, so it cannot surface later on the finalizer thread, and
    /// is passed to <paramref name="failed"/> when one is given. Cancellation is not a fault.
    /// </summary>
    public static void Forget(this Task task, Action<Exception>? failed = null) =>
        _ = task.ContinueWith(static (completed, state) =>
        {
            var fault = completed.Exception!.GetBaseException(); // Reading Exception is what marks the fault observed.
            try { (state as Action<Exception>)?.Invoke(fault); }
            catch (Exception) { /* A failing reporter must not become the unobserved fault this method prevents. */ }
        }, failed, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

#if !NETFRAMEWORK
    /// <summary>
    /// For a task nobody awaits whose fault handler updates UI state. Its fault is always observed and marshaled onto
    /// <paramref name="dispatcher"/> before invoking <paramref name="failed"/>. Cancellation is not a fault.
    /// </summary>
    public static void ForgetOnUi(this Task task, IUiDispatcher dispatcher, Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(failed);
        task.Forget(fault => dispatcher.Post(() =>
        {
            try { failed(fault); }
            catch (Exception) { /* A failing reporter must not become an unhandled UI-thread exception. */ }
        }));
    }
#endif
}
