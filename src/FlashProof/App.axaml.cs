using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FlashProof.Coordination;
using FlashProof.ViewModels;

namespace FlashProof;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The worker is this same exe with --worker (design §4); the UI never touches a device itself.
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path is unknown.");
            var coordinator = new Coordinator(() => WorkerProcess.Start(exe), TimeProvider.System, action => Dispatcher.UIThread.Post(action));
            var window = new MainWindow();
            var main = new MainViewModel(coordinator, TimeProvider.System, window);
            main.CloseConfirmed += window.Close;
            window.DataContext = main;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => coordinator.Dispose(); // the worker stops a running test as "app closed" (design §5.4)
        }

        base.OnFrameworkInitializationCompleted();
    }
}
