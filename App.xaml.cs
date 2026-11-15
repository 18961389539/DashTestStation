using System.Threading;
using System.Windows;

namespace DustLoadScada;

public partial class App : Application
{
    private const string MutexName = @"Local\DustLoadScada.SingleInstance";
    private const string ActivateEventName = @"Local\DustLoadScada.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                EventWaitHandle.OpenExisting(ActivateEventName).Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent, (_, _) => Dispatcher.InvokeAsync(ActivateMainWindow), null, -1, false);

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateRegistration?.Unregister(null);
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}