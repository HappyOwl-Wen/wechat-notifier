using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Tests;

/// <summary>Name formats observed on Weixin 4.1.13.65 (docs/wechat-ui-analysis.md §3.3), with fictional content.</summary>
public class SessionCellParserTests
{
    [Fact]
    public void Unread_preview_and_time()
    {
        var c = WeChatUiProfile.ParseSessionCell("Alice\n[4条] \nsee you at 3\n19:44\n");
        Assert.Equal(new SessionCell("Alice", Pinned: false, Unread: 4, Preview: "see you at 3", Time: "19:44", Muted: false), c);
        Assert.True(c.HasContent);
    }

    [Fact]
    public void Pinned_with_image_placeholder()
    {
        var c = WeChatUiProfile.ParseSessionCell("文件传输助手\n已置顶\n[图片] \n09/21\n");
        Assert.True(c.Pinned);
        Assert.Equal(0, c.Unread);
        Assert.Equal("[图片]", c.Preview);
        Assert.Equal("09/21", c.Time);
    }

    [Fact]
    public void Muted_group_keeps_sender_prefix_in_preview()
    {
        var c = WeChatUiProfile.ParseSessionCell("Study Group\nBob: see you\n13:50\n消息免打扰\n");
        Assert.True(c.Muted);
        Assert.Equal("Bob: see you", c.Preview);
        Assert.Equal("13:50", c.Time);
    }

    [Fact]
    public void Muted_conversation_has_badge_line_and_count_inside_preview()
    {
        var c = WeChatUiProfile.ParseSessionCell("Games\n[4条] \n[4条] Event starts today\n09:31\n消息免打扰\n");
        Assert.Equal(4, c.Unread);
        Assert.True(c.Muted);
        Assert.Equal("[4条] Event starts today", c.Preview);
    }

    [Fact]
    public void Folder_embeds_count_in_preview_only()
    {
        var c = WeChatUiProfile.ParseSessionCell("公众号\n[5条] Daily News: headline…\n19:18\n");
        Assert.Equal(0, c.Unread);
        Assert.Equal("[5条] Daily News: headline…", c.Preview);
    }

    [Theory]
    [InlineData("Alice\n[1条] \n", 1)]
    [InlineData("Alice\n", 0)]
    [InlineData("文件传输助手\n已置顶\n", 0)]
    public void Hidden_window_names_have_no_preview(string name, int unread)
    {
        var c = WeChatUiProfile.ParseSessionCell(name);
        Assert.Equal(unread, c.Unread);
        Assert.Null(c.Preview);
        Assert.Null(c.Time);
        Assert.False(c.HasContent);
    }

    [Theory]
    [InlineData("昨天 15:44")]
    [InlineData("星期一")]
    [InlineData("09/21")]
    [InlineData("2025/12/31")]
    [InlineData("7:05")]
    public void Time_formats(string time)
    {
        var c = WeChatUiProfile.ParseSessionCell($"Alice\nhello\n{time}\n");
        Assert.Equal(time, c.Time);
        Assert.Equal("hello", c.Preview);
    }

    [Fact]
    public void Preview_that_looks_like_a_time_is_kept_when_a_time_line_follows()
    {
        var c = WeChatUiProfile.ParseSessionCell("Alice\n12:30\n19:44\n");
        Assert.Equal("12:30", c.Preview);
        Assert.Equal("19:44", c.Time);
    }

    [Fact]
    public void Multi_line_preview_is_joined()
    {
        var c = WeChatUiProfile.ParseSessionCell("Alice\nline one\nline two\n19:00\n");
        Assert.Equal("line one\nline two", c.Preview);
    }

    [Fact]
    public void Empty_name()
    {
        var c = WeChatUiProfile.ParseSessionCell("");
        Assert.Equal("", c.Title);
        Assert.Null(c.Preview);
    }
}
