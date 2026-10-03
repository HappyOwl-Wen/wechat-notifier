using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WeChatNotifier.Setup;

internal static class Native
{
    public const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000;
    public const int IDYES = 6;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, IntPtr packageFullNames, ref uint bufferLength, char[]? buffer);

    /// <summary>Full names of the packages of a family installed for the current user (e.g. "Name_2.5.1.0_x64__publisher").</summary>
    public static string[] PackagesOfFamily(string familyName)
    {
        uint count = 0, length = 0;
        int error = GetPackagesByPackageFamily(familyName, ref count, IntPtr.Zero, ref length, null);
        if (error != ERROR_INSUFFICIENT_BUFFER || count == 0) return [];
        var pointers = Marshal.AllocHGlobal((int)count * IntPtr.Size);
        try
        {
            var buffer = new char[length];
            if (GetPackagesByPackageFamily(familyName, ref count, pointers, ref length, buffer) != 0) return [];
            return new string(buffer).Split(['\0'], StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            Marshal.FreeHGlobal(pointers);
        }
    }

    // ---- Start menu shortcut (IShellLinkW) ----

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] char[] file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] char[] name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] char[] dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] char[] args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] char[] iconPath, int max, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    public static void CreateShortcut(string shortcut, string target, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
            ((IPersistFile)link).Save(shortcut, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}
