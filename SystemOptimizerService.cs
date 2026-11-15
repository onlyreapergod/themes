using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Win11ProSuite.Native;

namespace Win11ProSuite.Services;

/// <summary>
/// Registry + Win32 animation speed optimizer with an automatic first-run backup of the
/// affected registry keys and a one-click restore.
/// </summary>
public sealed class SystemOptimizerService
{
    private const string DesktopKey = @"Control Panel\Desktop";
    private const string WindowMetricsKey = @"Control Panel\Desktop\WindowMetrics";
    private const string ThemesKey = @"Software\Microsoft\Windows\CurrentVersion\Themes";

    private const string DesktopSection = @"HKEY_CURRENT_USER\Control Panel\Desktop";
    private const string WindowMetricsSection = @"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics";

    private const string WindowsDefaultMenuShowDelay = "400";
    private const string WindowsDefaultMinAnimate = "1";

    private static readonly Regex StringValueRegex =
        new(@"^""(?<n>(?:[^""\\]|\\.)*)""=""(?<v>(?:[^""\\]|\\.)*)""$", RegexOptions.Compiled);

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly Action<string> _status;

    public SystemOptimizerService(SettingsService settingsService, AppSettings settings, Action<string> status)
    {
        _settingsService = settingsService;
        _settings = settings;
        _status = status;
    }

    /// <summary>Full-key safety copy of Desktop + Themes, taken before the first change.</summary>
    public string BackupPath => Path.Combine(AppPaths.DataDirectory, "win10_backup.reg");

    public bool BackupExists
    {
        get
        {
            try { return File.Exists(BackupPath) && new FileInfo(BackupPath).Length > 0; }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ backup

    public Task<bool> EnsureBackupAsync() => Task.Run(EnsureBackupCore);

    private bool EnsureBackupCore()
    {
        try
        {
            if (BackupExists) return true;

            Report("Backing up your current Windows settings…");

            string tempA = Path.Combine(Path.GetTempPath(), $"w11ps_{Guid.NewGuid():N}_a.reg");
            string tempB = Path.Combine(Path.GetTempPath(), $"w11ps_{Guid.NewGuid():N}_b.reg");

            try
            {
                RunReg("export", $@"HKCU\{DesktopKey}", tempA, "/y");
                RunReg("export", $@"HKCU\{ThemesKey}", tempB, "/y");

                const string header = "Windows Registry Editor Version 5.00";
                string a = File.ReadAllText(tempA, Encoding.Unicode);
                string b = File.ReadAllText(tempB, Encoding.Unicode)
                    .Replace(header, string.Empty, StringComparison.Ordinal)
                    .TrimStart('﻿', '\r', '\n');

                string merged = a.TrimEnd() + "\r\n\r\n" + b;
                File.WriteAllText(BackupPath, merged, new UnicodeEncoding(false, true));
            }
            finally
            {
                TryDelete(tempA);
                TryDelete(tempB);
            }

            CaptureUiEffectsIfNeeded();
            Report($"Backup saved to {BackupPath}");
            return true;
        }
        catch (Exception ex)
        {
            Report($"Backup failed: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ turbo

    public Task<bool> EnableTurboAsync() => Task.Run(() =>
    {
        try
        {
            if (!EnsureBackupCore())
            {
                Report("Turbo animations were not applied because the safety backup could not be created.");
                return false;
            }

            CaptureUiEffectsIfNeeded();
            Report("Applying turbo animation speed…");

            SetRegistryString(DesktopKey, "MenuShowDelay", "0");
            SetRegistryString(WindowMetricsKey, "MinAnimate", "0");

            bool live = ApplyLive(menuDelayMs: 0, minAnimate: false, uiEffects: true);
            Report(live
                ? "Turbo animations active — menus open instantly, no reboot needed."
                : "Turbo values saved, but Windows didn't accept the live update. Sign out and back in to finish applying.");
            return true;
        }
        catch (Exception ex)
        {
            Report($"Couldn't enable turbo animations: {ex.Message}");
            return false;
        }
    });

    public Task<bool> DisableTurboAsync() => Task.Run(() => RestoreAnimationValues("Turbo animations turned off — original animation timing restored."));

    public bool IsTurboActive()
    {
        try
        {
            using RegistryKey? desktop = Registry.CurrentUser.OpenSubKey(DesktopKey);
            using RegistryKey? metrics = Registry.CurrentUser.OpenSubKey(WindowMetricsKey);
            return string.Equals(desktop?.GetValue("MenuShowDelay") as string, "0", StringComparison.Ordinal)
                && string.Equals(metrics?.GetValue("MinAnimate") as string, "0", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// One-click restore: puts back the animation values saved in win10_backup.reg
    /// (MenuShowDelay, MinAnimate) and re-applies them live. Other values in the backed-up
    /// keys, such as your wallpaper, are deliberately left alone; the .reg file remains a
    /// full-key safety copy you can double-click to import manually.
    /// </summary>
    public Task<bool> RestoreBackupAsync() => Task.Run(() => RestoreAnimationValues("Windows 10 defaults restored from your backup."));

    private bool RestoreAnimationValues(string successMessage)
    {
        try
        {
            (string? delay, string? minAnimate, bool found) = ReadOriginalValues();

            if (!found)
            {
                delay = WindowsDefaultMenuShowDelay;
                minAnimate = WindowsDefaultMinAnimate;
            }

            WriteOrDeleteRegistryString(DesktopKey, "MenuShowDelay", delay);
            WriteOrDeleteRegistryString(WindowMetricsKey, "MinAnimate", minAnimate);

            int delayMs = int.TryParse(delay ?? WindowsDefaultMenuShowDelay, out int parsed) ? parsed : 400;
            bool animate = !string.Equals(minAnimate ?? WindowsDefaultMinAnimate, "0", StringComparison.Ordinal);

            bool live = ApplyLive(delayMs, animate, _settings.OriginalUiEffects ?? true);
            Report(live
                ? successMessage
                : "Original values written, but Windows didn't accept the live update. Sign out and back in to finish.");
            return true;
        }
        catch (Exception ex)
        {
            Report($"Restore failed: {ex.Message}");
            return false;
        }
    }

    private (string? MenuShowDelay, string? MinAnimate, bool Found) ReadOriginalValues()
    {
        if (!BackupExists) return (null, null, false);

        string section = string.Empty;
        string? delay = null;
        string? minAnimate = null;

        foreach (string raw in File.ReadLines(BackupPath, Encoding.Unicode))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            Match match = StringValueRegex.Match(line);
            if (!match.Success) continue;

            string name = match.Groups["n"].Value;
            if (section.Equals(DesktopSection, StringComparison.OrdinalIgnoreCase) && name == "MenuShowDelay")
            {
                delay = Unescape(match.Groups["v"].Value);
            }
            else if (section.Equals(WindowMetricsSection, StringComparison.OrdinalIgnoreCase) && name == "MinAnimate")
            {
                minAnimate = Unescape(match.Groups["v"].Value);
            }
        }

        return (delay, minAnimate, true);
    }

    // ------------------------------------------------------------------ Win32 live apply

    private bool ApplyLive(int menuDelayMs, bool minAnimate, bool uiEffects)
    {
        const uint flags = NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE;
        bool ok = true;

        try
        {
            ok &= NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETMENUSHOWDELAY, (uint)Math.Max(0, menuDelayMs), IntPtr.Zero, flags);

            var animation = new AnimationInfo
            {
                cbSize = (uint)Marshal.SizeOf<AnimationInfo>(),
                iMinAnimate = minAnimate ? 1 : 0
            };
            ok &= NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETANIMATION, animation.cbSize, ref animation, flags);

            ok &= NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETUIEFFECTS, 0, (IntPtr)(uiEffects ? 1 : 0), flags);
        }
        catch (Exception ex)
        {
            Report($"Win32 live update failed: {ex.Message}");
            return false;
        }

        return ok;
    }

    private void CaptureUiEffectsIfNeeded()
    {
        if (_settings.OriginalUiEffects is not null) return;

        try
        {
            int current = 0;
            if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETUIEFFECTS, 0, ref current, 0))
            {
                _settings.OriginalUiEffects = current != 0;
                _settingsService.Save(_settings);
            }
        }
        catch
        {
            // Not critical; restore falls back to "effects on".
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void SetRegistryString(string subKey, string name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($@"Couldn't open HKCU\{subKey} for writing.");
        key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void WriteOrDeleteRegistryString(string subKey, string name, string? value)
    {
        if (value is null)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            SetRegistryString(subKey, name, value);
        }
    }

    private static string Unescape(string value) => value.Replace("\\\\", "\\").Replace("\\\"", "\"");

    private static void RunReg(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "reg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start reg.exe.");
        string stderr = process.StandardError.ReadToEnd();
        _ = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(15000))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("reg.exe did not finish in time.");
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"reg.exe failed ({process.ExitCode}): {stderr.Trim()}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Report(string message) => _status(message);
}
