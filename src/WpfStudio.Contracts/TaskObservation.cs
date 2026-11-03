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
}
