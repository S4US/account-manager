using System.Diagnostics;
using System.Runtime.InteropServices;
using AccountManager.Core.Native;

namespace AccountManager.Core.Processes;

/// <summary>
/// Per-process resource controls applied to Roblox clients. Each call opens the process with only the access
/// rights it needs, so a client that refuses broad access can still be tuned.
/// </summary>
public static class ProcessTuner
{
    private const long MinWorkingSetBytes = 50L * 1024 * 1024;

    // Windows' default working set bounds, used when a limit is removed.
    private const long DefaultMinWorkingSetBytes = 200L * 1024;
    private const long DefaultMaxWorkingSetBytes = 1380L * 1024;

    // One job per client: a job's CPU rate applies to all of its processes together.
    private static readonly Dictionary<int, nint> CpuJobs = [];
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
        return WithProcess(process, NativeMethods.PROCESS_SET_INFORMATION, out _,
            handle => NativeMethods.SetProcessInformation(handle, NativeMethods.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf(state)));
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
    /// Caps the client's working set (physical RAM in use) with a hard limit: Windows pages out anything above it,
    /// even when plenty of RAM is free. A limit of 0 removes the cap. On failure, <paramref name="error"/> holds the
    /// Windows error code.
    /// </summary>
    public static bool TryLimitWorkingSet(Process process, int limitMb, out int error)
    {
        (long min, long max, uint flags) = limitMb > 0
            ? (MinWorkingSetBytes, Math.Max((long)limitMb * 1024 * 1024, MinWorkingSetBytes * 2),
                NativeMethods.QUOTA_LIMITS_HARDWS_MIN_DISABLE | NativeMethods.QUOTA_LIMITS_HARDWS_MAX_ENABLE)
            : (DefaultMinWorkingSetBytes, DefaultMaxWorkingSetBytes,
                NativeMethods.QUOTA_LIMITS_HARDWS_MIN_DISABLE | NativeMethods.QUOTA_LIMITS_HARDWS_MAX_DISABLE);

        return WithProcess(process, NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, out error,
            handle => NativeMethods.SetProcessWorkingSetSizeEx(handle, (nint)min, (nint)max, flags));
    }

    /// <summary>
    /// Limits the client to a share of total CPU time with a job object hard cap, which also lowers its frame rate.
    /// <paramref name="percent"/> is a share of the whole CPU (all cores); 0 removes the limit.
    /// </summary>
    public static bool TryLimitCpu(Process process, double percent, out int error)
    {
        error = 0;
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        lock (JobLock)
        {
            if (!CpuJobs.TryGetValue(pid, out nint job))
            {
                if (percent <= 0)
                {
                    return true;
                }

                job = NativeMethods.CreateJobObjectW(0, null);
                if (job == 0)
                {
                    error = Marshal.GetLastWin32Error();
                    return false;
                }

                bool assigned = WithProcess(process, NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_TERMINATE, out error,
                    handle => NativeMethods.AssignProcessToJobObject(job, handle));
                if (!assigned)
                {
                    NativeMethods.CloseHandle(job);
                    return false;
                }

                CpuJobs[pid] = job;
            }

            var info = new NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = percent > 0 ? NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP : 0,
                CpuRate = percent > 0 ? (uint)Math.Clamp(Math.Round(percent * 100), 1, 10000) : 0,
            };
            if (!NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectCpuRateControlInformation, ref info, (uint)Marshal.SizeOf(info)))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            return true;
        }
    }

    /// <summary>Releases what the tuner holds for a process that has exited.</summary>
    public static void Forget(int pid)
    {
        lock (JobLock)
        {
            if (CpuJobs.Remove(pid, out nint job))
            {
                NativeMethods.CloseHandle(job);
            }
        }
    }

    /// <summary>Asks Windows to move the process's pages out of RAM. They fault back in as needed.</summary>
    public static bool TryTrimWorkingSet(Process process) =>
        WithProcess(process, NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, out _, NativeMethods.EmptyWorkingSet);

    private static bool WithProcess(Process process, uint access, out int error, Func<nint, bool> action)
    {
        error = 0;
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        nint handle = NativeMethods.OpenProcess(access, false, pid);
        if (handle == 0)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            if (action(handle))
            {
                return true;
            }

            error = Marshal.GetLastWin32Error();
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
