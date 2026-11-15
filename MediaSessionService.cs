using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Win11ProSuite.Services;

public sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    byte[]? Thumbnail,
    bool HasSession,
    string SourceApp);

/// <summary>
/// Wraps the Windows GlobalSystemMediaTransportControlsSessionManager (SMTC) so the pill can show
/// whatever Spotify, Chrome, YouTube, etc. is currently playing. Events fire on background threads.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _lastThumbKey;
    private byte[]? _lastThumb;
    private bool _disposed;

    public event Action<MediaSnapshot>? SnapshotChanged;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        await AttachAsync(_manager.GetCurrentSession());
    }

    public async Task PreviousAsync()
    {
        var session = _session;
        if (session is null) return;
        try { await session.TrySkipPreviousAsync(); } catch { }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null) return;
        try { await session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        var session = _session;
        if (session is null) return;
        try { await session.TrySkipNextAsync(); } catch { }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (_disposed) return;
        _ = AttachAsync(sender.GetCurrentSession());
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _session)) return;
        _ = PublishAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (_disposed || !ReferenceEquals(sender, _session)) return;
        _ = PublishAsync();
    }

    private async Task AttachAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        await _gate.WaitAsync();
        try
        {
            DetachCurrent();
            _session = session;
            _lastThumbKey = null;
            _lastThumb = null;

            if (session is not null)
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }

            await PublishCoreAsync();
        }
        catch
        {
            // A session can vanish mid-attach (app closed); the next session-changed event recovers.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PublishAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await PublishCoreAsync();
        }
        catch
        {
            // Ignore transient failures while a media app is starting or closing.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PublishCoreAsync()
    {
        var session = _session;
        if (session is null)
        {
            Raise(new MediaSnapshot(string.Empty, string.Empty, string.Empty, false, null, false, string.Empty));
            return;
        }

        var properties = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        string title = properties?.Title ?? string.Empty;
        string artist = properties?.Artist ?? string.Empty;
        string album = properties?.AlbumTitle ?? string.Empty;

        // Thumbnails are heavier than metadata, so only reload when the track changes
        // (or while the app hasn't supplied the artwork yet).
        string key = $"{title}\u001F{artist}\u001F{album}";
        if (key != _lastThumbKey || _lastThumb is null)
        {
            _lastThumb = await ReadThumbnailAsync(properties?.Thumbnail);
            _lastThumbKey = key;
        }

        Raise(new MediaSnapshot(title, artist, album, playing, _lastThumb, true, FriendlySource(session.SourceAppUserModelId)));
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;

        try
        {
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            ulong size = stream.Size;
            if (size == 0 || size > 8 * 1024 * 1024) return null;

            var bytes = new byte[(int)size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static string FriendlySource(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return string.Empty;
        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (appId.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        return appId;
    }

    private void Raise(MediaSnapshot snapshot)
    {
        if (_disposed) return;
        SnapshotChanged?.Invoke(snapshot);
    }

    private void DetachCurrent()
    {
        if (_session is null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        catch
        {
            // Session already gone.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { if (_manager is not null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged; } catch { }
        DetachCurrent();
        _session = null;
        _manager = null;
    }
}
