using System.Runtime.InteropServices;

namespace Win11ProSuite.Services;

/// <summary>Adds/removes a shortcut to this exe in the current user's Startup folder.</summary>
public sealed class StartupService
{
    private const string ShortcutName = "Win11ProSuite.lnk";

    public string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    public bool IsEnabled => File.Exists(ShortcutPath);

    public (bool Success, string Message) SetEnabled(bool enable)
    {
        try
        {
            if (!enable)
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return (true, "Win11ProSuite will no longer start with Windows.");
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)
                || string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Startup can only be enabled from the published Win11ProSuite.exe, not from 'dotnet run'.");
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return (false, "Windows Script Host isn't available, so the startup shortcut couldn't be created.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);

            object shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Couldn't create the Windows shell object.");
            try
            {
                dynamic link = ((dynamic)shell).CreateShortcut(ShortcutPath);
                try
                {
                    link.TargetPath = exePath;
                    link.Arguments = "--startup";
                    link.WorkingDirectory = Path.GetDirectoryName(exePath);
                    link.IconLocation = exePath + ",0";
                    link.Description = "Win11ProSuite";
                    link.Save();
                }
                finally
                {
                    Marshal.FinalReleaseComObject((object)link);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }

            return (true, "Win11ProSuite will start quietly in the background when you sign in.");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't update the startup shortcut: {ex.Message}");
        }
    }
}
