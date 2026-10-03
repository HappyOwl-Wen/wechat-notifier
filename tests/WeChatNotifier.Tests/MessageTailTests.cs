using WeChatNotifier.Core;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Listener.Reading;

namespace WeChatNotifier.Tests;

public class MessageTailTests
{
    // The message list as measured in WeChat 4.1.13 (screen px): top 451, bottom 1002.
    private static readonly PixelRect List = new(1065, 451, 933, 551);

    private const string Text = "mmui::ChatTextItemView";
    private const string Media = "mmui::ChatBubbleReferItemView";
    private const string Separator = "mmui::ChatItemView";

    private static TailCell Cell(string? cls, string? name, int top, int bottom) => new(cls, name, new PixelRect(1065, top, 933, bottom - top));

    [Fact]
    public void The_cell_at_the_bottom_of_the_list_is_the_newest_message()
    {
        // As read live: an older picture cut off at the top, the newest picture ending exactly at the list's bottom.
        var tail = MessageTail.Of([Cell(Media, "图片", 380, 711), Cell(Media, "图片", 711, 1002)], List);
        Assert.Equal(TailState.Ready, tail.State);
        Assert.Equal(MessageKind.Image, tail.Newest!.Kind);
        Assert.Equal(711, tail.Newest.Rect!.Value.Y);
        Assert.Equal(List, tail.Newest.ListRect);
        Assert.Equal(2, tail.Visible);
    }

    [Fact]
    public void A_list_scrolled_up_never_yields_an_older_message()
    {
        // The lowest visible cell continues below the list: the fully visible picture above it is an older one.
        var tail = MessageTail.Of([Cell(Media, "图片", 451, 800), Cell(Text, "older text", 800, 1100)], List);
        Assert.Equal(TailState.NotAtEnd, tail.State);
        Assert.Null(tail.Newest);
    }

    [Fact]
    public void Recycled_cells_outside_the_list_are_ignored()
    {
        var tail = MessageTail.Of(
        [
            Cell(Media, "图片", -400, -100),     // parked above
            Cell(Text, "new", 760, 1002),
            Cell(Media, "图片", 1200, 1500),     // parked below
            Cell(Text, "stale", 1000, 1200),     // touches the list's bottom edge by 2 px only
        ], List);
        Assert.Equal(TailState.Ready, tail.State);
        Assert.Equal(MessageKind.Text, tail.Newest!.Kind);
        Assert.Equal("new", tail.Newest.Text);
        Assert.Equal(1, tail.Visible);
    }

    [Fact]
    public void A_few_pixels_of_rounding_still_count_as_the_end()
    {
        var tail = MessageTail.Of([Cell(Media, "图片", 700, 1002 + MessageTail.EdgeTolerance)], List);
        Assert.Equal(TailState.Ready, tail.State);
        Assert.Equal(TailState.NotAtEnd, MessageTail.Of([Cell(Media, "图片", 700, 1002 + MessageTail.EdgeTolerance + 1)], List).State);
    }

    [Fact]
    public void A_message_still_being_laid_out_is_not_ready()
    {
        Assert.Equal(TailState.NotSettled, MessageTail.Of([Cell(Text, "a", 600, 900), Cell(Separator, "22:04", 900, 1002)], List).State);
        Assert.Equal(TailState.NotSettled, MessageTail.Of([Cell(Text, "a", 600, 900), Cell(Media, "", 900, 1002)], List).State);
        Assert.Equal(TailState.NotSettled, MessageTail.Of([Cell(Text, "a", 600, 900), Cell(Media, null, 900, 1002)], List).State);
    }

    [Fact]
    public void An_unknown_cell_class_is_reported_for_the_log()
    {
        var tail = MessageTail.Of([Cell("mmui::ChatSomethingNewItemView", "x", 700, 1002)], List);
        Assert.Equal(TailState.Ready, tail.State);
        Assert.Equal(MessageKind.Unknown, tail.Newest!.Kind);
        Assert.Contains("mmui::ChatSomethingNewItemView", tail.Describe());
    }

    [Fact]
    public void Empty_or_missing_lists()
    {
        Assert.Equal(TailState.NoCells, MessageTail.Of([], List).State);
        Assert.Equal(TailState.NoCells, MessageTail.Of([Cell(Text, "x", 100, 300)], List).State);
        Assert.Equal(TailState.NoList, MessageTail.Of([Cell(Text, "x", 700, 1002)], null).State);
        Assert.Equal(TailState.NoList, MessageTail.NoList.State);
    }

    [Fact]
    public void Describe_carries_no_message_content()
    {
        const string secret = "my bank PIN is 1234";
        foreach (var tail in new[]
        {
            MessageTail.Of([Cell(Text, secret, 700, 1002)], List),
            MessageTail.Of([Cell(Text, secret, 700, 1100)], List),
            MessageTail.Of([Cell(Media, secret, 700, 1002)], List) with { Refreshed = true },
        })
        {
            Assert.DoesNotContain(secret, tail.Describe());
        }
        Assert.EndsWith(", read again", (MessageTail.NoList with { Refreshed = true }).Describe());
    }
}
