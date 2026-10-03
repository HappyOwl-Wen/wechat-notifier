using System.Diagnostics;

namespace WeChatInspector;

internal sealed record WeChatProcessInfo(
    int Pid,
    string ProcessName,
    string? ImagePath,
    string? FileVersion,
    string? ProductName,
    string? CompanyName,
    bool IsHelper,
    string MatchReason);

internal sealed record TopWindow(
    IntPtr Hwnd,
    int Pid,
    string ClassName,
    string Title,
    bool Visible,
    bool Iconic,
    Native.RECT Rect,
    long Style,
    long ExStyle,
    IntPtr Owner);

internal sealed record MainWindowMatch(TopWindow Window, string UiaClassName, string Reason);

internal static class WeChatLocator
{
    /// <summary>
    /// Finds WeChat processes. Names are only hints: a process qualifies when its version resource says
    /// Tencent + a WeChat product name. <paramref name="deep"/> also checks every process's version
    /// resource, which catches renamed executables.
    /// </summary>
    public static List<WeChatProcessInfo> FindProcesses(bool deep)
    {
        var result = new List<WeChatProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name = p.ProcessName;
                bool hinted = WeChatUiProfile.ProcessNameHints.Any(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
                bool helper = WeChatUiProfile.HelperProcessNames.Any(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
                bool looksRelated = name.Contains("wechat", StringComparison.OrdinalIgnoreCase) || name.Contains("weixin", StringComparison.OrdinalIgnoreCase);
                if (!hinted && !looksRelated && !deep) continue;

                string? path = Native.GetProcessImagePath(p.Id);
                if (path is null) continue;

                FileVersionInfo? vi = null;
                try { vi = FileVersionInfo.GetVersionInfo(path); } catch { /* access denied / no resource */ }

                bool tencent = vi?.CompanyName?.Contains(WeChatUiProfile.CompanyName, StringComparison.OrdinalIgnoreCase) == true;
                bool product = vi?.ProductName is { } pn && WeChatUiProfile.ProductNames.Any(x => x.Equals(pn.Trim(), StringComparison.OrdinalIgnoreCase));

                string? reason = null;
                if (tencent && product) reason = hinted ? "name-hint+version-resource" : "version-resource";
                else if (tencent && (hinted || looksRelated || helper)) reason = "tencent-binary";
                else if (hinted) reason = "name-hint-only(unverified)";
                if (reason is null) continue;

                result.Add(new WeChatProcessInfo(p.Id, name, path, vi?.FileVersion, vi?.ProductName, vi?.CompanyName, helper, reason));
            }
        }
        return result.OrderBy(r => r.IsHelper).ThenBy(r => r.ProcessName).ThenBy(r => r.Pid).ToList();
    }

    /// <summary>Processes that could host the chat UI (verified product, not a known helper).</summary>
    public static List<WeChatProcessInfo> UiCandidates(IEnumerable<WeChatProcessInfo> all) =>
        all.Where(p => !p.IsHelper && p.MatchReason.Contains("version-resource", StringComparison.Ordinal)).ToList();

    public static List<TopWindow> EnumTopWindows(ICollection<int> pids)
    {
        var list = new List<TopWindow>();
        Native.EnumWindows((h, _) =>
        {
            int pid = Native.GetPid(h);
            if (!pids.Contains(pid)) return true;
            Native.GetWindowRect(h, out var r);
            list.Add(new TopWindow(
                h, pid,
                Native.GetClassName(h),
                Native.GetWindowText(h),
                Native.IsWindowVisible(h),
                Native.IsIconic(h),
                r,
                Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64(),
                Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64(),
                Native.GetWindow(h, Native.GW_OWNER)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Main window detection, strongest rule first:
    ///  1. UIA ClassName is a known main-window widget class (independent of Qt version / language).
    ///  2. Qt top-level window whose caption is a known main-window title.
    /// Only Qt top-level windows (and the legacy 3.x class) are asked for their UIA class, so this stays cheap.
    /// </summary>
    public static MainWindowMatch? FindMainWindow(Uia uia, ICollection<int> pids)
    {
        var windows = EnumTopWindows(pids);
        MainWindowMatch? byTitle = null;
        foreach (var w in windows)
        {
            bool qt = WeChatUiProfile.IsQtTopLevelClass(w.ClassName);
            bool legacy = WeChatUiProfile.MainWindowUiaClassNames.Contains(w.ClassName);
            if (!qt && !legacy) continue;

            string uiaClass = uia.TryGetRootClassName(w.Hwnd) ?? "";
            if (WeChatUiProfile.MainWindowUiaClassNames.Contains(uiaClass) || legacy)
                return new MainWindowMatch(w, uiaClass, $"uia-class '{uiaClass}'");

            if (byTitle is null && WeChatUiProfile.MainWindowTitles.Contains(w.Title))
                byTitle = new MainWindowMatch(w, uiaClass, $"qt-window + title '{w.Title}'");
        }
        return byTitle;
    }
}
