using AgentStatus.Core.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AgentStatusTaskbar;

/// <summary>
/// Command that selects a specific choice in an agent session's interactive prompt.
/// Brings the host window to front, switches to the correct tab, and sends
/// keystrokes to navigate to the choice and press Enter.
/// </summary>
internal partial class SelectChoiceCommand : InvokableCommand
{
    private readonly AISessionInfo _session;
    private readonly int _choiceIndex;

    public SelectChoiceCommand(AISessionInfo session, int choiceIndex, string choiceText)
    {
        _session = session;
        _choiceIndex = choiceIndex;
        Id = $"session-choice-{session.SessionId}-{choiceIndex}";
        Name = choiceText;
        Icon = new IconInfo("\uE73E");
    }

    public override ICommandResult Invoke()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                HWND hwnd = ShowWindowCommand.FindTerminalWindowPublic(_session.ShellPid);
                if (hwnd != HWND.Null)
                {
                    if (PInvoke.IsIconic(hwnd))
                        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

                    PInvoke.SetForegroundWindow(hwnd);
                    PInvoke.BringWindowToTop(hwnd);
                }

                int tabIndex = ShowWindowCommand.FindTabIndexPublic(_session.ShellPid);
                if (tabIndex >= 0)
                {
                    ShowWindowCommand.SwitchTerminalTabPublic(tabIndex);
                }

                await Task.Delay(500);

                SendChoiceKeystrokes(_choiceIndex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SelectChoiceCommand error: {ex.Message}");
            }
        });

        return CommandResult.KeepOpen();
    }

    /// <summary>
    /// Sends keyboard input to select a choice in the terminal's interactive picker.
    /// Sends down-arrow N times, then Enter.
    /// </summary>
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
