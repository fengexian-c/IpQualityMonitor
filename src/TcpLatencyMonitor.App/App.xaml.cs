using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using TcpLatencyMonitor.App.Services;

namespace TcpLatencyMonitor.App;

public partial class App : Application
{
    private const string SingleInstanceKey = "IpQualityMonitor.MainInstance";
    private Window? _window;
    private AppInstance? _mainInstance;

    public App()
    {
        StartupDiagnostics.Write("Application constructor entered.");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            StartupDiagnostics.Write(
                "Unhandled AppDomain exception.",
                eventArgs.ExceptionObject as Exception);
        UnhandledException += (_, eventArgs) =>
            StartupDiagnostics.Write("Unhandled XAML exception.", eventArgs.Exception);

        try
        {
            InitializeComponent();
            StartupDiagnostics.Write("Application XAML initialized.");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Application XAML initialization failed.", exception);
            throw;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupDiagnostics.Write("OnLaunched entered.");
            var instanceKey=Environment.GetEnvironmentVariable("TCP_MONITOR_SMOKE_TEST")=="1"?SingleInstanceKey+".SelfTest":SingleInstanceKey;
            var instance = AppInstance.FindOrRegisterForKey(instanceKey);
            if (!instance.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                await instance.RedirectActivationToAsync(activation);
                Environment.Exit(0);
                return;
            }

            _mainInstance = instance;
            _mainInstance.Activated += OnInstanceActivated;

            _window = new MainWindow();
            _window.Activate();
            StartupDiagnostics.Write("Main window activated.");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("OnLaunched failed.", exception);
            throw;
        }
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (window is MainWindow mainWindow)
            {
                mainWindow.RestoreFromTray();
            }
            else
            {
                window.AppWindow.Show();
                window.Activate();
            }
        });
    }

}
