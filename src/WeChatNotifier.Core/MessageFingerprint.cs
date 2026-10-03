using System.Security.Cryptography;
using System.Text;

namespace WeChatNotifier.Core;

public static class MessageFingerprint
{
    /// <summary>
    /// Hashes the identifying parts of an observation (conversation, unread count, preview, time, ...).
    /// Only the hash is kept in the de-duplication cache, never the text itself.
    /// </summary>
    public static string Create(params string?[] parts)
    {
        var joined = string.Join('\u001f', parts.Select(p => p ?? "\u0000"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)), 0, 12);
    }
}
