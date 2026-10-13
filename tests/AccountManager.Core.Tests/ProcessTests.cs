using System.Runtime.InteropServices;
using AccountManager.Core.Native;
using AccountManager.Core.Processes;
using AccountManager.Core.Services;

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

/// <summary>Sizing against real (hidden) windows, which use Windows' default minimum size.</summary>
public class WindowSizingTests
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SM_CXMINTRACK = 34;
    private const int SM_CYMINTRACK = 35;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [Fact]
    public void Shrinking_a_framed_window_stops_at_the_minimum_size_and_keeps_its_place()
    {
        nint hwnd = CreateHiddenWindow();
        try
        {
            WindowTools.ShrinkToMinimum(hwnd);

            NativeMethods.RECT rect = GetRect(hwnd);
            Assert.Equal(GetSystemMetrics(SM_CXMINTRACK), rect.Right - rect.Left);
            Assert.Equal(GetSystemMetrics(SM_CYMINTRACK), rect.Bottom - rect.Top);
            Assert.Equal((100, 100), (rect.Left, rect.Top));
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    [Fact]
    public void Without_its_frame_a_window_goes_below_the_minimum_and_the_frame_comes_back()
    {
        nint hwnd = CreateHiddenWindow();
        try
        {
            Assert.True(20 < GetSystemMetrics(SM_CXMINTRACK) && 10 < GetSystemMetrics(SM_CYMINTRACK));
            Assert.True(WindowTools.RemoveFrame(hwnd));
            NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 20, 10, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            NativeMethods.RECT rect = GetRect(hwnd);
            Assert.Equal((20, 10), (rect.Right - rect.Left, rect.Bottom - rect.Top));

            Assert.True(WindowTools.RestoreFrame(hwnd));
            uint style = (uint)NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_STYLE);
            Assert.Equal(WS_OVERLAPPEDWINDOW, style & WS_OVERLAPPEDWINDOW);
            Assert.Equal(0u, style & WS_POPUP);
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    private static nint CreateHiddenWindow()
    {
        nint hwnd = CreateWindowExW(0, "STATIC", "account-manager test", WS_OVERLAPPEDWINDOW, 100, 100, 800, 600, 0, 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        return hwnd;
    }

    private static NativeMethods.RECT GetRect(nint hwnd)
    {
        Assert.True(GetWindowRect(hwnd, out NativeMethods.RECT rect));
        return rect;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeMethods.RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
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

    [Fact]
    public void Reports_commit_limit()
    {
        MemorySnapshot snapshot = SystemMemory.GetSnapshot();

        Assert.True(snapshot.CommitLimitGb > 0);
        Assert.InRange(snapshot.CommitUsedGb, 0, snapshot.CommitLimitGb);
    }
}

public class ClientCommitEstimateTests
{
    [Fact]
    public void Uses_a_typical_value_without_clients()
    {
        (double perClient, double pending) = SystemStatus.EstimateClientCommit([]);

        Assert.Equal(SystemStatus.DefaultClientCommitMb, perClient);
        Assert.Equal(0, pending);
    }

    [Fact]
    public void Averages_loaded_clients_and_counts_what_loading_ones_still_take()
    {
        (double perClient, double pending) = SystemStatus.EstimateClientCommit(
        [
            (1600, true),
            (1700, true),
            (400, false), // just started: will take 1250 more
            (1500, false), // nearly there: 150 more
            (2000, false), // already past the average: nothing more
        ]);

        Assert.Equal(1650, perClient);
        Assert.Equal(1400, pending);
    }

    [Fact]
    public void Uses_the_typical_value_while_every_client_is_loading()
    {
        (double perClient, double pending) = SystemStatus.EstimateClientCommit([(300, false), (800, false)]);

        Assert.Equal(SystemStatus.DefaultClientCommitMb, perClient);
        Assert.Equal(2 * SystemStatus.DefaultClientCommitMb - 1100, pending);
    }

    [Fact]
    public void Never_assumes_less_than_a_gigabyte_per_client()
    {
        (double perClient, _) = SystemStatus.EstimateClientCommit([(200, true), (300, true)]);

        Assert.Equal(1024, perClient);
    }
}
