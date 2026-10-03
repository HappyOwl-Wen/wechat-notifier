using System.Diagnostics;
using WeChatNotifier.Listener.Interop;
using WeChatNotifier.Listener.Profile;

namespace WeChatNotifier.Listener.Discovery;

public sealed record WeChatProcessInfo(int Pid, string ProcessName, string ImagePath, string? Version, long CreationTime);

public static class WeChatProcessLocator
{
    /// <summary>
    /// Confirms that <paramref name="pid"/> runs a WeChat executable able to host the chat UI: the version
    /// resource says Tencent + a WeChat product name, and it is not a known helper. Names are never trusted alone.
    /// </summary>
    public static WeChatProcessInfo? Identify(int pid)
    {
        var (path, created) = Native.GetProcessIdentity(pid);
        if (path is null) return null;

        var name = Path.GetFileNameWithoutExtension(path);
        if (WeChatUiProfile.HelperProcessNames.Any(h => h.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;

        FileVersionInfo vi;
        try { vi = FileVersionInfo.GetVersionInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }

        bool tencent = vi.CompanyName?.Contains(WeChatUiProfile.CompanyName, StringComparison.OrdinalIgnoreCase) == true;
        bool product = vi.ProductName is { } pn && WeChatUiProfile.ProductNames.Any(p => p.Equals(pn.Trim(), StringComparison.OrdinalIgnoreCase));
        return tencent && product ? new WeChatProcessInfo(pid, name, path, vi.FileVersion, created) : null;
    }

    /// <summary>One-time scan at start-up: processes whose name hints at WeChat, confirmed via <see cref="Identify"/>.</summary>
    public static List<WeChatProcessInfo> FindCandidates()
    {
        var list = new List<WeChatProcessInfo>();
        foreach (var hint in WeChatUiProfile.ProcessNameHints)
        {
            foreach (var p in Process.GetProcessesByName(hint))
            {
                using (p)
                {
                    if (Identify(p.Id) is { } info) list.Add(info);
                }
            }
        }
        return list;
    }
}
