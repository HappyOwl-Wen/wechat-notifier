using WeChatNotifier.Core.Notifications;

namespace WeChatNotifier.Tests;

public class NotificationWorkQueueTests
{
    private static NotificationContent Message(string tag = "a", string body = "first", string group = "chat") => new()
    {
        Kind = NotificationKind.Message,
        Tag = tag,
        Group = group,
        Title = "conversation",
        Body = body,
        Timestamp = DateTimeOffset.Now,
        Expires = DateTimeOffset.Now.AddHours(1),
    };

    private static NotificationContent Take(NotificationWorkQueue queue)
    {
        Assert.True(queue.TryTake(out var content, out var action));
        Assert.Null(action);
        return Assert.IsType<NotificationContent>(content);
    }

    [Fact]
    public void Burst_keeps_the_latest_complete_notification_in_one_slot()
    {
        var queue = new NotificationWorkQueue(1);
        var last = Message(body: "last") with { EarlierLines = ["first", "second"], Attribution = "3 条未读" };
        Assert.True(queue.TryAdd(Message()));
        Assert.True(queue.TryAdd(Message(body: "second")));
        Assert.True(queue.TryAdd(last));
        Assert.Equal(1, queue.Count);
        Assert.Same(last, Take(queue));
    }

    [Fact]
    public void Conversations_share_neither_payload_nor_queue_slot()
    {
        var queue = new NotificationWorkQueue(2);
        queue.TryAdd(Message("a"));
        queue.TryAdd(Message("b"));
        var latest = Message("a", "latest");
        queue.TryAdd(latest);
        Assert.Same(latest, Take(queue));
        Assert.Equal("b", Take(queue).Tag);
    }

    [Fact]
    public void Identical_tags_in_different_groups_are_independent()
    {
        var queue = new NotificationWorkQueue(2);
        queue.TryAdd(Message(group: "one"));
        queue.TryAdd(Message(group: "two"));
        Assert.Equal("one", Take(queue).Group);
        Assert.Equal("two", Take(queue).Group);
    }

    [Fact]
    public void Withdrawal_and_settings_actions_are_ordering_barriers()
    {
        var queue = new NotificationWorkQueue(4);
        var before = Message(body: "before read");
        var after = Message(body: "after read");
        bool withdrawn = false;
        queue.TryAdd(before);
        queue.TryAdd(() => withdrawn = true);
        queue.TryAdd(Message(body: "intermediate after read"));
        queue.TryAdd(after);
        Assert.Same(before, Take(queue));
        var latest = after with { Body = "latest after read" };
        Assert.True(queue.TryAdd(latest)); // taking the older item must not unindex this newer one
        Assert.True(queue.TryTake(out var content, out var action));
        Assert.Null(content);
        Assert.NotNull(action);
        action();
        Assert.True(withdrawn);
        Assert.Same(latest, Take(queue));
    }

    [Fact]
    public void Failed_barrier_does_not_break_coalescing_in_a_full_queue()
    {
        var queue = new NotificationWorkQueue(1);
        queue.TryAdd(Message());
        Assert.False(queue.TryAdd(() => { }));
        Assert.False(queue.TryAdd(Message("other")));
        var latest = Message(body: "latest");
        Assert.True(queue.TryAdd(latest));
        Assert.Same(latest, Take(queue));
    }

    [Fact]
    public void Test_notifications_keep_their_order_and_act_as_barriers()
    {
        var queue = new NotificationWorkQueue(4);
        queue.TryAdd(Message(body: "before"));
        queue.TryAdd(Message() with { Kind = NotificationKind.Test });
        queue.TryAdd(Message() with { Kind = NotificationKind.Test });
        queue.TryAdd(Message(body: "after"));
        Assert.Equal("before", Take(queue).Body);
        Assert.Equal(NotificationKind.Test, Take(queue).Kind);
        Assert.Equal(NotificationKind.Test, Take(queue).Kind);
        Assert.Equal("after", Take(queue).Body);
    }

    [Fact]
    public void A_payload_already_taken_is_not_mutated_by_new_messages()
    {
        var queue = new NotificationWorkQueue(1);
        var first = Message();
        queue.TryAdd(first);
        Assert.Same(first, Take(queue));
        var next = Message(body: "next");
        Assert.True(queue.TryAdd(next));
        Assert.Same(next, Take(queue));
        Assert.Equal("first", first.Body);
    }

    [Fact]
    public void Completion_rejects_new_payloads_and_drains_existing_work()
    {
        var queue = new NotificationWorkQueue(2);
        queue.TryAdd(Message());
        queue.TryAdd(() => { });
        queue.CompleteAdding();
        Assert.False(queue.TryAdd(Message()));
        Assert.False(queue.TryAdd(() => { }));
        Take(queue);
        Assert.True(queue.TryTake(out _, out _));
        Assert.False(queue.TryTake(out _, out _));
    }

    [Fact]
    public async Task Concurrent_production_and_consumption_preserve_the_latest_message()
    {
        var queue = new NotificationWorkQueue(1);
        var consumed = new List<int>();
        var consumer = Task.Run(() =>
        {
            while (queue.TryTake(out var content, out _)) consumed.Add(int.Parse(content!.Body));
        }, TestContext.Current.CancellationToken);
        try
        {
            for (int i = 0; i < 5000; i++) Assert.True(queue.TryAdd(Message(body: i.ToString())));
        }
        finally { queue.CompleteAdding(); }
        await consumer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotEmpty(consumed);
        Assert.Equal(4999, consumed[^1]);
        for (int i = 1; i < consumed.Count; i++) Assert.True(consumed[i] > consumed[i - 1]);
    }
}
