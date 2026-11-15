using System.Windows.Threading;
using Win11ProSuite.Services;

namespace Win11ProSuite.ViewModels;

/// <summary>
/// Drives the dashboard. Each toggle applies its feature as soon as it flips and reverts itself
/// (with a status message) if the operation fails. Operations are serialized so rapid clicking is safe.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly SystemOptimizerService _optimizer;
    private readonly ShellStyleService _shell;
    private readonly StartupService _startup = new();
    private readonly SemaphoreSlim _opLock = new(1, 1);

    private DynamicPillWindow? _pill;
    private bool _suppress;
    private bool _disposed;

    private bool _turboEnabled;
    private bool _pillEnabled;
    private bool _dockEnabled;
    private bool _startupEnabled;
    private string _statusText = "Ready.";
    private string _licenseText = string.Empty;
    private string _backupInfo = string.Empty;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = _settingsService.Load();
        _optimizer = new SystemOptimizerService(_settingsService, _settings, PostStatus);
        _shell = new ShellStyleService(PostStatus);
        RefreshBackupInfo();
    }

    // ------------------------------------------------------------------ bindable state

    public bool TurboEnabled
    {
        get => _turboEnabled;
        set
        {
            if (SetProperty(ref _turboEnabled, value) && !_suppress)
            {
                _ = RunExclusiveAsync(() => ApplyTurboAsync(value));
            }
        }
    }

    public bool PillEnabled
    {
        get => _pillEnabled;
        set
        {
            if (SetProperty(ref _pillEnabled, value) && !_suppress)
            {
                OnPropertyChanged(nameof(KeepsRunningInBackground));
                _ = RunExclusiveAsync(() => ApplyPillAsync(value));
            }
        }
    }

    public bool DockEnabled
    {
        get => _dockEnabled;
        set
        {
            if (SetProperty(ref _dockEnabled, value) && !_suppress)
            {
                OnPropertyChanged(nameof(KeepsRunningInBackground));
                _ = RunExclusiveAsync(() => ApplyDockAsync(value));
            }
        }
    }

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (SetProperty(ref _startupEnabled, value) && !_suppress)
            {
                _ = RunExclusiveAsync(() => ApplyStartupAsync(value));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string LicenseText
    {
        get => _licenseText;
        set => SetProperty(ref _licenseText, value);
    }

    public string BackupInfo
    {
        get => _backupInfo;
        private set => SetProperty(ref _backupInfo, value);
    }

    public string BackupPath => _optimizer.BackupPath;

    public bool BackupExists => _optimizer.BackupExists;

    /// <summary>True when closing the window should hide it instead of quitting.</summary>
    public bool KeepsRunningInBackground => PillEnabled || DockEnabled;

    public void SetStatus(string message) => PostStatus(message);

    // ------------------------------------------------------------------ lifecycle

    public async Task InitializeAsync()
    {
        try
        {
            // Take the safety backup before anything can change.
            await _optimizer.EnsureBackupAsync();
            RefreshBackupInfo();

            bool turboActive = _optimizer.IsTurboActive();
            bool startupActive = _startup.IsEnabled;
            SetSilently(() =>
            {
                TurboEnabled = turboActive;
                StartupEnabled = startupActive;
            });

            // Re-apply what the user had switched on last time.
            if (_settings.TurboEnabled && !turboActive) TurboEnabled = true;
            if (_settings.PillEnabled) PillEnabled = true;
            if (_settings.DockEnabled) DockEnabled = true;
        }
        catch (Exception ex)
        {
            PostStatus($"Startup check failed: {ex.Message}");
        }
    }

    public Task<bool> CreateBackupAsync() => RunBackupAsync();

    private async Task<bool> RunBackupAsync()
    {
        bool ok = await _optimizer.EnsureBackupAsync();
        RefreshBackupInfo();
        return ok;
    }

    /// <summary>Turns everything off and puts the original Windows 10 animation settings back.</summary>
    public async Task<bool> RestoreDefaultsAsync()
    {
        await _opLock.WaitAsync();
        try
        {
            PostStatus("Restoring Windows 10 defaults…");

            StopPill();
            await Task.Run(() => _shell.Disable());
            bool ok = await _optimizer.RestoreBackupAsync();

            SetSilently(() =>
            {
                PillEnabled = false;
                DockEnabled = false;
                if (ok) TurboEnabled = false;
            });

            _settings.PillEnabled = false;
            _settings.DockEnabled = false;
            if (ok) _settings.TurboEnabled = false;
            _settingsService.Save(_settings);

            OnPropertyChanged(nameof(KeepsRunningInBackground));
            return ok;
        }
        finally
        {
            _opLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { StopPill(); } catch { }
        try { _shell.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------ feature handlers

    private async Task ApplyTurboAsync(bool enable)
    {
        bool ok = enable ? await _optimizer.EnableTurboAsync() : await _optimizer.DisableTurboAsync();
        if (ok)
        {
            _settings.TurboEnabled = enable;
            _settingsService.Save(_settings);
            RefreshBackupInfo();
        }
        else
        {
            SetSilently(() => TurboEnabled = !enable);
        }
    }

    private Task ApplyPillAsync(bool enable)
    {
        try
        {
            if (enable)
            {
                if (_pill is null)
                {
                    var pill = new DynamicPillWindow();
                    pill.Closed += (_, _) =>
                    {
                        if (ReferenceEquals(_pill, pill)) _pill = null;
                    };
                    _pill = pill;
                    pill.Show();
                }
                PostStatus("Dynamic Media Island launched.");
            }
            else
            {
                StopPill();
                PostStatus("Dynamic Media Island closed.");
            }

            _settings.PillEnabled = enable;
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            PostStatus($"Media Island failed: {ex.Message}");
            SetSilently(() => PillEnabled = !enable);
            OnPropertyChanged(nameof(KeepsRunningInBackground));
        }

        return Task.CompletedTask;
    }

    private async Task ApplyDockAsync(bool enable)
    {
        bool ok = await Task.Run(() => enable ? _shell.Enable() : _shell.Disable());
        if (ok)
        {
            _settings.DockEnabled = enable;
            _settingsService.Save(_settings);
        }
        else
        {
            SetSilently(() => DockEnabled = !enable);
            OnPropertyChanged(nameof(KeepsRunningInBackground));
        }
    }

    private async Task ApplyStartupAsync(bool enable)
    {
        (bool success, string message) = await Task.Run(() => _startup.SetEnabled(enable));
        PostStatus(message);
        if (!success)
        {
            SetSilently(() => StartupEnabled = !enable);
        }
    }

    // ------------------------------------------------------------------ helpers

    private void StopPill()
    {
        DynamicPillWindow? pill = _pill;
        _pill = null;
        pill?.Close();
    }

    private async Task RunExclusiveAsync(Func<Task> work)
    {
        await _opLock.WaitAsync();
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            PostStatus($"Unexpected error: {ex.Message}");
        }
        finally
        {
            _opLock.Release();
        }
    }

    private void SetSilently(Action change)
    {
        bool previous = _suppress;
        _suppress = true;
        try { change(); }
        finally { _suppress = previous; }
    }

    private void RefreshBackupInfo()
    {
        BackupInfo = _optimizer.BackupExists
            ? $"Backup saved: {_optimizer.BackupPath}"
            : "No backup yet. Click the button below to create one.";
    }

    private void PostStatus(string message)
    {
        if (_dispatcher.CheckAccess())
        {
            StatusText = message;
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(() => StatusText = message));
        }
    }
}
