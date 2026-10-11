using AccountManager.Core.Native;

namespace AccountManager.Core.Processes;

/// <summary>Finds and arranges top-level windows of client processes.</summary>
public static class WindowTools
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>Returns the main visible top-level window of a process, or 0.</summary>
    public static nint FindMainWindow(int processId)
    {
        nint found = 0;
        var buffer = new char[64];
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != processId || !NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                // Roblox's game window class; accept any visible window of the process as a fallback.
                int length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Length);
                string className = new(buffer, 0, Math.Max(0, length));
                found = hwnd;
                return className != "WINDOWSCLIENT";
            },
            0);
        return found;
    }

    public static void Minimize(nint hwnd)
    {
        if (hwnd != 0 && !NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
        }
    }

    public static void Restore(nint hwnd)
    {
        if (hwnd != 0)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }
    }

    public static void SetTitle(nint hwnd, string title)
    {
        if (hwnd != 0)
        {
            NativeMethods.SetWindowTextW(hwnd, title);
        }
    }

    public static bool IsMinimized(nint hwnd) => hwnd != 0 && NativeMethods.IsIconic(hwnd);

    /// <summary>Arranges the windows in an even grid across the primary monitor's work area.</summary>
    public static void Tile(IReadOnlyList<nint> windows)
    {
        List<nint> valid = windows.Where(w => w != 0 && NativeMethods.IsWindow(w)).ToList();
        if (valid.Count == 0)
        {
            return;
        }

        var area = new NativeMethods.RECT();
        NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETWORKAREA, 0, ref area, 0);
        (int columns, int rows) = GridFor(valid.Count);
        int width = (area.Right - area.Left) / columns;
        int height = (area.Bottom - area.Top) / rows;

        for (int i = 0; i < valid.Count; i++)
        {
            int x = area.Left + ((i % columns) * width);
            int y = area.Top + ((i / columns) * height);
            NativeMethods.ShowWindow(valid[i], NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.SetWindowPos(valid[i], 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    /// <summary>Smallest near-square grid that fits <paramref name="count"/> cells, wider than tall.</summary>
    public static (int Columns, int Rows) GridFor(int count)
    {
        if (count <= 0)
        {
            return (1, 1);
        }

        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling(count / (double)columns);
        return (columns, rows);
    }

    /// <summary>Brings a window to the foreground, working around Windows' foreground lock.</summary>
    public static bool Focus(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        if (NativeMethods.SetForegroundWindow(hwnd))
        {
            return true;
        }

        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        uint current = NativeMethods.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != current && NativeMethods.AttachThreadInput(current, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            return NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(current, foregroundThread, false);
            }
        }
    }

    public static nint Foreground => NativeMethods.GetForegroundWindow();
}
