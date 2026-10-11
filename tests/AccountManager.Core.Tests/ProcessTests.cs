using AccountManager.Core.Processes;

namespace AccountManager.Core.Tests;

public class AffinityTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Small_cpus_are_not_restricted(int cpus)
    {
        Assert.Equal(0UL, ProcessTuner.ComputeAffinityMask(0, cpus));
    }

    [Fact]
    public void Four_cpus_reserve_first_core()
    {
        // 1 reserved, 3 left => 1 pair: cores 1 and 2.
        Assert.Equal(0b0110UL, ProcessTuner.ComputeAffinityMask(0, 4));
        Assert.Equal(0b0110UL, ProcessTuner.ComputeAffinityMask(5, 4));
    }

    [Fact]
    public void Sixteen_cpus_rotate_through_pairs_after_two_reserved()
    {
        Assert.Equal(0b1100UL, ProcessTuner.ComputeAffinityMask(0, 16));
        Assert.Equal(0b110000UL, ProcessTuner.ComputeAffinityMask(1, 16));
        Assert.Equal((1UL << 14) | (1UL << 15), ProcessTuner.ComputeAffinityMask(6, 16));
        Assert.Equal(0b1100UL, ProcessTuner.ComputeAffinityMask(7, 16)); // wraps
    }

    [Fact]
    public void Masks_stay_within_processor_count()
    {
        for (int cpus = 4; cpus <= 64; cpus++)
        {
            for (int slot = 0; slot < 40; slot++)
            {
                ulong mask = ProcessTuner.ComputeAffinityMask(slot, cpus);
                Assert.NotEqual(0UL, mask);
                Assert.Equal(0UL, cpus == 64 ? 0UL : mask >> cpus);
                Assert.Equal(0UL, mask & 1UL); // core 0 always left free
            }
        }
    }
}

public class WindowGridTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(9, 3, 3)]
    [InlineData(10, 4, 3)]
    [InlineData(0, 1, 1)]
    public void Picks_near_square_grid(int count, int columns, int rows)
    {
        Assert.Equal((columns, rows), WindowTools.GridFor(count));
    }
}

public class SystemMemoryTests
{
    [Fact]
    public void Reports_physical_memory()
    {
        MemorySnapshot snapshot = SystemMemory.GetSnapshot();

        Assert.True(snapshot.TotalBytes > 0);
        Assert.True(snapshot.AvailableBytes <= snapshot.TotalBytes);
        Assert.InRange(snapshot.LoadPercent, 0u, 100u);
    }
}
