using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WeChatInspector;

/// <summary>Numeric ids from UIAutomationClient.h, plus readable names for them.</summary>
internal static class UiaIds
{
    public const int RuntimeId = 30000, BoundingRectangle = 30001, ProcessId = 30002, ControlType = 30003,
        LocalizedControlType = 30004, Name = 30005, HasKeyboardFocus = 30008, IsKeyboardFocusable = 30009,
        IsEnabled = 30010, AutomationId = 30011, ClassName = 30012, HelpText = 30013, IsControlElement = 30016,
        IsContentElement = 30017, NativeWindowHandle = 30020, ItemType = 30021, IsOffscreen = 30022,
        FrameworkId = 30024, ItemStatus = 30026, ValueValue = 30045, RangeValueValue = 30047,
        ScrollVerticalPercent = 30055, ScrollVerticalViewSize = 30056, ExpandCollapseState = 30070,
        SelectionItemIsSelected = 30079, ToggleState = 30086, LegacyChildId = 30091, LegacyName = 30092,
        LegacyValue = 30093, LegacyDescription = 30094, LegacyRole = 30095, LegacyState = 30096,
        LegacyDefaultAction = 30100, LiveSetting = 30135, PositionInSet = 30152, SizeOfSet = 30153,
        Level = 30154, FullDescription = 30159;

    public static readonly (int Id, string Name)[] PatternAvailability =
    [
        (30027, "Dock"), (30028, "ExpandCollapse"), (30029, "GridItem"), (30030, "Grid"), (30031, "Invoke"),
        (30032, "MultipleView"), (30033, "RangeValue"), (30034, "Scroll"), (30035, "ScrollItem"),
        (30036, "SelectionItem"), (30037, "Selection"), (30038, "Table"), (30039, "TableItem"), (30040, "Text"),
        (30041, "Toggle"), (30042, "Transform"), (30043, "Value"), (30044, "Window"), (30090, "LegacyIAccessible"),
        (30108, "ItemContainer"), (30109, "VirtualizedItem"), (30110, "SynchronizedInput"), (30112, "ObjectModel"),
        (30118, "Annotation"), (30119, "Text2"), (30127, "Styles"), (30128, "Spreadsheet"), (30132, "SpreadsheetItem"),
        (30134, "Transform2"), (30136, "TextChild"), (30137, "Drag"), (30141, "DropTarget"), (30149, "TextEdit"),
        (30151, "CustomNavigation"), (30168, "Selection2"),
    ];

    private static readonly string[] ControlTypeNames =
    [
        "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image", "ListItem", "List", "Menu",
        "MenuBar", "MenuItem", "ProgressBar", "RadioButton", "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab",
        "TabItem", "Text", "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb", "DataGrid",
        "DataItem", "Document", "SplitButton", "Window", "Pane", "Header", "HeaderItem", "Table", "TitleBar",
        "Separator", "SemanticZoom", "AppBar",
    ];

    public static string ControlTypeName(int id) =>
        id >= 50000 && id - 50000 < ControlTypeNames.Length ? ControlTypeNames[id - 50000] : $"CT{id}";

    public const int EvStructureChanged = 20002, EvAutomationFocusChanged = 20005, EvWindowOpened = 20016,
        EvTextChanged = 20015, EvTextSelectionChanged = 20014, EvNotification = 20035;

    private static readonly Dictionary<int, string> EventNames = new()
    {
        [20000] = "ToolTipOpened", [20001] = "ToolTipClosed", [20002] = "StructureChanged", [20003] = "MenuOpened",
        [20004] = "PropertyChanged", [20005] = "FocusChanged", [20006] = "AsyncContentLoaded", [20007] = "MenuClosed",
        [20008] = "LayoutInvalidated", [20009] = "Invoke_Invoked", [20010] = "SelectionItem_ElementAddedToSelection",
        [20011] = "SelectionItem_ElementRemovedFromSelection", [20012] = "SelectionItem_ElementSelected",
        [20013] = "Selection_Invalidated", [20014] = "Text_TextSelectionChanged", [20015] = "Text_TextChanged",
        [20016] = "Window_WindowOpened", [20017] = "Window_WindowClosed", [20018] = "MenuModeStart",
        [20019] = "MenuModeEnd", [20023] = "SystemAlert", [20024] = "LiveRegionChanged",
        [20025] = "HostedFragmentRootsInvalidated", [20032] = "TextEdit_TextChanged",
        [20033] = "TextEdit_ConversionTargetChanged", [20034] = "Changes", [20035] = "Notification",
        [20036] = "ActiveTextPositionChanged",
    };

    public static string EventName(int id) => EventNames.TryGetValue(id, out var n) ? n : $"Event{id}";

    /// <summary>Automation events registered through the generic handler (the rest have dedicated handlers).</summary>
    public static readonly int[] GenericEvents =
        [20000, 20001, 20003, 20006, 20007, 20008, 20009, 20010, 20011, 20012, 20013, 20015, 20016, 20017, 20018, 20019, 20023, 20024, 20025];

    private static readonly Dictionary<int, string> PropertyNames = new()
    {
        [BoundingRectangle] = "BoundingRectangle", [Name] = "Name", [HasKeyboardFocus] = "HasKeyboardFocus",
        [IsEnabled] = "IsEnabled", [AutomationId] = "AutomationId", [HelpText] = "HelpText", [ItemType] = "ItemType",
        [IsOffscreen] = "IsOffscreen", [ItemStatus] = "ItemStatus", [ValueValue] = "Value.Value",
        [RangeValueValue] = "RangeValue.Value", [ScrollVerticalPercent] = "Scroll.VerticalScrollPercent",
        [ScrollVerticalViewSize] = "Scroll.VerticalViewSize", [ExpandCollapseState] = "ExpandCollapse.State",
        [SelectionItemIsSelected] = "SelectionItem.IsSelected", [ToggleState] = "Toggle.ToggleState",
        [LegacyName] = "LegacyIAccessible.Name", [LegacyValue] = "LegacyIAccessible.Value",
        [LegacyState] = "LegacyIAccessible.State", [LiveSetting] = "LiveSetting", [PositionInSet] = "PositionInSet",
        [SizeOfSet] = "SizeOfSet", [FullDescription] = "FullDescription", [ControlType] = "ControlType",
        [ClassName] = "ClassName",
    };

    public static string PropertyName(int id) => PropertyNames.TryGetValue(id, out var n) ? n : $"Prop{id}";

    /// <summary>Properties watched by the recorder. BoundingRectangle is opt-in because it is very chatty.</summary>
    public static readonly int[] WatchedProperties =
    [
        Name, ValueValue, ItemStatus, HelpText, IsOffscreen, SelectionItemIsSelected, LegacyValue, LegacyState,
        ExpandCollapseState, ToggleState, FullDescription, IsEnabled, ScrollVerticalPercent, ScrollVerticalViewSize,
        PositionInSet, SizeOfSet, LiveSetting, ItemType, RangeValueValue,
    ];

    public static readonly Dictionary<int, string> StructureChangeNames = new()
    {
        [0] = "ChildAdded", [1] = "ChildRemoved", [2] = "ChildrenInvalidated",
        [3] = "ChildrenBulkAdded", [4] = "ChildrenBulkRemoved", [5] = "ChildrenReordered",
    };
}

/// <summary>A plain snapshot of an element's cached properties (safe to keep after the element is gone).</summary>
internal sealed class ElementInfo
{
    public string ControlType { get; set; } = "";
    public string? Name { get; set; }
    public string? AutomationId { get; set; }
    public string? ClassName { get; set; }
    public string? FrameworkId { get; set; }
    public double[]? Rect { get; set; }
    public int[]? RuntimeId { get; set; }
    public int ProcessId { get; set; }
    public long NativeWindowHandle { get; set; }
    public bool? IsOffscreen { get; set; }
    public bool? IsEnabled { get; set; }
    public bool? IsKeyboardFocusable { get; set; }
    public bool? HasKeyboardFocus { get; set; }
    public bool? IsControlElement { get; set; }
    public bool? IsContentElement { get; set; }
    public string? LocalizedControlType { get; set; }
    public string? HelpText { get; set; }
    public string? ItemStatus { get; set; }
    public string? ItemType { get; set; }
    public string? FullDescription { get; set; }
    public string? Value { get; set; }
    public bool? IsSelected { get; set; }
    public int? PositionInSet { get; set; }
    public int? SizeOfSet { get; set; }
    public int? Level { get; set; }
    public string? LegacyName { get; set; }
    public string? LegacyValue { get; set; }
    public string? LegacyDescription { get; set; }
    public string? LegacyDefaultAction { get; set; }
    public int? LegacyRole { get; set; }
    public int? LegacyState { get; set; }
    public List<string>? Patterns { get; set; }
    public int? ChildCount { get; set; }

    public string RuntimeIdText => RuntimeId is null ? "-" : "[" + string.Join(",", RuntimeId.Select(x => x.ToString("X"))) + "]";

    public string RectText => Rect is { Length: 4 } r ? $"[{r[0]:0},{r[1]:0} {r[2]:0}x{r[3]:0}]" : "[-]";
}

/// <summary>Owns the UIA client object and the cache requests used by the inspector.</summary>
internal sealed class Uia
{
    public Uia()
    {
        Automation = (IUIAutomation6)new CUIAutomation8Class();
        Automation.AutoSetFocus = 0;          // never move focus as a side effect of a read
        Automation.ConnectionTimeout = 2000;  // don't hang for long on an unresponsive provider
        Automation.TransactionTimeout = 5000;
        NotSupported = Automation.ReservedNotSupportedValue;

        FullCache = CreateCache(full: true);
        EventCache = CreateCache(full: false);
    }

    public IUIAutomation6 Automation { get; }

    public object NotSupported { get; }

    /// <summary>Everything the dump prints, including pattern availability.</summary>
    public IUIAutomationCacheRequest FullCache { get; }

    /// <summary>The small set needed to describe an event sender.</summary>
    public IUIAutomationCacheRequest EventCache { get; }

    private static readonly int[] EventProps =
    [
        UiaIds.RuntimeId, UiaIds.BoundingRectangle, UiaIds.ProcessId, UiaIds.ControlType, UiaIds.Name,
        UiaIds.AutomationId, UiaIds.ClassName, UiaIds.FrameworkId, UiaIds.IsOffscreen, UiaIds.NativeWindowHandle,
        UiaIds.ItemStatus, UiaIds.HelpText,
    ];

    private static readonly int[] FullProps =
    [
        .. EventProps,
        UiaIds.LocalizedControlType, UiaIds.HasKeyboardFocus, UiaIds.IsKeyboardFocusable, UiaIds.IsEnabled,
        UiaIds.IsControlElement, UiaIds.IsContentElement, UiaIds.ItemType, UiaIds.FullDescription, UiaIds.ValueValue,
        UiaIds.SelectionItemIsSelected, UiaIds.PositionInSet, UiaIds.SizeOfSet, UiaIds.Level, UiaIds.LegacyName,
        UiaIds.LegacyValue, UiaIds.LegacyDescription, UiaIds.LegacyRole, UiaIds.LegacyState, UiaIds.LegacyDefaultAction,
    ];

    private IUIAutomationCacheRequest CreateCache(bool full)
    {
        var cr = Automation.CreateCacheRequest();
        foreach (var id in full ? FullProps : EventProps) cr.AddProperty(id);
        if (full)
            foreach (var (id, _) in UiaIds.PatternAvailability) cr.AddProperty(id);
        cr.TreeFilter = Automation.RawViewCondition;
        cr.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        return cr;
    }

    public string? TryGetRootClassName(IntPtr hwnd)
    {
        try { return Automation.ElementFromHandleBuildCache(hwnd, EventCache).CachedClassName; }
        catch (COMException) { return null; }
    }

    public ElementInfo Snapshot(IUIAutomationElement el, bool full)
    {
        var info = new ElementInfo
        {
            ControlType = UiaIds.ControlTypeName(Int(el, UiaIds.ControlType) ?? 0),
            Name = Str(el, UiaIds.Name),
            AutomationId = Str(el, UiaIds.AutomationId),
            ClassName = Str(el, UiaIds.ClassName),
            FrameworkId = Str(el, UiaIds.FrameworkId),
            Rect = Get(el, UiaIds.BoundingRectangle) as double[],
            RuntimeId = Get(el, UiaIds.RuntimeId) as int[],
            ProcessId = Int(el, UiaIds.ProcessId) ?? 0,
            NativeWindowHandle = Int(el, UiaIds.NativeWindowHandle) ?? 0,
            IsOffscreen = Bool(el, UiaIds.IsOffscreen),
            ItemStatus = Str(el, UiaIds.ItemStatus),
            HelpText = Str(el, UiaIds.HelpText),
        };
        if (!full) return info;

        info.LocalizedControlType = Str(el, UiaIds.LocalizedControlType);
        info.HasKeyboardFocus = Bool(el, UiaIds.HasKeyboardFocus);
        info.IsKeyboardFocusable = Bool(el, UiaIds.IsKeyboardFocusable);
        info.IsEnabled = Bool(el, UiaIds.IsEnabled);
        info.IsControlElement = Bool(el, UiaIds.IsControlElement);
        info.IsContentElement = Bool(el, UiaIds.IsContentElement);
        info.ItemType = Str(el, UiaIds.ItemType);
        info.FullDescription = Str(el, UiaIds.FullDescription);
        info.Value = Str(el, UiaIds.ValueValue);
        info.IsSelected = Bool(el, UiaIds.SelectionItemIsSelected);
        info.PositionInSet = Int(el, UiaIds.PositionInSet);
        info.SizeOfSet = Int(el, UiaIds.SizeOfSet);
        info.Level = Int(el, UiaIds.Level);
        info.LegacyName = Str(el, UiaIds.LegacyName);
        info.LegacyValue = Str(el, UiaIds.LegacyValue);
        info.LegacyDescription = Str(el, UiaIds.LegacyDescription);
        info.LegacyDefaultAction = Str(el, UiaIds.LegacyDefaultAction);
        info.LegacyRole = Int(el, UiaIds.LegacyRole);
        info.LegacyState = Int(el, UiaIds.LegacyState);
        info.Patterns = UiaIds.PatternAvailability.Where(p => Bool(el, p.Id) == true).Select(p => p.Name).ToList();
        return info;
    }

    private object? Get(IUIAutomationElement el, int id)
    {
        try
        {
            var v = el.GetCachedPropertyValue(id);
            return v is null || ReferenceEquals(v, NotSupported) || Marshal.IsComObject(v) ? null : v;
        }
        catch (COMException) { return null; }
        catch (ArgumentException) { return null; } // E_INVALIDARG: property not in the cache request
        catch (InvalidCastException) { return null; }
    }

    private string? Str(IUIAutomationElement el, int id) => Get(el, id) is string s && s.Length > 0 ? s : null;

    private bool? Bool(IUIAutomationElement el, int id) => Get(el, id) switch
    {
        bool b => b,
        int i => i != 0,
        _ => null,
    };

    private int? Int(IUIAutomationElement el, int id) => Get(el, id) switch
    {
        int i => i,
        uint u => unchecked((int)u),
        long l => (int)l,
        _ => null,
    };

    /// <summary>Formats an arbitrary property value from an event for display (text is redacted).</summary>
    public static string FormatValue(object? v, Redactor r) => v switch
    {
        null => "null",
        string s => "\"" + r.Apply(s) + "\"",
        bool b => b ? "true" : "false",
        double[] d => "[" + string.Join(",", d.Select(x => x.ToString("0"))) + "]",
        int[] a => "[" + string.Join(",", a) + "]",
        Array a => $"array({a.Length})",
        _ when Marshal.IsComObject(v) => "<com-object>",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "?",
    };
}
