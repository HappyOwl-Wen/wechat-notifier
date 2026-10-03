using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatNotifier.Core.Notifications;

namespace WeChatNotifier.Core.Configuration;

/// <summary>
/// User settings (spec §18), stored as JSON in %LOCALAPPDATA%\WeChatNotifier\settings.json. Contains no chat
/// data. Unknown or missing values fall back to the defaults below. Changed only through <c>with</c> copies; the
/// setters are not <c>init</c> because the source-generated serializer would then set every value missing from
/// the file (an older version's file) to false instead of its default.
/// </summary>
public sealed record AppSettings
{
    // ---- General ----

    /// <summary>Start the notifier when the user signs in (HKCU Run key).</summary>
    public bool StartWithWindows { get; set; } = true;

    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Spec §15, default off.</summary>
    public bool NotifyWhileWeChatFocused { get; set; }

    // ---- Notifications ----

    public bool MessagePreview { get; set; } = true;

    public bool ImagePreview { get; set; } = true;

    public bool Avatars { get; set; } = true;

    /// <summary>Notify for group chats (recognised by WeChat's "sender: text" previews; see MessageEvent.IsGroup).</summary>
    public bool GroupMessages { get; set; } = true;

    public bool Sound { get; set; } = true;

    /// <summary>Notification duration: banners stay ~25 s instead of ~5 s.</summary>
    public bool LongDuration { get; set; }

    public bool NotifyMutedConversations { get; set; }

    public bool OpenConversationOnClick { get; set; } = true;

    /// <summary>The notifications' header shows "微信" and WeChat's icon (NotificationOptions.AppearAsWeChat).</summary>
    public bool ShowAsWeChat { get; set; } = true;

    // ---- Privacy ----

    /// <summary>Allow copied avatars and pictures in the temporary image cache.</summary>
    public bool StoreImageCache { get; set; } = true;

    /// <summary>Write burst/read details to the log (still no chat text).</summary>
    public bool DebugLogging { get; set; }

    public NotificationOptions ToNotificationOptions() => new()
    {
        ShowMessagePreview = MessagePreview,
        PlaySound = Sound,
        LongDuration = LongDuration,
        ShowAvatars = Avatars,
        ShowImagePreview = ImagePreview,
        StoreImageCache = StoreImageCache,
        AppearAsWeChat = ShowAsWeChat,
    };
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;

/// <summary>Loads and saves <see cref="AppSettings"/>; never throws (a broken file means defaults).</summary>
public static class SettingsStore
{
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatNotifier");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Copied conversation avatars kept across restarts (the listener's AvatarCache; temporary, cleaned up).</summary>
    public static string AvatarFolder => Path.Combine(Folder, "avatars");

    public static AppSettings Load() => Load(FilePath);

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep the broken file for inspection and continue with defaults.
            try { File.Move(path, path + ".bad", overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new AppSettings();
        }
    }

    public static bool Save(AppSettings settings) => Save(settings, FilePath);

    /// <summary>Writes a temporary file and replaces the old one, so a crash never leaves half a file.</summary>
    public static bool Save(AppSettings settings, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, AppSettingsJson.Default.AppSettings));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
