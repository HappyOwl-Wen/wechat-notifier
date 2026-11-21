using System.Collections.Concurrent;

namespace WeChatNotifier.Core.Notifications;

/// <summary>
/// Bounded notification work, with at most one pending payload per conversation between control actions.
/// Replacing a pending payload releases its text and images without another Windows Show call. Control actions
/// (withdrawal, settings and cache changes) are barriers: a later message must stay after them. Thread-safe.
/// </summary>
internal sealed class NotificationWorkQueue
{
    private readonly object _gate = new();
    private readonly BlockingCollection<PendingWork> _work;
    private readonly Dictionary<(string Group, string Tag), PendingWork> _pending = [];

    public NotificationWorkQueue(int capacity)
    {
        _work = new BlockingCollection<PendingWork>(new ConcurrentQueue<PendingWork>(), capacity);
    }

    public int Count => _work.Count;

    public bool TryAdd(NotificationContent content)
    {
        lock (_gate)
        {
            if (_work.IsAddingCompleted) return false;
            var key = (content.Group, content.Tag);
            if (content.Kind == NotificationKind.Message && _pending.TryGetValue(key, out var pending))
            {
                pending.Content = content;
                return true;
            }

            var item = new PendingWork { Content = content };
            if (!_work.TryAdd(item)) return false;
            if (content.Kind == NotificationKind.Message) _pending.Add(key, item);
            else _pending.Clear();
            return true;
        }
    }

    public bool TryAdd(Action action)
    {
        lock (_gate)
        {
            if (_work.IsAddingCompleted || !_work.TryAdd(new PendingWork { Action = action })) return false;
            _pending.Clear();
            return true;
        }
    }

    /// <summary>Blocks while idle. Completion drains accepted work before returning false.</summary>
    public bool TryTake(out NotificationContent? content, out Action? action)
    {
        content = null;
        action = null;
        if (!_work.TryTake(out var item, Timeout.Infinite)) return false;
        lock (_gate)
        {
            content = item.Content;
            action = item.Action;
            if (content is { Kind: NotificationKind.Message })
            {
                var key = (content.Group, content.Tag);
                // A barrier may have queued another item with the same key after this one.
                if (_pending.TryGetValue(key, out var pending) && ReferenceEquals(pending, item)) _pending.Remove(key);
            }
            return true;
        }
    }

    public void CompleteAdding()
    {
        lock (_gate) _work.CompleteAdding();
    }

    private sealed class PendingWork
    {
        public NotificationContent? Content;
        public Action? Action;
    }
}
