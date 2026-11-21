using System.Diagnostics;
using System.Text.Json;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Core.Notifications;
using WeChatNotifier.Listener.Profile;
using WeChatNotifier.Listener.Tracking;

// Synthetic inputs only; this tool never attaches to WeChat or shows notifications.
// Prebuild inputs so allocation measurements cover the operations, not fixture generation.
var names = Enumerable.Range(0, 12000).Select(i => "conversation-" + i).ToArray();
var churn = names.Select(n => new[] { new SessionCell(n, false, 0, "hello", "19:00", false) }).ToArray();
var steady = churn.Take(10).Select(c => c[0]).ToArray();
var bitmap = new Bitmap32(1024, 512);
new Random(42).NextBytes(bitmap.Pixels);
var png = PngEncoder.Encode(bitmap);

Measure("session-churn-10000", () =>
{
    var tracker = new SessionTracker();
    for (int i = 0; i < 10000; i++) tracker.Apply(churn[i], true, null, false);
});
Measure("session-steady-10000", () =>
{
    var tracker = new SessionTracker();
    for (int i = 0; i < 10000; i++) tracker.Apply(steady, true, null, false);
});
Measure("lines-churn-10000", () =>
{
    var lines = new ConversationLines();
    for (int i = 0; i < 10000; i++) lines.Add(names[i], "hello", 1, 0);
});
Measure("png-encode-20", () =>
{
    for (int i = 0; i < 20; i++) PngEncoder.Encode(bitmap);
});
Measure("png-decode-20", () =>
{
    for (int i = 0; i < 20; i++) PngDecoder.Decode(png);
});

// Model a burst arriving while the Windows notification thread is occupied. Every new payload already
// contains the conversation's latest history. Count work left for Windows rather than inventing Show timings.
var queue = new NotificationWorkQueue(256);
for (int i = 0; i < 100; i++)
{
    queue.TryAdd(new NotificationContent
    {
        Kind = NotificationKind.Message,
        Group = "chat",
        Tag = "synthetic-conversation",
        Title = "Synthetic conversation",
        Body = i.ToString(),
        Timestamp = DateTimeOffset.UnixEpoch,
        Expires = DateTimeOffset.UnixEpoch.AddDays(1),
    });
}
queue.CompleteAdding();
int calls = 0;
string? lastBody = null;
while (queue.TryTake(out var content, out _))
{
    calls++;
    lastBody = content!.Body;
}
if (calls != 1 || lastBody != "99") throw new InvalidOperationException("Burst lost the latest notification.");
Console.WriteLine(JsonSerializer.Serialize(new { Name = "notification-burst", Messages = 100, PendingShowCalls = calls, LatestBody = lastBody }));

static void Measure(string name, Action action)
{
    for (int i = 0; i < 3; i++) action();
    var samples = new List<(double Ms, long Bytes)>();
    for (int i = 0; i < 7; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        samples.Add((sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before));
    }
    samples.Sort((a, b) => a.Ms.CompareTo(b.Ms));
    var median = samples[3];
    Console.WriteLine(JsonSerializer.Serialize(new { Name = name, MedianMs = median.Ms, AllocatedBytes = median.Bytes }));
}
