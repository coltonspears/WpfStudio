using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Profiling.Tests;

public sealed class HeapBudgetTests
{
    private const long Gigabyte = 1L << 30;

    [Fact]
    public void ADefaultRequestReadsTheWholeHeapUpToTheWorkersMemory()
    {
        var budget = HeapBudget.For(new HeapCaptureRequest(DumpPath: "a.dmp"), 32 * Gigabyte, is64Bit: true);
        Assert.Equal(Array.MaxLength, budget.Objects);
        Assert.Equal(Array.MaxLength, budget.References);
        Assert.Equal(Array.MaxLength, budget.Roots);
        // Ten million objects with four references each fit comfortably in 60% of 32 GB; a hundred million do not.
        Assert.False(budget.Exceeds(10_000_000, 40_000_000));
        Assert.True(budget.Exceeds(100_000_000, 400_000_000));
        Assert.Contains("larger than this worker can analyze", budget.MemoryLimitNote());
    }

    [Fact]
    public void ExplicitLimitsAreKeptAndStillBoundTheRootList()
    {
        var budget = HeapBudget.For(new HeapCaptureRequest(DumpPath: "a.dmp", MaxObjects: 250_000, MaxReferences: 2_000_000), 32 * Gigabyte, is64Bit: true);
        Assert.Equal(250_000, budget.Objects);
        Assert.Equal(2_000_000, budget.References);
        Assert.Equal(1_000_000, budget.Roots);
        Assert.Contains("Object limit reached (250,000)", budget.ObjectLimitNote());
        Assert.Contains("Reference limit reached (2,000,000)", budget.ReferenceLimitNote());
    }

    [Fact]
    public void AThirtyTwoBitWorkerKeepsToItsAddressSpace()
    {
        var budget = HeapBudget.For(new HeapCaptureRequest(DumpPath: "a.dmp"), 64 * Gigabyte, is64Bit: false);
        Assert.True(budget.MemoryBytes <= 3 * Gigabyte / 2);
        Assert.Contains("32-bit", budget.MemoryLimitNote());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 999)]
    [InlineData(500, 6_000_000)]
    public void LimitsBelowAThousandAreRejected(int objects, int references) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HeapBudget.For(new HeapCaptureRequest(DumpPath: "a.dmp", MaxObjects: objects, MaxReferences: references)));

    [Fact]
    public void AnUnknownMachineSizeStillAllowsALargeHeap()
    {
        var budget = HeapBudget.For(new HeapCaptureRequest(DumpPath: "a.dmp"), 0, is64Bit: true);
        Assert.False(budget.Exceeds(5_000_000, 20_000_000));
    }
}
