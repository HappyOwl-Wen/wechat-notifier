using System.Security.Cryptography;
using System.Text;

namespace WeChatNotifier.Core;

/// <summary>
/// Name-free key of a conversation: "c" + the first 12 bytes of SHA-256(display name), hex. Used as the
/// notification tag and as the click argument, so neither carries the name; the listener finds the conversation
/// again by hashing the names it can see, which also works after a restart.
/// </summary>
public static class ConversationKey
{
    public static string Of(string conversation) =>
        "c" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(conversation)), 0, 12);
}
