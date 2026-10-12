using System.Diagnostics;
using System.Runtime.InteropServices;
using AccountManager.Core.Native;
using AccountManager.Core.Roblox;

namespace AccountManager.Core.Processes;

/// <summary>
/// Lets several Roblox clients run side by side.
///
/// Roblox enforces a single client through named kernel objects (ROBLOX_singletonMutex / ROBLOX_singletonEvent).
/// A starting client waits on the mutex, and an older client that still holds it is asked to quit. Before each
/// launch the handles to those objects inside the running clients are closed, so the objects disappear and the new
/// client starts unopposed. Nothing holds the objects in between, so a starting client never waits on them.
/// (Holding them in this process instead made every new client wait, and closing a handle a client is waiting on
/// makes it shut itself down.)
/// </summary>
public static class MultiInstanceGuard
{
    private const string SingletonPrefix = "ROBLOX_singleton";

    /// <summary>
    /// Closes the singleton handles held by running Roblox clients and returns how many were closed. Only clients
    /// older than <paramref name="minimumAge"/> are touched, so none is caught in its own startup wait, and only
    /// their mutex and event handles are opened.
    /// </summary>
    public static int CloseSingletonHandlesInRunningClients(TimeSpan minimumAge, Action<string>? log = null)
    {
        Process[] clients = Process.GetProcessesByName(RobloxInstallation.PlayerProcessName);
        if (clients.Length == 0)
        {
            return 0;
        }

        var pids = new HashSet<int>();
        foreach (Process p in clients)
        {
            try
            {
                if (DateTime.Now - p.StartTime >= minimumAge)
                {
                    pids.Add(p.Id);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        if (pids.Count == 0)
        {
            return 0;
        }
        int closed = 0;
        foreach ((int pid, List<nint> handles) in HandleEnumerator.GetMutexAndEventHandles(pids))
        {
            nint process = NativeMethods.OpenProcess(NativeMethods.PROCESS_DUP_HANDLE, false, pid);
            if (process == 0)
            {
                continue;
            }

            try
            {
                foreach (nint remote in handles)
                {
                    if (!IsSingletonHandle(process, remote))
                    {
                        continue;
                    }

                    if (NativeMethods.DuplicateHandle(process, remote, NativeMethods.GetCurrentProcess(), out nint local, 0, false, NativeMethods.DUPLICATE_CLOSE_SOURCE))
                    {
                        NativeMethods.CloseHandle(local);
                        closed++;
                        log?.Invoke($"Released singleton handle in Roblox process {pid}");
                    }
                }
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        return closed;
    }

    private static bool IsSingletonHandle(nint process, nint remote)
    {
        if (!NativeMethods.DuplicateHandle(process, remote, NativeMethods.GetCurrentProcess(), out nint local, 0, false, NativeMethods.DUPLICATE_SAME_ACCESS))
        {
            return false;
        }

        try
        {
            // Only query names of mutexes and events: name queries on some object types (e.g. pipes) can block.
            string type = HandleEnumerator.QueryObjectString(local, HandleEnumerator.ObjectTypeInformation);
            if (type is not ("Mutant" or "Event"))
            {
                return false;
            }

            string name = HandleEnumerator.QueryObjectString(local, HandleEnumerator.ObjectNameInformation);
            return name.Contains(SingletonPrefix, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NativeMethods.CloseHandle(local);
        }
    }
}

/// <summary>Enumerates open handles of given processes via NtQuerySystemInformation.</summary>
internal static class HandleEnumerator
{
    public const int ObjectNameInformation = 1;
    public const int ObjectTypeInformation = 2;
    private const int SystemExtendedHandleInformation = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntryEx
    {
        public nint Object;
        public nint UniqueProcessId;
        public nint HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    /// <summary>
    /// Returns only the mutex and event handles of the given processes. Object type numbers differ between Windows
    /// builds, so they are learned from a mutex and an event created here in this process.
    /// </summary>
    public static Dictionary<int, List<nint>> GetMutexAndEventHandles(IReadOnlySet<int> pids)
    {
        using var probeMutex = new Mutex(false);
        using var probeEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        int self = Environment.ProcessId;
        nint mutexHandle = probeMutex.SafeWaitHandle.DangerousGetHandle();
        nint eventHandle = probeEvent.SafeWaitHandle.DangerousGetHandle();

        var wanted = new HashSet<int>(pids) { self };
        List<HandleEntryEx> entries = Snapshot(wanted);
        var types = entries
            .Where(e => (int)e.UniqueProcessId == self && (e.HandleValue == mutexHandle || e.HandleValue == eventHandle))
            .Select(e => e.ObjectTypeIndex)
            .ToHashSet();

        var result = new Dictionary<int, List<nint>>();
        if (types.Count < 2)
        {
            // Couldn't identify both types; touch nothing rather than guess.
            return result;
        }

        foreach (HandleEntryEx entry in entries)
        {
            int pid = (int)entry.UniqueProcessId;
            if (pid == self || !pids.Contains(pid) || !types.Contains(entry.ObjectTypeIndex))
            {
                continue;
            }

            if (!result.TryGetValue(pid, out List<nint>? list))
            {
                result[pid] = list = [];
            }

            list.Add(entry.HandleValue);
        }

        return result;
    }

    private static List<HandleEntryEx> Snapshot(IReadOnlySet<int> pids)
    {
        var result = new List<HandleEntryEx>();
        int size = 1 << 20;
        nint buffer = 0;
        try
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                int status = NativeMethods.NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                if (status == 0)
                {
                    break;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = 0;
                if (status != NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                {
                    return result;
                }

                size = Math.Max(needed + (1 << 16), size * 2);
            }

            if (buffer == 0)
            {
                return result;
            }

            long count = Marshal.ReadIntPtr(buffer);
            int entrySize = Marshal.SizeOf<HandleEntryEx>();
            nint entry = buffer + (2 * IntPtr.Size);
            for (long i = 0; i < count; i++, entry += entrySize)
            {
                HandleEntryEx info = Marshal.PtrToStructure<HandleEntryEx>(entry);
                if (pids.Contains((int)info.UniqueProcessId))
                {
                    result.Add(info);
                }
            }

            return result;
        }
        finally
        {
            if (buffer != 0)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Reads the UNICODE_STRING at the start of an object type/name information block.</summary>
    public static string QueryObjectString(nint handle, int infoClass)
    {
        const int size = 2048;
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.NtQueryObject(handle, infoClass, buffer, size, out _) != 0)
            {
                return "";
            }

            ushort length = (ushort)Marshal.ReadInt16(buffer);
            nint text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return length == 0 || text == 0 ? "" : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
