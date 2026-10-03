using WeChatNotifier.Listener.Tracking;

namespace WeChatNotifier.Tests;

public class ReadSchedulerTests
{
    /// <summary>Runs the scheduler like the listener loop does and returns the times at which reads ran.</summary>
    private static (List<long> Reads, BurstEnd? End) Simulate(ReadScheduler s, long[] flashes, Func<long, bool> changedAt, long untilMs = 10_000)
    {
        var reads = new List<long>();
        BurstEnd? end = null;
        int f = 0;
        for (long now = 0; now <= untilMs; now++)
        {
            while (f < flashes.Length && flashes[f] == now) s.OnFlash(flashes[f++]);
            if (!s.TakeDue(now)) continue;
            reads.Add(now);
            end = s.OnReadCompleted(changedAt(now)) ?? end;
        }
        return (reads, end);
    }

    [Fact]
    public void Idle_scheduler_has_nothing_due()
    {
        var s = new ReadScheduler();
        Assert.Null(s.NextDueMs);
        Assert.False(s.TakeDue(1_000_000));
        Assert.False(s.BurstActive);
    }

    [Fact]
    public void One_flash_without_change_reads_on_the_initial_schedule_then_ends()
    {
        var (reads, end) = Simulate(new ReadScheduler(), [0], _ => false);
        Assert.Equal([60, 100, 150, 210, 300, 600, 1000, 1500], reads);
        Assert.NotNull(end);
        Assert.False(end!.ChangeSeen);
        Assert.Equal(1, end.Flashes);
    }

    [Fact]
    public void After_a_change_and_one_quiet_read_the_initial_reads_are_dropped()
    {
        var (reads, end) = Simulate(new ReadScheduler(), [0], t => t == 150);
        Assert.Equal([60, 100, 150, 210, 1500], reads);
        Assert.True(end!.ChangeSeen);
    }

    [Fact]
    public void Blinking_adds_reads_and_extends_the_burst()
    {
        // Four flashes ~310 ms apart (window shown), as recorded in Phase 0.
        var (reads, end) = Simulate(new ReadScheduler(), [0, 310, 620, 930], _ => false);
        // 310+250=560 is merged into the 600 read; 870 and 1180 are added; the final read moves to 930+1500.
        Assert.Equal([60, 100, 150, 210, 300, 600, 870, 1000, 1180, 2430], reads);
        Assert.Equal(4, end!.Flashes);
    }

    [Fact]
    public void A_flash_after_the_burst_starts_a_new_burst()
    {
        var s = new ReadScheduler();
        var (reads, _) = Simulate(s, [0, 5000], _ => false);
        Assert.Equal([60, 100, 150, 210, 300, 600, 1000, 1500, 5060, 5100, 5150, 5210, 5300, 5600, 6000, 6500], reads);
        Assert.False(s.BurstActive);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var s = new ReadScheduler();
        s.OnFlash(0);
        s.Reset();
        Assert.False(s.BurstActive);
        Assert.Null(s.NextDueMs);
    }
}
