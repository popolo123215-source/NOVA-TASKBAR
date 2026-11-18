using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Nova.WindowsClient.Services;

internal static class ProductivityQuickActionRunner
{
    private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appref-ms", ".bat", ".cmd", ".com", ".exe", ".hta", ".jar", ".js",
        ".jse", ".lnk", ".msi", ".ps1", ".psm1", ".reg", ".scr", ".url",
        ".vbe", ".vbs", ".wsf"
    };

    public static bool RequiresConfirmation(ProductivityQuickAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            ProductivityQuickActionKind.LaunchApplication or
            ProductivityQuickActionKind.LockWorkstation or
            ProductivityQuickActionKind.Sleep => true,
            ProductivityQuickActionKind.OpenPath =>
                RiskyExtensions.Contains(Path.GetExtension(action.Target)),
            _ => throw new ArgumentOutOfRangeException(nameof(action), "Неизвестный тип действия.")
        };
    }

    public static void Execute(ProductivityQuickAction action, bool confirmationAccepted)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (RequiresConfirmation(action) && !confirmationAccepted)
            throw new InvalidOperationException("Для этого действия требуется подтверждение пользователя.");

        switch (action.Kind)
        {
            case ProductivityQuickActionKind.OpenPath:
                StartShellPath(action.Target);
                break;
            case ProductivityQuickActionKind.LaunchApplication:
                var executable = Path.GetFullPath(action.Target);
                if (!File.Exists(executable) ||
                    !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetExtension(executable), ".lnk", StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("Выберите существующий EXE-файл или ярлык.", executable);
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                break;
            case ProductivityQuickActionKind.LockWorkstation:
                if (!LockWorkStation())
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не смогла заблокировать рабочую станцию.");
                break;
            case ProductivityQuickActionKind.Sleep:
                if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не смогла перевести компьютер в спящий режим.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), "Неизвестный тип действия.");
        }
    }

    private static void StartShellPath(string target)
    {
        var fullPath = Path.GetFullPath(target);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new FileNotFoundException("Файл или папка больше не существует.", fullPath);
        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("PowrProf.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
