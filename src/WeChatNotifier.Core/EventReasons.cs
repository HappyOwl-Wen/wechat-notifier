namespace WeChatNotifier.Core;

/// <summary><see cref="MessageEvent.Reason"/> values that other components act on.</summary>
public static class EventReasons
{
    /// <summary>Generic event: WeChat signalled a message, but no session list was ever readable (not shown since start).</summary>
    public const string NothingReadable = "nothing-readable";

    /// <summary>Generic event from a synthetic flash (diagnostics), not a real message.</summary>
    public const string SelfTest = "self-test";
}
