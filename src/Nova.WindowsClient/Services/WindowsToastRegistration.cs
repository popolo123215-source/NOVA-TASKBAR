using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nova.WindowsClient.Services;

internal static class WindowsToastRegistration
{
    internal const string AppUserModelId = "NOVA.TaskBar";

    public static void Initialize(string? executablePath = null)
    {
        var resolvedExecutablePath = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь NOVA Taskbar.");
        if (!File.Exists(resolvedExecutablePath))
            throw new FileNotFoundException("Исполняемый файл NOVA Taskbar не найден.", resolvedExecutablePath);
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppUserModelId));
        EnsureStartMenuShortcut(resolvedExecutablePath);
    }

    private static void EnsureStartMenuShortcut(string executablePath)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programs))
            throw new InvalidOperationException("Не удалось определить папку меню «Пуск».");

        Directory.CreateDirectory(programs);
        var shortcutPath = Path.Combine(programs, "NOVA Taskbar.lnk");
        var shellLink = (IShellLinkW)new ShellLink();
        try
        {
            shellLink.SetPath(executablePath);
            shellLink.SetWorkingDirectory(Path.GetDirectoryName(executablePath)!);
            shellLink.SetDescription("Панель NOVA");
            shellLink.SetIconLocation(executablePath, 0);

            var propertyStore = (IPropertyStore)shellLink;
            var key = new PropertyKey(
                new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                5);
            var appId = PropertyVariant.FromString(AppUserModelId);
            try
            {
                Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, ref appId));
                Marshal.ThrowExceptionForHR(propertyStore.Commit());
            }
            finally
            {
                Marshal.FreeCoTaskMem(appId.StringValue);
            }

            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(shellLink);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder path, int pathLength, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription(StringBuilder description, int descriptionLength);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory(StringBuilder directory, int directoryLength);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(StringBuilder arguments, int argumentsLength);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropertyVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr StringValue;

        public static PropertyVariant FromString(string value) => new()
        {
            VariantType = 31,
            StringValue = Marshal.StringToCoTaskMemUni(value)
        };
    }
}
