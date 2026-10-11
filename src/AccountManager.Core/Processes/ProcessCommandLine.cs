using System.Management;

namespace AccountManager.Core.Processes;

/// <summary>Reads process command lines through WMI (works for same-user processes without elevation).</summary>
public static class ProcessCommandLine
{
    /// <summary>Command lines of every running process with the given executable name, keyed by process id.</summary>
    public static Dictionary<int, string> GetAll(string executableName)
    {
        var result = new Dictionary<int, string>();
        try
        {
            string escaped = executableName.Replace("'", "''", StringComparison.Ordinal);
            using var searcher = new ManagementObjectSearcher($"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{escaped}'");
            using ManagementObjectCollection items = searcher.Get();
            foreach (ManagementBaseObject item in items)
            {
                using (item)
                {
                    if (item["ProcessId"] is uint pid && item["CommandLine"] is string commandLine)
                    {
                        result[(int)pid] = commandLine;
                    }
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }

        return result;
    }
}
