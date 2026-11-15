using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Win11ProSuite.Native;
using Win11ProSuite.Services;

namespace Win11ProSuite;

/// <summary>
/// Frameless, always-on-top media "island" docked at the top-center of the primary screen.
/// Hidden from Alt+Tab, never steals focus, springs open on hover or when a new track starts.
/// </summary>
public partial class DynamicPillWindow : Window
{
    private const double CollapsedWidth = 250;
    private const double CollapsedHeight = 40;
    private const double ExpandedWidth = 392;
    private const double ExpandedHeight = 92;

    private static readonly Brush FallbackCover = CreateFrozenBrush(Color.FromRgb(0x2A, 0x2A, 0x32));

    private readonly MediaSessionService _media = new();
    private readonly SystemStatsService _stats = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _autoCollapseTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private string? _lastTrackKey;
    private bool _expanded;

    public DynamicPillWindow()
    {
        InitializeComponent();

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        _statsTimer.Tick += (_, _) => UpdateStats();
        _autoCollapseTimer.Tick += (_, _) =>
        {
            _autoCollapseTimer.Stop();
            if (!Pill.IsMouseOver) SetExpanded(false);
        };

        _media.SnapshotChanged += snapshot => Dispatcher.BeginInvoke(new Action(() => Render(snapshot)));
    }

    // ------------------------------------------------------------------ window plumbing

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        // Tool window = no Alt+Tab entry; no-activate = clicking the pill never steals focus.
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _stats.Sample(); // prime the CPU delta
        _statsTimer.Start();

        try
        {
            await _media.StartAsync();
        }
        catch (Exception)
        {
            TitleText.Text = "Media controls unavailable";
            ArtistText.Text = "Windows media sessions couldn't be opened";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statsTimer.Stop();
        _autoCollapseTimer.Stop();
        _media.Dispose();
    }

    // ------------------------------------------------------------------ rendering

    private void Render(MediaSnapshot snapshot)
    {
        PlayPauseButton.Content = snapshot.IsPlaying ? "" : "";

        if (!snapshot.HasSession || string.IsNullOrWhiteSpace(snapshot.Title))
        {
            TitleText.Text = "Nothing playing";
            ArtistText.Text = "Start music in Spotify or Chrome";
            SetCover(null);
            _lastTrackKey = null;
            return;
        }

        TitleText.Text = snapshot.Title;
        ArtistText.Text = !string.IsNullOrWhiteSpace(snapshot.Artist) ? snapshot.Artist
            : !string.IsNullOrWhiteSpace(snapshot.Album) ? snapshot.Album
            : snapshot.SourceApp;
        SetCover(snapshot.Thumbnail);

        string trackKey = $"{snapshot.Title}\u001F{snapshot.Artist}";
        if (trackKey != _lastTrackKey)
        {
            _lastTrackKey = trackKey;
            Expand(autoCollapse: true); // new song: pop open for 3 seconds
        }
    }

    private void SetCover(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            CoverBorder.Background = FallbackCover;
            CoverGlyph.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 96;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();

            CoverBorder.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            CoverGlyph.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CoverBorder.Background = FallbackCover;
            CoverGlyph.Visibility = Visibility.Visible;
        }
    }

    private void UpdateStats()
    {
        (int cpu, int ram) = _stats.Sample();
        CpuText.Text = $"CPU {cpu}%";
        RamText.Text = $"RAM {ram}%";
    }

    // ------------------------------------------------------------------ expand / collapse

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _autoCollapseTimer.Stop();
        SetExpanded(true);
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        // If the 3-second auto-expand is still running, let it finish the collapse.
        if (!_autoCollapseTimer.IsEnabled) SetExpanded(false);
    }

    private void Expand(bool autoCollapse)
    {
        if (autoCollapse)
        {
            _autoCollapseTimer.Stop();
            _autoCollapseTimer.Start();
        }
        SetExpanded(true);
    }

    private void SetExpanded(bool expand)
    {
        if (_expanded == expand) return;
        _expanded = expand;

        if (expand) UpdateStats();

        var spring = new BackEase
        {
            EasingMode = EasingMode.EaseOut,
            Amplitude = expand ? 0.6 : 0.35
        };
        TimeSpan duration = TimeSpan.FromMilliseconds(expand ? 480 : 380);

        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(expand ? ExpandedWidth : CollapsedWidth, duration)
        {
            EasingFunction = spring
        });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(expand ? ExpandedHeight : CollapsedHeight, duration)
        {
            EasingFunction = spring
        });

        ControlsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(
            expand ? 1 : 0,
            TimeSpan.FromMilliseconds(expand ? 240 : 120))
        {
            BeginTime = expand ? TimeSpan.FromMilliseconds(140) : TimeSpan.Zero
        });
        ControlsPanel.IsHitTestVisible = expand;
    }

    // ------------------------------------------------------------------ transport buttons

    private async void Previous_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
