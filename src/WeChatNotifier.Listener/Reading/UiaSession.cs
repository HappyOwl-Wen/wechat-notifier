using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WeChatNotifier.Listener.Reading;

/// <summary>The UIA client (created and used on the listener thread only) plus the cache requests it needs.</summary>
internal sealed class UiaSession
{
    public const int NameProperty = 30005;
    public const int BoundingRectangleProperty = 30001;
    public const int ProcessIdProperty = 30002;
    public const int ControlTypeProperty = 30003;
    public const int AutomationIdProperty = 30011;
    public const int ClassNameProperty = 30012;
    public const int NativeWindowHandleProperty = 30020;
    public const int InvokePatternId = 10000;
    public const int SelectionItemPatternId = 10010;

    private const int UIA_E_ELEMENTNOTAVAILABLE = unchecked((int)0x80040201);
    private const int CO_E_OBJNOTCONNECTED = unchecked((int)0x800401FD);
    private const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    private const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
    private const int RPC_S_CALL_FAILED = unchecked((int)0x800706BE);

    public UiaSession()
    {
        Automation = (IUIAutomation6)new CUIAutomation8Class();
        Automation.AutoSetFocus = 0;           // reading must never move focus
        Automation.ConnectionTimeout = 2000;   // bound every call if WeChat stops responding
        Automation.TransactionTimeout = 3000;
        NotSupported = Automation.ReservedNotSupportedValue;
        True = Automation.CreateTrueCondition();

        Element = Automation.CreateCacheRequest();
        foreach (var id in new[] { NameProperty, ClassNameProperty, AutomationIdProperty, ControlTypeProperty, BoundingRectangleProperty, ProcessIdProperty, NativeWindowHandleProperty })
            Element.AddProperty(id);
        Element.TreeFilter = Automation.RawViewCondition;
        Element.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;

        Subtree = Element.Clone();
        Subtree.TreeScope = TreeScope.TreeScope_Subtree;
    }

    public IUIAutomation6 Automation { get; }

    public object NotSupported { get; }

    public IUIAutomationCondition True { get; }

    /// <summary>The properties the listener reads, for one element.</summary>
    public IUIAutomationCacheRequest Element { get; }

    /// <summary>Same properties for a whole subtree in one cross-process call.</summary>
    public IUIAutomationCacheRequest Subtree { get; }

    /// <summary>The element (or the whole provider) is gone: drop references, re-capture when possible.</summary>
    public static bool IsGone(COMException ex) =>
        ex.HResult is UIA_E_ELEMENTNOTAVAILABLE or CO_E_OBJNOTCONNECTED or RPC_E_DISCONNECTED or RPC_S_SERVER_UNAVAILABLE or RPC_S_CALL_FAILED;

    public string? CachedString(IUIAutomationElement el, int propertyId)
    {
        try
        {
            var v = el.GetCachedPropertyValue(propertyId);
            return v is string s && !ReferenceEquals(v, NotSupported) ? s : null;
        }
        catch (COMException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Cached BoundingRectangle as (left, top, width, height), or null.</summary>
    public double[]? CachedRect(IUIAutomationElement el)
    {
        try { return el.GetCachedPropertyValue(BoundingRectangleProperty) as double[] is { Length: 4 } r ? r : null; }
        catch (COMException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static IEnumerable<IUIAutomationElement> CachedChildren(IUIAutomationElement el)
    {
        IUIAutomationElementArray? arr = null;
        try { arr = el.GetCachedChildren(); }
        catch (COMException) { }
        for (int i = 0; i < (arr?.Length ?? 0); i++) yield return arr!.GetElement(i);
    }

    public static IEnumerable<IUIAutomationElement> Elements(IUIAutomationElementArray? arr)
    {
        for (int i = 0; i < (arr?.Length ?? 0); i++) yield return arr!.GetElement(i);
    }
}
