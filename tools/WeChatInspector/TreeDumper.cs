using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;

namespace WeChatInspector;

/// <summary>
/// Dumps a UIA subtree. Two strategies, both timed so their cost can be compared:
///  - walk: TreeWalker + BuildCache, one cross-process call per node, honours --depth/--max.
///  - bulk: a single BuildUpdatedCache(TreeScope_Subtree) call, i.e. what a listener would use to
///          read one changed subtree in one round trip.
/// </summary>
internal sealed class TreeDumper(Uia uia, Redactor redactor, Sink sink, int maxDepth, int maxNodes, bool controlView)
{
    public int Count { get; private set; }

    public void DumpWalk(IUIAutomationElement root)
    {
        var walker = controlView ? uia.Automation.ControlViewWalker : uia.Automation.RawViewWalker;
        var sw = Stopwatch.StartNew();
        Walk(walker, root, 0, "0");
        sink.Always($"# walk: {Count} nodes in {sw.ElapsedMilliseconds} ms{(Count >= maxNodes ? " (truncated at --max)" : "")}");
    }

    private void Walk(IUIAutomationTreeWalker walker, IUIAutomationElement el, int depth, string path)
    {
        if (Count >= maxNodes) return;
        Emit(el, depth, path, childCount: null);
        if (depth >= maxDepth) return;

        IUIAutomationElement? child;
        try { child = walker.GetFirstChildElementBuildCache(el, uia.FullCache); }
        catch (COMException ex) { sink.Line($"{Indent(depth + 1)}! GetFirstChild failed 0x{ex.HResult:X8}"); return; }

        int index = 0;
        while (child is not null && Count < maxNodes)
        {
            Walk(walker, child, depth + 1, path + "." + index);
            try { child = walker.GetNextSiblingElementBuildCache(child, uia.FullCache); }
            catch (COMException ex) { sink.Line($"{Indent(depth + 1)}! GetNextSibling failed 0x{ex.HResult:X8}"); break; }
            index++;
        }
    }

    public void DumpBulk(IUIAutomationElement root)
    {
        var cr = uia.FullCache.Clone();
        cr.TreeScope = TreeScope.TreeScope_Subtree;
        if (controlView) cr.TreeFilter = uia.Automation.ControlViewCondition;
        var sw = Stopwatch.StartNew();
        IUIAutomationElement cached;
        try { cached = root.BuildUpdatedCache(cr); }
        catch (COMException ex) { sink.Always($"# bulk: BuildUpdatedCache failed 0x{ex.HResult:X8}"); return; }
        long fetchMs = sw.ElapsedMilliseconds;
        Visit(cached, 0, "0");
        sink.Always($"# bulk: {Count} nodes, single BuildUpdatedCache(Subtree) call took {fetchMs} ms");
    }

    private void Visit(IUIAutomationElement el, int depth, string path)
    {
        if (Count >= maxNodes) return;
        IUIAutomationElementArray? children = null;
        try { children = el.GetCachedChildren(); } catch (COMException) { }
        int n = children?.Length ?? 0;
        Emit(el, depth, path, n);
        if (depth >= maxDepth) return;
        for (int i = 0; i < n && Count < maxNodes; i++)
            Visit(children!.GetElement(i), depth + 1, path + "." + i);
    }

    private void Emit(IUIAutomationElement el, int depth, string path, int? childCount)
    {
        Count++;
        var info = uia.Snapshot(el, full: true);
        info.ChildCount = childCount;
        sink.Line(Indent(depth) + Describe(info, redactor));
        sink.Record(new
        {
            depth,
            path,
            info.ControlType,
            name = redactor.Apply(info.Name),
            automationId = redactor.Ids(info.AutomationId),
            info.ClassName,
            info.FrameworkId,
            rect = info.Rect,
            runtimeId = info.RuntimeIdText,
            info.Patterns,
            info.IsOffscreen,
            info.IsEnabled,
            info.IsKeyboardFocusable,
            info.HasKeyboardFocus,
            info.IsControlElement,
            info.IsContentElement,
            hwnd = info.NativeWindowHandle == 0 ? null : $"0x{info.NativeWindowHandle:X}",
            localizedControlType = info.LocalizedControlType,
            helpText = redactor.Apply(info.HelpText),
            itemStatus = redactor.Apply(info.ItemStatus),
            itemType = info.ItemType,
            fullDescription = redactor.Apply(info.FullDescription),
            value = redactor.Apply(info.Value),
            info.IsSelected,
            info.PositionInSet,
            info.SizeOfSet,
            info.Level,
            legacyName = redactor.Apply(info.LegacyName),
            legacyValue = redactor.Apply(info.LegacyValue),
            legacyDescription = redactor.Apply(info.LegacyDescription),
            info.LegacyDefaultAction,
            info.LegacyRole,
            legacyState = info.LegacyState is { } st ? $"0x{st:X}" : null,
            info.ChildCount,
        });
    }

    private static string Indent(int depth) => new(' ', depth * 2);

    public static string Describe(ElementInfo i, Redactor r)
    {
        var sb = new StringBuilder();
        sb.Append(i.ControlType);
        if (i.Name is not null) sb.Append(" \"").Append(r.Apply(i.Name)).Append('"');
        if (i.AutomationId is not null) sb.Append(" aid='").Append(r.Ids(i.AutomationId)).Append('\'');
        if (i.ClassName is not null) sb.Append(" cls='").Append(i.ClassName).Append('\'');
        if (i.FrameworkId is not null && i.FrameworkId != "Qt") sb.Append(" fw=").Append(i.FrameworkId);
        sb.Append(' ').Append(i.RectText);
        sb.Append(" rid=").Append(i.RuntimeIdText);
        if (i.NativeWindowHandle != 0) sb.Append($" hwnd=0x{i.NativeWindowHandle:X}");
        if (i.ChildCount is { } cc) sb.Append(" children=").Append(cc);
        if (i.IsOffscreen == true) sb.Append(" offscreen");
        if (i.IsEnabled == false) sb.Append(" disabled");
        if (i.HasKeyboardFocus == true) sb.Append(" FOCUSED");
        if (i.IsSelected == true) sb.Append(" SELECTED");
        if (i.Value is not null) sb.Append(" value=\"").Append(r.Apply(i.Value)).Append('"');
        if (i.HelpText is not null) sb.Append(" help=\"").Append(r.Apply(i.HelpText)).Append('"');
        if (i.ItemStatus is not null) sb.Append(" status=\"").Append(r.Apply(i.ItemStatus)).Append('"');
        if (i.ItemType is not null) sb.Append(" itemType=\"").Append(i.ItemType).Append('"');
        if (i.FullDescription is not null) sb.Append(" desc=\"").Append(r.Apply(i.FullDescription)).Append('"');
        if (i.PositionInSet is { } pos && pos > 0) sb.Append($" pos={pos}/{i.SizeOfSet}");
        if (i.LegacyRole is { } role) sb.Append($" msaaRole={role} msaaState=0x{i.LegacyState ?? 0:X}");
        if (i.LegacyName is not null && i.LegacyName != i.Name) sb.Append(" msaaName=\"").Append(r.Apply(i.LegacyName)).Append('"');
        if (i.LegacyValue is not null && i.LegacyValue != i.Value) sb.Append(" msaaValue=\"").Append(r.Apply(i.LegacyValue)).Append('"');
        if (i.Patterns is { Count: > 0 } p) sb.Append(" pat=[").Append(string.Join(",", p.Where(x => x != "LegacyIAccessible"))).Append(']');
        return sb.ToString();
    }
}
