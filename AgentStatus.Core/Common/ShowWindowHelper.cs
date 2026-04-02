using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AgentStatus.Core.Common;

/// <summary>
/// Static helper for finding and interacting with terminal windows hosting agent sessions.
/// Extracted from UI-specific command classes so both the WinUI app and CmdPal extension can use it.
/// </summary>
public static class ShowWindowHelper
{
    /// <summary>
    /// Walks up the process tree from the shell PID to find the host window.
    /// Returns the window handle as nint (IntPtr), or 0 if not found.
    /// </summary>
    public static nint FindTerminalWindow(int shellPid)
    {
        try
        {
            int currentPid = shellPid;

            for (int i = 0; i < 5; i++)
            {
                HWND hwnd = FindMainWindowForProcess(currentPid);
                if (hwnd != HWND.Null)
                    return (nint)hwnd;

                using ManagementObjectSearcher searcher = new(
                    $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {currentPid}");

                int parentPid = 0;
                foreach (ManagementObject obj in searcher.Get())
                {
                    parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                }

                if (parentPid == 0 || parentPid == currentPid)
                    break;

                currentPid = parentPid;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FindTerminalWindow error: {ex.Message}");
        }

        return 0;
    }

    private static HWND FindMainWindowForProcess(int pid)
    {
        HWND result = HWND.Null;

        try
        {
            Process proc = Process.GetProcessById(pid);
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                result = new HWND(proc.MainWindowHandle);
            }
        }
        catch
        {
            // Process may have exited
        }

        return result;
    }

    /// <summary>
    /// Determines the tab index for a shell PID by enumerating all shell processes
    /// under the terminal sorted by creation time (which matches tab order).
    /// </summary>
    public static int FindTabIndex(int shellPid)
    {
        try
        {
            int terminalPid = 0;
            using (ManagementObjectSearcher searcher = new(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {shellPid}"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    terminalPid = Convert.ToInt32(obj["ParentProcessId"]);
                }
            }

            if (terminalPid == 0)
                return -1;

            List<(int pid, DateTime created)> shells = new();
            using (ManagementObjectSearcher searcher = new(
                $"SELECT ProcessId, CreationDate FROM Win32_Process WHERE ParentProcessId = {terminalPid} AND (Name = 'pwsh.exe' OR Name = 'powershell.exe' OR Name = 'cmd.exe')"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    int pid = Convert.ToInt32(obj["ProcessId"]);
                    DateTime created = ManagementDateTimeConverter.ToDateTime(obj["CreationDate"]?.ToString() ?? "");
                    shells.Add((pid, created));
                }
            }

            shells.Sort((a, b) => a.created.CompareTo(b.created));

            for (int i = 0; i < shells.Count; i++)
            {
                if (shells[i].pid == shellPid)
                    return i;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FindTabIndex error: {ex.Message}");
        }

        return -1;
    }

    /// <summary>
    /// Uses wt.exe to switch to the specified tab index.
    /// </summary>
    public static void SwitchTerminalTab(int tabIndex)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "wt.exe",
                Arguments = $"-w 0 focus-tab -t {tabIndex}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SwitchTerminalTab error: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings the terminal window for the given shell PID to the foreground and switches to its tab.
    /// </summary>
    public static void BringToFront(int shellPid)
    {
        BringToFrontAsync(shellPid).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async version of <see cref="BringToFront"/>. Adds a short delay between
    /// foregrounding the window and switching tabs so that <c>wt.exe -w 0</c>
    /// targets the correct (now-MRU) terminal window.
    /// </summary>
    public static async Task BringToFrontAsync(int shellPid)
    {
        HWND hwnd = new((nint)FindTerminalWindow(shellPid));
        if (hwnd != HWND.Null)
        {
            if (PInvoke.IsIconic(hwnd))
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

            PInvoke.SetForegroundWindow(hwnd);
            PInvoke.BringWindowToTop(hwnd);

            int tabIndex = FindTabIndex(shellPid);
            if (tabIndex >= 0)
            {
                // Allow the OS to register the terminal as the MRU window
                // before wt.exe resolves -w 0.
                await Task.Delay(200);
                SwitchTerminalTab(tabIndex);
            }
        }
        else
        {
            Debug.WriteLine($"Could not find window for shell PID {shellPid}");
        }
    }

    /// <summary>
    /// Sends keyboard input to select a choice in the terminal's interactive picker.
    /// Brings the window to front first, then sends down-arrow N times + Enter.
    /// </summary>
    public static async Task SelectChoice(int shellPid, int choiceIndex)
    {
        try
        {
            BringToFront(shellPid);
            await Task.Delay(500);
            SendChoiceKeystrokes(choiceIndex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SelectChoice error: {ex.Message}");
        }
    }

    private static void SendChoiceKeystrokes(int choiceIndex)
    {
        int totalKeys = choiceIndex + 1;
        int totalInputs = totalKeys * 2;
        INPUT[] inputs = new INPUT[totalInputs];

        int idx = 0;

        for (int i = 0; i < choiceIndex; i++)
        {
            inputs[idx++] = MakeKeyInput(VIRTUAL_KEY.VK_DOWN, false);
            inputs[idx++] = MakeKeyInput(VIRTUAL_KEY.VK_DOWN, true);
        }

        inputs[idx++] = MakeKeyInput(VIRTUAL_KEY.VK_RETURN, false);
        inputs[idx++] = MakeKeyInput(VIRTUAL_KEY.VK_RETURN, true);

        unsafe
        {
            fixed (INPUT* pInputs = inputs)
            {
                PInvoke.SendInput((uint)inputs.Length, pInputs, Marshal.SizeOf<INPUT>());
            }
        }
    }

    private static INPUT MakeKeyInput(VIRTUAL_KEY key, bool keyUp)
    {
        INPUT input = new()
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
        };
        input.Anonymous.ki.wVk = key;
        input.Anonymous.ki.dwFlags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        return input;
    }
}
