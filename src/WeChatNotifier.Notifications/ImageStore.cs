using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Diagnostics;

namespace WeChatNotifier.Notifications;

/// <summary>
/// Temporary image files for notifications (spec §7, §14): Windows shows notification images only from files.
/// %LOCALAPPDATA%\WeChatNotifier\cache, random file names (no conversation names). A file is deleted when its
/// notification is replaced or withdrawn; whatever is left is deleted at the next start once its notification
/// can no longer exist (expired, or from before the last restart, since notifications expire on reboot).
/// Used from the notification thread only.
/// </summary>
internal sealed class ImageStore
{
    /// <summary>Upper bound for files kept at once; the oldest go first.</summary>
    internal const int MaxFiles = 300;

    private readonly DiagnosticLog _log;

    public ImageStore(DiagnosticLog log) => _log = log;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatNotifier", "cache");

    /// <returns>The file, or null when it could not be written (the notification then goes without the image).</returns>
    public string? Save(Bitmap32 image)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, PngEncoder.Encode(image));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("images", $"could not write a notification image: {ex.GetType().Name}");
            return null;
        }
    }

    public static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes files whose notifications cannot exist any more, and the oldest beyond <see cref="MaxFiles"/>.</summary>
    public int CleanUp(TimeSpan notificationLifetime)
    {
        if (!Directory.Exists(Folder)) return 0;
        var lastBoot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var oldestAlive = DateTime.UtcNow - notificationLifetime;
        int deleted = 0;
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                var written = files[i].LastWriteTimeUtc;
                if (written >= lastBoot && written >= oldestAlive && i < MaxFiles) continue;
                Delete(files[i].FullName);
                deleted++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (deleted > 0) _log.Info("images", $"cleaned up {deleted} old notification image(s)");
        return deleted;
    }

    /// <summary>Removes the whole cache folder (uninstall / --unregister).</summary>
    public static void DeleteAll()
    {
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
