using System.Diagnostics;
using System.Runtime.InteropServices;
using AccountManager.Core.Native;
using AccountManager.Core.Roblox;

namespace AccountManager.Core.Processes;

/// <summary>
/// Lets several Roblox clients run side by side.
///
/// Roblox enforces a single client through named kernel objects ("ROBLOX_singletonMutex" / "ROBLOX_singletonEvent").
/// The primary technique is to create and hold those objects in this process before any client starts, which is
/// what the client checks for. As a fallback for clients that were already running when the manager started, the
/// singleton handles inside those client processes can be closed, which releases the objects.
/// </summary>
public sealed class MultiInstanceGuard : IDisposable
{
    private static readonly string[] SingletonNames = ["ROBLOX_singletonMutex", "ROBLOX_singletonEvent"];
    private readonly object _lock = new();
    private Thread? _holder;
    private ManualResetEventSlim? _stop;

    public bool IsHolding
    {
        get
        {
            lock (_lock)
            {
                return _holder is not null;
            }
        }
    }

    /// <summary>
    /// Creates and holds the singleton objects. Safe to call repeatedly. Mutex ownership belongs to a thread, so the
    /// objects live on a dedicated thread that stays alive until <see cref="Release"/>.
    /// </summary>
    public void Acquire()
    {
        lock (_lock)
        {
            if (_holder is not null)
            {
                return;
            }

            var stop = new ManualResetEventSlim(false);
            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                using var mutex = new Mutex(initiallyOwned: true, SingletonNames[0], out _);
                using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, SingletonNames[1], out _);
                ready.Set();
                stop.Wait();
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not owned (another process created it first); nothing to release.
                }
            })
            {
                IsBackground = true,
                Name = "Roblox singleton holder",
            };
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            _holder = thread;
            _stop = stop;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_holder is null)
            {
                return;
            }

            _stop!.Set();
            _holder.Join(TimeSpan.FromSeconds(2));
            _stop.Dispose();
            _holder = null;
            _stop = null;
        }
    }

    public void Dispose() => Release();

    /// <summary>
    /// Closes singleton handles held by running Roblox clients. Returns how many handles were closed.
    /// Only touches RobloxPlayerBeta processes owned by the current user.
    /// </summary>
    public static int CloseSingletonHandlesInRunningClients(Action<string>? log = null)
    {
        Process[] clients = Process.GetProcessesByName(RobloxInstallation.PlayerProcessName);
        if (clients.Length == 0)
        {
            return 0;
        }

        var pids = clients.Select(p => p.Id).ToHashSet();
        foreach (Process p in clients)
        {
            p.Dispose();
        }

        int closed = 0;
        foreach ((int pid, List<nint> handles) in HandleEnumerator.GetHandlesByProcess(pids))
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
            return SingletonNames.Any(n => name.EndsWith(n, StringComparison.OrdinalIgnoreCase));
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

    public static Dictionary<int, List<nint>> GetHandlesByProcess(IReadOnlySet<int> pids)
    {
        var result = new Dictionary<int, List<nint>>();
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
                int pid = (int)info.UniqueProcessId;
                if (!pids.Contains(pid))
                {
                    continue;
                }

                if (!result.TryGetValue(pid, out List<nint>? list))
                {
                    result[pid] = list = [];
                }

                list.Add(info.HandleValue);
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
