using System.Text;
using Avalonia;
using FlashProof.Windows;

namespace FlashProof;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--worker"])
            return RunWorker();
        using var instance = SingleInstance.TryAcquire(Text.AppName);
        if (instance is null)
        {
            SingleInstance.ShowOtherCopy();
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>The headless worker (design §4): JSON lines on stdin and stdout, in UTF-8 whatever the console's code page.</summary>
    static int RunWorker()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
        new Worker(input, output, new WindowsDevices()).RunAsync().GetAwaiter().GetResult();
        return 0;
    }
}
