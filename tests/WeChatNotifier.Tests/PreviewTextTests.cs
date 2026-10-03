using WeChatNotifier.Core;
using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Tests;

public class PreviewTextTests
{
    [Theory]
    [InlineData("[图片]", MessageKind.Image)]
    [InlineData("Bob: [图片]", MessageKind.Image)]
    [InlineData("[动画表情]", MessageKind.Sticker)]
    [InlineData("[视频通话]", MessageKind.Call)]
    [InlineData("[语音] 5\"", MessageKind.Voice)]
    [InlineData("[文件] report.pdf", MessageKind.File)]
    [InlineData("[链接] Some article", MessageKind.Link)]
    [InlineData("[Photo]", MessageKind.Image)]
    [InlineData("hello", MessageKind.Text)]
    [InlineData("[微笑] hi", MessageKind.Text)]      // WeChat emoji code, not a placeholder
    [InlineData("Bob: see you", MessageKind.Text)]
    public void Kind_from_placeholder(string preview, MessageKind kind) =>
        Assert.Equal(kind, PreviewText.Analyze(preview).Kind);

    [Fact]
    public void Null_preview_is_unknown()
    {
        var p = PreviewText.Analyze(null);
        Assert.Null(p.Text);
        Assert.Equal(MessageKind.Unknown, p.Kind);
    }

    [Fact]
    public void Embedded_count_is_stripped()
    {
        var p = PreviewText.Analyze("[5条] Daily News: headline");
        Assert.Equal(5, p.EmbeddedUnread);
        Assert.Equal("Daily News: headline", p.Text);
    }

    [Fact]
    public void Mention_marker_is_stripped()
    {
        var p = PreviewText.Analyze("[有人@我] Bob: are you there?");
        Assert.True(p.Mentioned);
        Assert.Equal("Bob: are you there?", p.Text);
    }

    [Fact]
    public void Draft_and_recall_notices_are_flagged()
    {
        Assert.True(PreviewText.Analyze("[草稿] still typing").IsDraft);
        Assert.True(PreviewText.Analyze("Bob撤回了一条消息").IsSystemNotice);
        Assert.False(PreviewText.Analyze("hello").IsDraft);
    }

    [Theory]
    [InlineData("hello wor…", "hello world, this is long", true)]
    [InlineData("hello", "hello", true)]
    [InlineData("a b…", "a\nb c", true)]
    [InlineData("hello", "goodbye", false)]
    [InlineData("…", "anything", false)]
    [InlineData(null, "x", false)]
    public void Elided_preview_matches_full_text(string? preview, string full, bool expected) =>
        Assert.Equal(expected, PreviewText.IsCompatible(preview, full));

    [Theory]
    [InlineData("图片", MessageKind.Image)]
    [InlineData("视频", MessageKind.Video)]
    [InlineData("something", MessageKind.Unknown)]
    public void Media_cell_labels(string label, MessageKind kind) =>
        Assert.Equal(kind, PreviewText.KindOfCellLabel(label));
}
