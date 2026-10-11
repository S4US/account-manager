using System.Diagnostics;
using System.Runtime.InteropServices;
using AccountManager.Core.Native;

namespace AccountManager.Core.Processes;

/// <summary>Per-process resource controls applied to Roblox clients.</summary>
public static class ProcessTuner
{
    private const long MinWorkingSetBytes = 50L * 1024 * 1024;
    private static readonly Dictionary<int, nint> JobsByLimitMb = [];
    private static readonly object JobLock = new();

    public static bool TrySetPriority(Process process, ProcessPriorityClass priority)
    {
        try
        {
            process.PriorityClass = priority;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns on Windows "Efficiency mode" (EcoQoS) for the process: the scheduler prefers efficient cores and lower
    /// clock speeds for it. Supported on Windows 10 1709+; strongest effect on Windows 11.
    /// </summary>
    public static bool TrySetEfficiencyMode(Process process, bool enabled)
    {
        var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
        {
            Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = enabled ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        try
        {
            return NativeMethods.SetProcessInformation(process.Handle, NativeMethods.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf(state));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Assigns each client a pair of adjacent logical processors, rotating through the available ones and leaving
    /// the first core(s) free for Windows and the manager. Returns 0 (no restriction) on small CPUs.
    /// </summary>
    public static ulong ComputeAffinityMask(int slot, int processorCount)
    {
        if (processorCount < 4 || processorCount > 64)
        {
            return 0;
        }

        int reserved = processorCount < 8 ? 1 : 2;
        int pairs = Math.Max(1, (processorCount - reserved) / 2);
        int first = reserved + (Math.Abs(slot) % pairs) * 2;
        int second = Math.Min(first + 1, processorCount - 1);
        return (1UL << first) | (1UL << second);
    }

    public static bool TrySetAffinity(Process process, ulong mask)
    {
        try
        {
            if (mask == 0)
            {
                int count = Math.Min(Environment.ProcessorCount, 64);
                mask = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
            }

            process.ProcessorAffinity = (nint)(long)mask;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Caps the client's working set (physical RAM in use). Windows pages out the excess instead of letting the
    /// client grow, which keeps many clients from exhausting RAM. Implemented with a job object, one per limit size.
    /// </summary>
    public static bool TryLimitWorkingSet(Process process, int limitMb)
    {
        if (limitMb <= 0)
        {
            return false;
        }

        long max = Math.Max((long)limitMb * 1024 * 1024, MinWorkingSetBytes * 2);
        try
        {
            NativeMethods.SetProcessWorkingSetSizeEx(process.Handle, (nint)MinWorkingSetBytes, (nint)max, 0);
            nint job = GetOrCreateLimitJob(limitMb, max);
            return job != 0 && NativeMethods.AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Asks Windows to move the process's pages out of RAM. They fault back in as needed.</summary>
    public static bool TryTrimWorkingSet(Process process)
    {
        try
        {
            return NativeMethods.EmptyWorkingSet(process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static nint GetOrCreateLimitJob(int limitMb, long maxBytes)
    {
        lock (JobLock)
        {
            if (JobsByLimitMb.TryGetValue(limitMb, out nint existing))
            {
                return existing;
            }

            nint job = NativeMethods.CreateJobObjectW(0, null);
            if (job == 0)
            {
                return 0;
            }

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_WORKINGSET,
                    MinimumWorkingSetSize = (nuint)MinWorkingSetBytes,
                    MaximumWorkingSetSize = (nuint)maxBytes,
                },
            };
            if (!NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf(info)))
            {
                NativeMethods.CloseHandle(job);
                return 0;
            }

            JobsByLimitMb[limitMb] = job;
            return job;
        }
    }
}
