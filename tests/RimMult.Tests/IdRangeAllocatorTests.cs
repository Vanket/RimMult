using RimMult.ServerCore;

namespace RimMult.Tests;

public class IdRangeAllocatorTests
{
    [Fact]
    public void LeasesAreDisjointAndContiguous()
    {
        var allocator = new IdRangeAllocator(firstId: 5000, blockSize: 100);
        var a = allocator.Lease();
        var b = allocator.Lease();

        Assert.Equal(5000, a.Start);
        Assert.Equal(a.End, b.Start);
        Assert.False(a.Contains(b.Start));
        Assert.Equal(5200, allocator.NextUnleased);
    }

    [Fact]
    public void RefusesToOverflowRimWorldIntIds()
    {
        var allocator = new IdRangeAllocator(int.MaxValue - 50, blockSize: 100);
        Assert.Throws<InvalidOperationException>(() => allocator.Lease());
    }
}
