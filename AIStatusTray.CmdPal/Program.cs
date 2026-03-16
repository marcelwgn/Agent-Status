using System;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace AIStatusTrayCmdPal;

public class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            ComServer server = new();

            ManualResetEvent extensionDisposedEvent = new(false);

            AIStatusExtension extensionInstance = new(extensionDisposedEvent);
            server.RegisterClass<AIStatusExtension, IExtension>(() => extensionInstance);
            server.Start();

            extensionDisposedEvent.WaitOne();
            server.Stop();
            server.UnsafeDispose();
        }
        else
        {
            Console.WriteLine("Not being launched as an Extension... exiting.");
        }
    }
}
