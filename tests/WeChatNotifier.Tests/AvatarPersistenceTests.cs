using WeChatNotifier.Core;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Listener.Reading;

namespace WeChatNotifier.Tests;

public sealed class AvatarPersistenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WeChatNotifier.Tests", Guid.NewGuid().ToString("N"), "avatars");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static Bitmap32 Sample(int w = 45, int h = 45, int seed = 1)
    {
        var bmp = new Bitmap32(w, h);
        for (int i = 0; i < bmp.Pixels.Length; i++) bmp.Pixels[i] = (byte)((i * 31 + seed * 17) % 251);
        return bmp;
    }

    private string[] Files() => Directory.Exists(_folder) ? Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).Order().ToArray() : [];

    [Fact]
    public void Png_round_trip_is_lossless_including_alpha()
    {
        var original = Sample(37, 23);
        var decoded = PngDecoder.Decode(PngEncoder.Encode(original));
        Assert.NotNull(decoded);
        Assert.Equal((37, 23), (decoded.Width, decoded.Height));
        Assert.Equal(original.Pixels, decoded.Pixels);
    }

    [Fact]
    public void Damaged_or_foreign_files_are_not_decoded()
    {
        var png = PngEncoder.Encode(Sample());
        var damaged = (byte[])png.Clone();
        damaged[png.Length / 2] ^= 0x55;
        Assert.Null(PngDecoder.Decode(damaged));                 // CRC mismatch
        Assert.Null(PngDecoder.Decode(png.AsSpan(0, png.Length / 2)));
        Assert.Null(PngDecoder.Decode("not a png at all"u8));
        Assert.Null(PngDecoder.Decode([]));
    }

    [Fact]
    public void A_kept_copy_that_is_no_avatar_is_dropped_and_copied_again()
    {
        // Kept by 0.7.0 and earlier: the list's grey with the lower edge of an avatar across the top.
        var key = ConversationKey.Of("Bob");
        var missed = new Bitmap32(43, 43);
        for (int i = 0; i < missed.Pixels.Length; i += 4)
        {
            bool edge = i / 4 / 43 < 5;
            missed.Pixels[i] = edge ? (byte)200 : (byte)238;
            missed.Pixels[i + 1] = edge ? (byte)120 : (byte)238;
            missed.Pixels[i + 2] = edge ? (byte)230 : (byte)238;
            missed.Pixels[i + 3] = 255;
        }
        new AvatarCache(_folder).Set(key, missed, withBadge: false);

        var restarted = new AvatarCache(_folder);
        Assert.Null(restarted.Get(key));
        Assert.Empty(Files());
        Assert.True(restarted.NeedsCopy(key, badgeNow: false));
    }

    [Fact]
    public void Avatars_survive_a_restart_and_are_named_by_the_conversation_key_only()
    {
        var key = ConversationKey.Of("Bob");
        var avatar = Sample();
        new AvatarCache(_folder).Set(key, avatar, withBadge: false);
        Assert.Equal([key + ".png"], Files());                    // no conversation name on disk

        var restarted = new AvatarCache(_folder);
        Assert.False(restarted.NeedsCopy(key, badgeNow: false));  // just copied: no new copy needed
        var loaded = restarted.Get(key);
        Assert.NotNull(loaded);
        Assert.Equal(avatar.Pixels, loaded.Pixels);
        Assert.Same(loaded, restarted.Get(key));                  // read once, then from RAM
    }

    [Fact]
    public void Kept_avatars_expire_after_30_days_without_a_new_copy()
    {
        var key = ConversationKey.Of("old friend");
        new AvatarCache(_folder).Set(key, Sample(), withBadge: false);

        var later = new AvatarCache(_folder, () => DateTime.UtcNow + AvatarCache.KeepFor + TimeSpan.FromHours(1));
        Assert.Null(later.Get(key));
        Assert.Empty(Files());                                    // the file is deleted, not just ignored
    }

    [Fact]
    public void A_clean_copy_replaces_the_one_under_the_badge_on_disk_too()
    {
        var key = ConversationKey.Of("group");
        var cache = new AvatarCache(_folder);
        cache.Set(key, Sample(seed: 1), withBadge: true);
        Assert.Equal([key + ".badge.png"], Files());
        Assert.True(new AvatarCache(_folder).NeedsCopy(key, badgeNow: false)); // a restart still knows it is covered

        var clean = Sample(seed: 2);
        cache.Set(key, clean, withBadge: false);
        Assert.Equal([key + ".png"], Files());
        Assert.Equal(clean.Pixels, new AvatarCache(_folder).Get(key)!.Pixels);
    }

    [Fact]
    public void Clear_keeps_the_files_and_DeleteAll_removes_them()
    {
        var key = ConversationKey.Of("a");
        var cache = new AvatarCache(_folder);
        cache.Set(key, Sample(), withBadge: false);

        cache.Clear();                                            // WeChat exited
        Assert.NotNull(cache.Get(key));                           // read again from disk

        cache.DeleteAll();                                        // switched off / cache cleared
        Assert.Null(cache.Get(key));
        Assert.False(Directory.Exists(_folder));
        Assert.Null(new AvatarCache(_folder).Get(key));
    }

    [Fact]
    public void Unknown_files_are_removed_and_the_folder_is_bounded()
    {
        Directory.CreateDirectory(_folder);
        var png = PngEncoder.Encode(Sample(4, 4));
        File.WriteAllBytes(Path.Combine(_folder, "Bob.png"), png);   // not a key: never written by us
        var start = DateTime.UtcNow.AddMinutes(-10);
        var keys = Enumerable.Range(0, AvatarCache.Capacity + 5).Select(i => ConversationKey.Of($"c{i}")).ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            var path = Path.Combine(_folder, keys[i] + ".png");
            File.WriteAllBytes(path, png);
            File.SetLastWriteTimeUtc(path, start.AddSeconds(i));          // keys[0] is the oldest
        }

        var cache = new AvatarCache(_folder);
        Assert.Equal(AvatarCache.Capacity, cache.Count);
        Assert.Null(cache.Get(keys[0]));                                   // the oldest went first
        Assert.NotNull(cache.Get(keys[^1]));
        Assert.Equal(AvatarCache.Capacity, Files().Length);
        Assert.DoesNotContain("Bob.png", Files());
    }

    [Fact]
    public void Without_a_folder_nothing_is_written()
    {
        var cache = new AvatarCache();
        cache.Set(ConversationKey.Of("x"), Sample(), withBadge: false);
        Assert.False(Directory.Exists(_folder));
        cache.DeleteAll();
    }
}
