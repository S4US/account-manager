using System.Diagnostics;
using AccountManager.Core.Native;

namespace AccountManager.Core.Processes;

/// <summary>Computes CPU usage percentages from successive samples.</summary>
public sealed class CpuMeter
{
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _processSamples = [];
    private ulong _lastIdle;
    private ulong _lastTotal;

    /// <summary>Whole-system CPU usage since the previous call, 0–100.</summary>
    public double SampleSystem()
    {
        if (!NativeMethods.GetSystemTimes(out NativeMethods.FILETIME idle, out NativeMethods.FILETIME kernel, out NativeMethods.FILETIME user))
        {
            return 0;
        }

        ulong idleNow = idle.Value;
        ulong totalNow = kernel.Value + user.Value; // kernel time includes idle time
        ulong idleDelta = idleNow - _lastIdle;
        ulong totalDelta = totalNow - _lastTotal;
        bool first = _lastTotal == 0;
        _lastIdle = idleNow;
        _lastTotal = totalNow;
        return first || totalDelta == 0 ? 0 : Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    /// <summary>CPU usage of one process since its previous sample, as a share of total machine capacity, 0–100.</summary>
    public double SampleProcess(Process process)
    {
        try
        {
            TimeSpan cpu = process.TotalProcessorTime;
            DateTime now = DateTime.UtcNow;
            double result = 0;
            if (_processSamples.TryGetValue(process.Id, out (TimeSpan Cpu, DateTime At) last))
            {
                double elapsedMs = (now - last.At).TotalMilliseconds * Environment.ProcessorCount;
                if (elapsedMs > 0)
                {
                    result = Math.Clamp(100.0 * (cpu - last.Cpu).TotalMilliseconds / elapsedMs, 0, 100);
                }
            }

            _processSamples[process.Id] = (cpu, now);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    public void Forget(IEnumerable<int> keepProcessIds)
    {
        var keep = keepProcessIds.ToHashSet();
        foreach (int pid in _processSamples.Keys.Where(pid => !keep.Contains(pid)).ToList())
        {
            _processSamples.Remove(pid);
        }
    }
}
