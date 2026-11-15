using System.Windows.Automation;
using Win11ProSuite.Native;

namespace Win11ProSuite.Services;

/// <summary>
/// Gives the Windows 10 taskbar a Windows 11 look: centered task icons, an acrylic dark
/// background and rounded outer corners. The work is done by a light background loop that
/// re-applies the styling whenever Explorer resets it, and everything is undone on Disable.
/// On Windows 11 the taskbar is already centered and rounded, so this does nothing there.
/// </summary>
public sealed class ShellStyleService : IDisposable
{
    private const int CornerRadiusDip = 14;
    private const uint AcrylicTint = 0xB81C1818; // AABBGGRR -> #18181C at ~72% opacity

    private readonly Action<string> _status;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _reportedNoTaskList;
    private int _lastWidth = -1;
    private int _lastHeight = -1;

    public ShellStyleService(Action<string> status)
    {
        _status = status;
    }

    private static bool IsWindows11OrLater => Environment.OSVersion.Version.Build >= 22000;

    public bool Enable()
    {
        if (_loop is not null) return true;

        if (IsWindows11OrLater)
        {
            _status("Windows 11 detected: the centered taskbar and rounded corners are already native, so nothing was changed.");
            return true;
        }

        if (NativeMethods.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero)
        {
            _status("Couldn't find the taskbar (Explorer may be restarting). Try again in a moment.");
            return false;
        }

        _reportedNoTaskList = false;
        _lastWidth = _lastHeight = -1;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _loop = Task.Factory.StartNew(() => RunLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        _status("Win11 dock styling on: centered icons, acrylic taskbar, rounded corners.");
        return true;
    }

    public bool Disable()
    {
        try
        {
            CancellationTokenSource? cts = _cts;
            Task? loop = _loop;
            _cts = null;
            _loop = null;

            cts?.Cancel();
            try { loop?.Wait(3000); } catch { }
            cts?.Dispose();

            if (loop is not null)
            {
                RestoreTaskbar();
            }

            _status("Taskbar restored to the stock Windows 10 look.");
            return true;
        }
        catch (Exception ex)
        {
            _status($"Couldn't fully restore the taskbar: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => Disable();

    // ------------------------------------------------------------------ loop

    private void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { ApplyOnce(); }
            catch { /* Explorer can restart or UI Automation can time out; try again next tick. */ }

            token.WaitHandle.WaitOne(200);
        }
    }

    private void ApplyOnce()
    {
        IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(tray, out NativeRect trayRect)) return;

        NativeMethods.SetAccent(tray, NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND, AcrylicTint);
        EnsureRoundedRegion(tray, trayRect);
        CenterTaskList(tray, trayRect);
    }

    private void EnsureRoundedRegion(IntPtr tray, NativeRect rect)
    {
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= height) return; // vertical taskbar: leave square

        // GetWindowRgn returns 0 (ERROR) when no region is set, which is how we notice Explorer reset it.
        IntPtr probe = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        int regionKind = NativeMethods.GetWindowRgn(tray, probe);
        NativeMethods.DeleteObject(probe);

        if (regionKind != 0 && width == _lastWidth && height == _lastHeight) return;

        uint dpi = NativeMethods.GetDpiForWindow(tray);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int diameter = (int)Math.Round(CornerRadiusDip * 2 * scale);

        int screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        bool dockedTop = rect.Top <= 0 && rect.Bottom < screenHeight / 2;

        // Extend the rounded rectangle past the screen-side edge so only the free edge gets rounded corners.
        IntPtr region = dockedTop
            ? NativeMethods.CreateRoundRectRgn(0, -diameter, width + 1, height + 1, diameter, diameter)
            : NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + diameter + 1, diameter, diameter);

        if (region != IntPtr.Zero && NativeMethods.SetWindowRgn(tray, region, true) == 0)
        {
            NativeMethods.DeleteObject(region); // on success the system owns the region
        }

        _lastWidth = width;
        _lastHeight = height;
    }

    private void CenterTaskList(IntPtr tray, NativeRect trayRect)
    {
        int trayWidth = trayRect.Right - trayRect.Left;
        int trayHeight = trayRect.Bottom - trayRect.Top;
        if (trayWidth <= trayHeight) return;

        if (!TryGetTaskList(tray, out IntPtr switcher, out IntPtr list))
        {
            if (!_reportedNoTaskList)
            {
                _reportedNoTaskList = true;
                _status("Centering skipped: this Windows build doesn't expose the classic taskbar button list. Acrylic and rounded corners are still applied.");
            }
            return;
        }

        if (!NativeMethods.GetWindowRect(list, out NativeRect listRect)) return;
        if (!NativeMethods.GetWindowRect(switcher, out NativeRect switcherRect)) return;

        double minLeft = double.MaxValue;
        double maxRight = double.MinValue;

        AutomationElement root = AutomationElement.FromHandle(list);
        AutomationElementCollection buttons = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        foreach (AutomationElement button in buttons)
        {
            try
            {
                System.Windows.Rect bounds = button.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width < 1 || double.IsInfinity(bounds.Left)) continue;
                minLeft = Math.Min(minLeft, bounds.Left);
                maxRight = Math.Max(maxRight, bounds.Right);
            }
            catch (ElementNotAvailableException)
            {
                // Button disappeared while we were measuring.
            }
        }

        if (minLeft == double.MaxValue || maxRight <= minLeft) return;

        double contentWidth = maxRight - minLeft;
        double buttonOffsetInList = minLeft - listRect.Left;
        double screenCenter = (trayRect.Left + trayRect.Right) / 2.0;
        double desiredFirstButtonLeft = screenCenter - contentWidth / 2.0;
        double desiredListLeftOnScreen = desiredFirstButtonLeft - buttonOffsetInList;

        int switcherWidth = switcherRect.Right - switcherRect.Left;
        int desiredX = (int)Math.Round(desiredListLeftOnScreen - switcherRect.Left);

        // Keep all icons inside the visible area of the parent window.
        int maxX = Math.Max(0, switcherWidth - (int)Math.Ceiling(buttonOffsetInList + contentWidth));
        desiredX = Math.Clamp(desiredX, 0, maxX);

        int currentX = listRect.Left - switcherRect.Left;
        if (Math.Abs(desiredX - currentX) < 2) return;

        NativeMethods.SetWindowPos(
            list, IntPtr.Zero,
            desiredX, listRect.Top - switcherRect.Top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static bool TryGetTaskList(IntPtr tray, out IntPtr switcher, out IntPtr list)
    {
        switcher = IntPtr.Zero;
        list = IntPtr.Zero;

        IntPtr rebar = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
        if (rebar == IntPtr.Zero) return false;

        switcher = NativeMethods.FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (switcher == IntPtr.Zero) return false;

        list = NativeMethods.FindWindowEx(switcher, IntPtr.Zero, "MSTaskListWClass", null);
        return list != IntPtr.Zero;
    }

    // ------------------------------------------------------------------ restore

    private void RestoreTaskbar()
    {
        IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;

        NativeMethods.SetWindowRgn(tray, IntPtr.Zero, true);
        NativeMethods.SetAccent(tray, NativeMethods.ACCENT_DISABLED, 0);

        if (TryGetTaskList(tray, out IntPtr switcher, out IntPtr list)
            && NativeMethods.GetWindowRect(list, out NativeRect listRect)
            && NativeMethods.GetWindowRect(switcher, out NativeRect switcherRect))
        {
            NativeMethods.SetWindowPos(
                list, IntPtr.Zero,
                0, listRect.Top - switcherRect.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        // Nudge Explorer to recompute its own taskbar appearance.
        NativeMethods.SendMessageTimeout(
            NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE, UIntPtr.Zero, "ImmersiveColorSet",
            NativeMethods.SMTO_ABORTIFHUNG, 200, out _);
    }
}
