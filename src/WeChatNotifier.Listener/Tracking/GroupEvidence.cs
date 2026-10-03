namespace WeChatNotifier.Listener.Tracking;

/// <summary>
/// Which conversations are group chats, learned from WeChat's previews ("sender: text" for messages from others in
/// a group). One sender is weak evidence — a private message can itself start with "PS: " — so a conversation
/// counts as a group for good only after two different senders. RAM only, bounded, keyed by conversation key;
/// sender names are kept only as a hash until the second one decides. Owned by the listener thread.
/// </summary>
internal sealed class GroupEvidence
{
    internal const int Capacity = 500;

    private readonly Dictionary<string, int> _firstSender = new(StringComparer.Ordinal); // key -> hash of the sender seen
    private readonly HashSet<string> _groups = new(StringComparer.Ordinal);

    public int Count => _groups.Count;

    public void Observe(string key, string? sender)
    {
        if (sender is null || _groups.Contains(key)) return;
        int hash = StringComparer.Ordinal.GetHashCode(sender);
        if (_firstSender.TryGetValue(key, out var first))
        {
            if (first == hash) return;
            _firstSender.Remove(key);
            if (_groups.Count < Capacity) _groups.Add(key);
            return;
        }
        if (_firstSender.Count < Capacity) _firstSender[key] = hash;
    }

    /// <summary>A group: this message's preview names a sender, or the conversation is known to be one.</summary>
    public bool IsGroup(string key, string? senderNow) => senderNow is not null || _groups.Contains(key);

    public void Clear()
    {
        _firstSender.Clear();
        _groups.Clear();
    }
}
