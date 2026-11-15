using System.Windows;
using Win11ProSuite.Services;

namespace Win11ProSuite;

/// <summary>
/// Application entry: single-instance guard, then the dashboard.
/// Launching the exe a second time simply brings the first instance's dashboard forward.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Win11ProSuite.SingleInstance";
    private const string ShowEventName = "Win11ProSuite.ShowDashboard";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private MainWindow? _main;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };

        _mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }
        _ownsMutex = true;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        try
        {
            _main = new MainWindow();
            MainWindow = _main;
            _main.ViewModel.LicenseText = "Free Edition • Windows 10 & 11";

            await _main.ViewModel.InitializeAsync();

            bool launchedFromStartup = e.Args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));
            if (!launchedFromStartup)
            {
                _main.Show();
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show($"Win11ProSuite couldn't start:\n\n{ex.Message}", "Win11ProSuite",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _main?.ViewModel.Dispose(); } catch { /* best effort */ }
        try { _showWait?.Unregister(null); } catch { }
        try { _showEvent?.Dispose(); } catch { }
        try
        {
            if (_ownsMutex) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        if (_main is null) return;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(ShowEventName);
            existing.Set();
        }
        catch
        {
            // The first instance is still starting up; nothing to signal.
        }
    }

    private static void LogError(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppPaths.DataDirectory, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
