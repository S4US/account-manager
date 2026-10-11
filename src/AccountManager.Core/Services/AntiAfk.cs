using System.Runtime.InteropServices;
using AccountManager.Core.Native;
using AccountManager.Core.Processes;

namespace AccountManager.Core.Services;

/// <summary>
/// Keeps idle clients from being disconnected for inactivity by briefly focusing each window and tapping a key
/// that only nudges the camera (Page Up, then Page Down). Restores the window state and the user's foreground
/// window afterwards. Input is never blocked; a pass takes about a second per client.
/// </summary>
public static class AntiAfk
{
    private const ushort ScanPageUp = 0x49;
    private const ushort ScanPageDown = 0x51;

    /// <summary>Sends the keep-alive to each window. Returns how many windows were reached.</summary>
    public static async Task<int> PulseAsync(IReadOnlyList<nint> windows, bool restoreMinimized, CancellationToken ct = default)
    {
        nint previous = WindowTools.Foreground;
        int reached = 0;
        foreach (nint hwnd in windows)
        {
            ct.ThrowIfCancellationRequested();
            bool wasMinimized = WindowTools.IsMinimized(hwnd);
            if (!WindowTools.Focus(hwnd))
            {
                continue;
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
            if (WindowTools.Foreground != hwnd)
            {
                // Never type into a window we didn't manage to focus.
                continue;
            }

            await TapAsync(ScanPageUp, ct).ConfigureAwait(false);
            await Task.Delay(120, ct).ConfigureAwait(false);
            await TapAsync(ScanPageDown, ct).ConfigureAwait(false);
            reached++;

            await Task.Delay(100, ct).ConfigureAwait(false);
            if (wasMinimized && restoreMinimized)
            {
                WindowTools.Minimize(hwnd);
            }
        }

        if (previous != 0)
        {
            WindowTools.Focus(previous);
        }

        return reached;
    }

    private static async Task TapAsync(ushort scanCode, CancellationToken ct)
    {
        Send(scanCode, keyUp: false);
        await Task.Delay(40, ct).ConfigureAwait(false);
        Send(scanCode, keyUp: true);
    }

    private static void Send(ushort scanCode, bool keyUp)
    {
        uint flags = NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_EXTENDEDKEY;
        if (keyUp)
        {
            flags |= NativeMethods.KEYEVENTF_KEYUP;
        }

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wScan = scanCode, dwFlags = flags } },
        };
        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
