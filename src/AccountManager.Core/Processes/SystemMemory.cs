using System.Runtime.InteropServices;
using System.Security.Principal;
using AccountManager.Core.Native;

namespace AccountManager.Core.Processes;

public readonly record struct MemorySnapshot(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent, ulong CommitAvailableBytes, ulong CommitTotalBytes)
{
    public double TotalGb => TotalBytes / 1073741824.0;

    public double UsedGb => (TotalBytes - AvailableBytes) / 1073741824.0;

    public long AvailableMb => (long)(AvailableBytes / 1048576);
}

/// <summary>System-wide memory status and maintenance.</summary>
public static class SystemMemory
{
    private const int SystemMemoryListInformation = 80;

    private enum MemoryListCommand
    {
        EmptyWorkingSets = 2,
        FlushModifiedList = 3,
        PurgeStandbyList = 4,
        PurgeLowPriorityStandbyList = 5,
    }

    public static MemorySnapshot GetSnapshot()
    {
        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status)
            ? new MemorySnapshot(status.ullTotalPhys, status.ullAvailPhys, status.dwMemoryLoad, status.ullAvailPageFile, status.ullTotalPageFile)
            : default;
    }

    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Returns cached file pages ("standby list") to the free list. Needs administrator rights.
    /// Skipped when commit space is nearly exhausted, where it would only cause extra paging.
    /// </summary>
    public static bool TryPurgeStandbyList(bool includeNormalPriority)
    {
        MemorySnapshot snapshot = GetSnapshot();
        if (snapshot.CommitAvailableBytes < 2UL * 1024 * 1024 * 1024)
        {
            return false;
        }

        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            return false;
        }

        bool ok = Execute(MemoryListCommand.PurgeLowPriorityStandbyList);
        if (includeNormalPriority)
        {
            ok |= Execute(MemoryListCommand.PurgeStandbyList);
        }

        return ok;
    }

    private static bool Execute(MemoryListCommand command)
    {
        nint buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, (int)command);
            return NativeMethods.NtSetSystemInformation(SystemMemoryListInformation, buffer, sizeof(int)) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool EnablePrivilege(string name)
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out nint token))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValueW(null, name, out NativeMethods.LUID luid))
            {
                return false;
            }

            var privileges = new NativeMethods.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = NativeMethods.SE_PRIVILEGE_ENABLED };
            // AdjustTokenPrivileges succeeds even when the privilege isn't held; ERROR_NOT_ALL_ASSIGNED (1300) reports that.
            return NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0) && Marshal.GetLastPInvokeError() != 1300;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
