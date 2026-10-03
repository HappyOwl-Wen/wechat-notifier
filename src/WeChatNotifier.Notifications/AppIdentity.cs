using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Core.Notifications;

namespace WeChatNotifier.Notifications;

/// <summary>
/// The notifier's identity towards the Windows notification platform. Unpackaged, so the Windows App SDK keeps
/// it in HKCU only (no admin rights):
///   HKCU\Software\Classes\AppUserModelId\WeChatNotifier   DisplayName, IconUri, CustomActivator={CLSID}
///   HKCU\Software\Classes\CLSID\{CLSID}\LocalServer32     "&lt;exe&gt;" ----AppNotificationActivated:
/// An explicit AppUserModelID keeps that one entry stable when the executable moves; without it the SDK derives
/// a new identity from every executable path.
/// </summary>
internal static class AppIdentity
{
    public const string AppUserModelId = "WeChatNotifier";
    public const string DisplayName = "WeChat Notifier";

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    /// <summary>Must run before AppNotificationManager.Default is first touched (it caches the id).</summary>
    public static int Apply() => SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

    /// <summary>
    /// True when the process runs with another app's package identity (e.g. started from inside an MSIX app):
    /// notifications would then be attributed to that app, so they are not used.
    /// </summary>
    public static bool HasForeignPackageIdentity()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
    }

    /// <summary>
    /// The activation server Windows starts when a notification is clicked while no notifier runs. The SDK
    /// writes it only when it first creates the registration, so it goes stale when the executable moves.
    /// </summary>
    public static bool ActivationServerIsCurrent(out string? registered)
    {
        registered = null;
        try
        {
            using var app = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
            if (app?.GetValue("CustomActivator") is not string clsid) return true; // nothing registered yet
            using var server = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{clsid}\LocalServer32");
            registered = server?.GetValue(null) as string;
            return registered is null || registered.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>Icon files kept in the assets folder (the newest registered ones; each under 2 KB).</summary>
    private const int KeptIcons = 16;

    /// <summary>
    /// The icon file Windows shows in the notification header (and in Settings), written from the embedded copy to
    /// LocalAppData: the registration stores this path, so it must not point into a build output folder. Its size is
    /// the one the header draws at on the primary display (<see cref="ToastScale"/>), so Windows does not scale it.
    /// The file name carries a hash of the content — Windows caches the image per path, so a new icon (or size) needs
    /// a new path. Windows also reads the path only once per sign-in and keeps drawing from it, so a new path shows
    /// from the next sign-in, and older files must stay: 2026-10-02, 0.6.7 registered a new file and deleted the old
    /// one, and the header icon was empty until a file existed at the old path again.
    /// </summary>
    public static string? EnsureIcon()
    {
        try
        {
            int size = ToastScale.HeaderIconSize(ToastScale.PrimaryDpi());
            using var source = typeof(AppIdentity).Assembly.GetManifestResourceStream($"WeChatNotifier.HeaderIcon-{size}.png");
            if (source is null) return null;
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            return Keep(AppIconPrefix, buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The name the notifications' header shows when they appear as WeChat's.</summary>
    public const string WeChatDisplayName = "微信";

    private const string AppIconPrefix = "AppIcon";
    private const string WeChatIconPrefix = "WeixinIcon";

    private static string AssetsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatNotifier", "assets");

    /// <summary>
    /// The name and icon file of the notifications' header: the notifier's own, or — <paramref name="asWeChat"/> —
    /// "微信" with WeChat's icon (from <paramref name="weChatExe"/>, else the one read from it before; the notifier's
    /// icon until WeChat was found once).
    /// </summary>
    public static (string Name, string? Icon) Identity(bool asWeChat, string? weChatExe) =>
        asWeChat ? (WeChatDisplayName, EnsureWeChatIcon(weChatExe) ?? EnsureIcon()) : (DisplayName, EnsureIcon());

    /// <summary>
    /// WeChat's icon at the header's size on the primary display, read from <paramref name="weChatExe"/> and kept like
    /// the notifier's own (<see cref="EnsureIcon"/>). Without the executable (WeChat not running yet at sign-in): the
    /// icon read before, at this size if there is one.
    /// </summary>
    public static string? EnsureWeChatIcon(string? weChatExe)
    {
        try
        {
            int size = ToastScale.Pixels(ToastScale.HeaderIconEffectivePixels, ToastScale.PrimaryDpi());
            if (weChatExe is not null && WeChatAppIcon.Extract(weChatExe, size) is { } icon) return Keep(WeChatIconPrefix, PngEncoder.Encode(icon));
            if (!Directory.Exists(AssetsFolder)) return null;
            var kept = new DirectoryInfo(AssetsFolder).EnumerateFiles(WeChatIconPrefix + "-*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            return (kept.FirstOrDefault(f => PngWidth(f.FullName) == size) ?? kept.FirstOrDefault())?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the header's name and icon into the existing registration, as the SDK's Register does (which cannot run
    /// again in a running process). Windows reads them once per sign-in, so they show from the next one.
    /// </summary>
    public static bool WriteRegistration(string name, string? icon)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}", writable: true);
            if (key is null) return false;
            key.SetValue("DisplayName", name, RegistryValueKind.ExpandString);
            if (icon is null) key.DeleteValue("IconUri", throwOnMissingValue: false);
            else key.SetValue("IconUri", icon, RegistryValueKind.ExpandString);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="png"/> to the assets folder under a name made from its content (once), marks it the
    /// newest, and deletes the oldest of its kind beyond <see cref="KeptIcons"/>.
    /// </summary>
    private static string Keep(string prefix, byte[] png)
    {
        Directory.CreateDirectory(AssetsFolder);
        var target = Path.Combine(AssetsFolder, $"{prefix}-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png), 0, 4)}.png");
        if (!File.Exists(target)) File.WriteAllBytes(target, png);
        else File.SetLastWriteTimeUtc(target, DateTime.UtcNow); // newest = most recently registered
        var older = new DirectoryInfo(AssetsFolder).EnumerateFiles(prefix + "*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeptIcons);
        foreach (var old in older)
        {
            if (!string.Equals(old.FullName, target, StringComparison.OrdinalIgnoreCase)) old.Delete();
        }
        return target;
    }

    /// <summary>Width from a PNG's header; 0 when it is no PNG.</summary>
    private static int PngWidth(string path)
    {
        Span<byte> head = stackalloc byte[24];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
            ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[16..])
            : 0;
    }
}
