namespace CounterApp;

/// <summary>Explicit preview data; these methods are not called by application startup.</summary>
public static class CounterPreviewScenarios
{
    public static CounterViewModel Initial() => new() { Count = 0 };
    public static CounterViewModel Counted() => new() { Count = 42 };
    public static CounterViewModel LargeCount() => new() { Count = 999_999 };
}
