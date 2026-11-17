using Microsoft.Win32;
using System.IO;

namespace Nova.WindowsClient.Services;

internal static class ObsInstallationLocator
{
    public static string? FindExecutable()
    {
        var candidates = new List<string>();
        AddUninstallLocations(RegistryHive.LocalMachine, RegistryView.Registry64, candidates);
        AddUninstallLocations(RegistryHive.LocalMachine, RegistryView.Registry32, candidates);
        AddUninstallLocations(RegistryHive.CurrentUser, RegistryView.Default, candidates);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        AddCandidate(candidates, Path.Combine(programFiles, "obs-studio"));
        AddCandidate(candidates, Path.Combine(programFilesX86, "obs-studio"));
        AddCandidate(candidates, Path.Combine(programFiles, "Steam", "steamapps", "common", "OBS Studio"));
        AddCandidate(candidates, Path.Combine(programFilesX86, "Steam", "steamapps", "common", "OBS Studio"));

        using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steamPath = steamKey?.GetValue("SteamPath") as string;
        if (!string.IsNullOrWhiteSpace(steamPath))
            AddCandidate(candidates, Path.Combine(steamPath, "steamapps", "common", "OBS Studio"));

        return candidates.Select(Path.GetFullPath)
            .SelectMany(directory => new[]
            {
                Path.Combine(directory, "bin", "64bit", "obs64.exe"),
                Path.Combine(directory, "bin", "32bit", "obs32.exe")
            })
            .FirstOrDefault(File.Exists);
    }

    private static void AddUninstallLocations(RegistryHive hive, RegistryView view, ICollection<string> candidates)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry is null || !string.Equals(entry.GetValue("DisplayName") as string, "OBS Studio", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.GetValue("InstallLocation") is string installLocation && !string.IsNullOrWhiteSpace(installLocation))
                    AddCandidate(candidates, installLocation);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    private static void AddCandidate(ICollection<string> candidates, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            candidates.Add(path);
    }
}
