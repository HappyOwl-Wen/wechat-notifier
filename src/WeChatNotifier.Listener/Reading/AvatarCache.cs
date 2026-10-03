using WeChatNotifier.Core.Imaging;

namespace WeChatNotifier.Listener.Reading;

/// <summary>
/// Conversation avatars copied while WeChat's window was on screen, so that notifications can still show them while
/// WeChat is minimized or in the tray — also after the notifier restarted (update, reboot, "restart listener"): with a
/// folder, every copy is also kept there as a PNG named by the name-free <see cref="Core.ConversationKey"/>, and a
/// file not copied again within <see cref="KeepFor"/> is deleted (spec §14: temporary images in LocalAppData, cleaned
/// up automatically). Bounded, keyed by the conversation key. A copy taken while WeChat's unread badge covered a
/// corner of the avatar is kept only until a clean one can be taken. Owned by the listener thread.
/// </summary>
internal sealed class AvatarCache
{
    internal const int Capacity = 128;

    /// <summary>Avatars rarely change; one capture per conversation per this period is enough.</summary>
    internal static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);

    /// <summary>A kept file is deleted when its conversation was not seen (copied) again for this long.</summary>
    internal static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private const string BadgeSuffix = ".badge";

    private readonly string? _folder;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, Entry> _items = new(StringComparer.Ordinal);
    private bool _folderRead;

    /// <param name="Avatar">Null while the copy is only on disk (read on first use).</param>
    private readonly record struct Entry(Bitmap32? Avatar, DateTime AtUtc, bool WithBadge);

    /// <param name="folder">Where copies outlive the process; null keeps them in RAM only.</param>
    public AvatarCache(string? folder = null, Func<DateTime>? utcNow = null)
    {
        _folder = folder;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public int Count
    {
        get
        {
            ReadFolder();
            return _items.Count;
        }
    }

    public Bitmap32? Get(string key)
    {
        ReadFolder();
        if (!_items.TryGetValue(key, out var entry)) return null;
        if (entry.Avatar is not null) return entry.Avatar;
        if (Load(key, entry.WithBadge) is not { } avatar)
        {
            Remove(key); // damaged or deleted meanwhile
            return null;
        }
        _items[key] = entry with { Avatar = avatar };
        return avatar;
    }

    /// <summary>
    /// Whether to copy this conversation's avatar now: there is none yet, the kept one has the badge on it and the
    /// badge is gone now, or the kept one is old.
    /// </summary>
    public bool NeedsCopy(string key, bool badgeNow)
    {
        ReadFolder();
        if (!_items.TryGetValue(key, out var entry)) return true;
        if (entry.WithBadge && !badgeNow) return true;
        return _utcNow() - entry.AtUtc >= RefreshAfter;
    }

    public void Set(string key, Bitmap32 avatar, bool withBadge)
    {
        ReadFolder();
        var now = _utcNow();
        if (withBadge && _items.TryGetValue(key, out var kept) && !kept.WithBadge)
        {
            // Never replace a clean copy with a covered one; look again after the next period.
            _items[key] = kept with { AtUtc = now };
            Touch(key, now);
            return;
        }
        _items[key] = new Entry(avatar, now, withBadge);
        Save(key, avatar, withBadge);
        while (_items.Count > Capacity) Remove(_items.MinBy(kv => kv.Value.AtUtc).Key);
    }

    /// <summary>Forgets the copies in RAM (WeChat exited); kept files are read again on the next use.</summary>
    public void Clear()
    {
        _items.Clear();
        _folderRead = false;
    }

    /// <summary>Deletes every copy, in RAM and on disk (avatars or the image cache switched off, cache cleared).</summary>
    public void DeleteAll()
    {
        _items.Clear();
        _folderRead = true;
        DeleteFolder(_folder);
    }

    public static bool DeleteFolder(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Indexes the kept files once (file names and dates only); deletes expired and unknown ones.</summary>
    private void ReadFolder()
    {
        if (_folderRead || _folder is null) return;
        _folderRead = true;
        try
        {
            if (!Directory.Exists(_folder)) return;
            var now = _utcNow();
            foreach (var file in new DirectoryInfo(_folder).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                var name = Path.GetFileNameWithoutExtension(file.Name);
                bool badge = name.EndsWith(BadgeSuffix, StringComparison.Ordinal);
                var key = badge ? name[..^BadgeSuffix.Length] : name;
                if (now - file.LastWriteTimeUtc > KeepFor || !IsKey(key) || _items.ContainsKey(key) || _items.Count >= Capacity)
                {
                    TryDelete(file.FullName);
                    continue;
                }
                _items[key] = new Entry(null, file.LastWriteTimeUtc, badge);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary><see cref="Core.ConversationKey"/>: "c" and 24 lowercase hex digits.</summary>
    private static bool IsKey(string name) =>
        name.Length == 25 && name[0] == 'c' && name.AsSpan(1).IndexOfAnyExcept("0123456789abcdef") < 0;

    private string PathOf(string key, bool withBadge) => Path.Combine(_folder!, key + (withBadge ? BadgeSuffix : "") + ".png");

    private void Save(string key, Bitmap32 avatar, bool withBadge)
    {
        if (_folder is null) return;
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(PathOf(key, withBadge), PngEncoder.Encode(avatar));
            TryDelete(PathOf(key, !withBadge));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A kept copy, or null when it is damaged or no avatar (kept by a version without that check).</summary>
    private Bitmap32? Load(string key, bool withBadge)
    {
        if (_folder is null) return null;
        try
        {
            return PngDecoder.Decode(File.ReadAllBytes(PathOf(key, withBadge))) is { } avatar && ImageAnalysis.LooksLikeAvatar(avatar) ? avatar : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Touch(string key, DateTime utc)
    {
        if (_folder is null) return;
        try { File.SetLastWriteTimeUtc(PathOf(key, withBadge: false), utc); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Remove(string key)
    {
        _items.Remove(key);
        if (_folder is null) return;
        TryDelete(PathOf(key, withBadge: false));
        TryDelete(PathOf(key, withBadge: true));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
