using System.Diagnostics;

namespace FlashProof.Windows;

/// <summary>
/// One copy of the app per Windows sign-in: two copies could each start a Full test on the same drive. Opening the app
/// again brings the open window to the front instead. The worker (<c>--worker</c>) never takes part.
/// </summary>
public static class SingleInstance
{
    /// <summary>Takes the app's lock, or returns null if another copy holds it. Keep the result until the app exits.</summary>
    public static Mutex? TryAcquire(string appName)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{appName}-app", out bool createdNew);
        if (createdNew)
            return mutex;
        mutex.Dispose();
        return null;
    }

    /// <summary>Brings the other copy's window to the front, restored if minimised. The user just started this copy, so Windows allows it.</summary>
    public static void ShowOtherCopy()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(self.ProcessName))
        {
            using (process)
            {
                nint window = process.Id == self.Id ? 0 : process.MainWindowHandle;
                if (window == 0)
                    continue; // this copy, or a worker, which has no window
                if (Native.IsIconic(window))
                    Native.ShowWindow(window, Native.SwRestore);
                Native.SetForegroundWindow(window);
                return;
            }
        }
    }
}
