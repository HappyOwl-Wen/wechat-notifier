using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Listener.Profile;
using WeChatNotifier.Listener.Reading;

namespace WeChatNotifier.Tests;

public class Phase3ListenerTests
{
    [Fact]
    public void Session_avatar_estimate_is_a_centred_square()
    {
        // Phase 0 dump at 125 %: cells are 263 x 81 px -> 45 px avatar, same margin left and top.
        var avatar = WeChatUiProfile.SessionAvatar(new PixelRect(1356, 181, 263, 81));
        Assert.Equal(new PixelRect(1356 + 18, 181 + 18, 45, 45), avatar);

        // 100 %: 64 px cell -> 36 px avatar, 14 px margin.
        Assert.Equal(new PixelRect(14, 14, 36, 36), WeChatUiProfile.SessionAvatar(new PixelRect(0, 0, 250, 64)));
    }

    [Fact]
    public void Picture_area_skips_the_avatar_column_and_stays_inside_the_list()
    {
        var list = new PixelRect(1620, 100, 933, 268);
        var cell = new PixelRect(1620, 50, 933, 322);   // partly scrolled above the list
        var area = WeChatUiProfile.MessagePictureArea(cell, list, dpiScale: 1.25);
        Assert.Equal(new PixelRect(1620 + 90, 100, 933 - 90, 268), area);
        Assert.True(WeChatUiProfile.MessagePictureArea(new PixelRect(0, 500, 100, 50), list, 1.0).IsEmpty);
    }

    [Theory]
    [InlineData("Tom: Are we still meeting at 3?", "Tom", "Are we still meeting at 3?")]
    [InlineData("张三: [图片]", "张三", "[图片]")]
    [InlineData("hello world", null, "hello world")]
    [InlineData("see http://example.com", null, "see http://example.com")]
    [InlineData("[链接] 标题: 副标题", null, "[链接] 标题: 副标题")]
    public void Group_previews_split_into_sender_and_text(string preview, string? sender, string text)
    {
        Assert.Equal((sender, text), PreviewText.SplitSender(preview));
    }

    [Fact]
    public void Avatar_cache_is_bounded_and_ages()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new AvatarCache(utcNow: () => now);
        for (int i = 0; i < AvatarCache.Capacity + 10; i++)
        {
            cache.Set($"k{i}", new Bitmap32(1, 1), withBadge: false);
            now = now.AddSeconds(1);
        }
        Assert.Equal(AvatarCache.Capacity, cache.Count);
        Assert.Null(cache.Get("k0"));                       // oldest evicted
        Assert.NotNull(cache.Get($"k{AvatarCache.Capacity + 9}"));

        var copied = now.AddSeconds(-1);                    // the last one
        now = copied + AvatarCache.RefreshAfter - TimeSpan.FromSeconds(1);
        Assert.False(cache.NeedsCopy($"k{AvatarCache.Capacity + 9}", badgeNow: false));
        now = copied + AvatarCache.RefreshAfter;
        Assert.True(cache.NeedsCopy($"k{AvatarCache.Capacity + 9}", badgeNow: false));
        Assert.True(cache.NeedsCopy("missing", badgeNow: true));
    }

    [Fact]
    public void Avatar_copied_under_the_unread_badge_is_replaced_by_a_clean_one_but_never_the_other_way()
    {
        var cache = new AvatarCache();
        var covered = new Bitmap32(1, 1);
        var clean = new Bitmap32(1, 1);

        cache.Set("k", covered, withBadge: true);
        Assert.False(cache.NeedsCopy("k", badgeNow: true));  // still covered: no point copying again
        Assert.True(cache.NeedsCopy("k", badgeNow: false));  // badge gone: take a clean copy

        cache.Set("k", clean, withBadge: false);
        Assert.Same(clean, cache.Get("k"));

        cache.Set("k", new Bitmap32(1, 1), withBadge: true);
        Assert.Same(clean, cache.Get("k"));                   // a covered copy never replaces a clean one
        Assert.False(cache.NeedsCopy("k", badgeNow: true));
    }

    [Fact]
    public void Avatar_area_is_the_left_square_of_the_cell()
    {
        Assert.Equal(new PixelRect(1356, 181, 81, 81), WeChatUiProfile.SessionAvatarArea(new PixelRect(1356, 181, 263, 81)));
        Assert.Equal(new PixelRect(0, 0, 50, 64), WeChatUiProfile.SessionAvatarArea(new PixelRect(0, 0, 50, 64)));
    }
}
