namespace WeChatInspector;

internal static class WinEventNames
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x0001] = "SYSTEM_SOUND", [0x0002] = "SYSTEM_ALERT", [0x0003] = "SYSTEM_FOREGROUND",
        [0x0004] = "SYSTEM_MENUSTART", [0x0005] = "SYSTEM_MENUEND", [0x0006] = "SYSTEM_MENUPOPUPSTART",
        [0x0007] = "SYSTEM_MENUPOPUPEND", [0x0008] = "SYSTEM_CAPTURESTART", [0x0009] = "SYSTEM_CAPTUREEND",
        [0x000A] = "SYSTEM_MOVESIZESTART", [0x000B] = "SYSTEM_MOVESIZEEND", [0x000C] = "SYSTEM_CONTEXTHELPSTART",
        [0x000D] = "SYSTEM_CONTEXTHELPEND", [0x000E] = "SYSTEM_DRAGDROPSTART", [0x000F] = "SYSTEM_DRAGDROPEND",
        [0x0010] = "SYSTEM_DIALOGSTART", [0x0011] = "SYSTEM_DIALOGEND", [0x0012] = "SYSTEM_SCROLLINGSTART",
        [0x0013] = "SYSTEM_SCROLLINGEND", [0x0014] = "SYSTEM_SWITCHSTART", [0x0015] = "SYSTEM_SWITCHEND",
        [0x0016] = "SYSTEM_MINIMIZESTART", [0x0017] = "SYSTEM_MINIMIZEEND", [0x0020] = "SYSTEM_DESKTOPSWITCH",
        [0x8000] = "OBJECT_CREATE", [0x8001] = "OBJECT_DESTROY", [0x8002] = "OBJECT_SHOW", [0x8003] = "OBJECT_HIDE",
        [0x8004] = "OBJECT_REORDER", [0x8005] = "OBJECT_FOCUS", [0x8006] = "OBJECT_SELECTION",
        [0x8007] = "OBJECT_SELECTIONADD", [0x8008] = "OBJECT_SELECTIONREMOVE", [0x8009] = "OBJECT_SELECTIONWITHIN",
        [0x800A] = "OBJECT_STATECHANGE", [0x800B] = "OBJECT_LOCATIONCHANGE", [0x800C] = "OBJECT_NAMECHANGE",
        [0x800D] = "OBJECT_DESCRIPTIONCHANGE", [0x800E] = "OBJECT_VALUECHANGE", [0x800F] = "OBJECT_PARENTCHANGE",
        [0x8010] = "OBJECT_HELPCHANGE", [0x8011] = "OBJECT_DEFACTIONCHANGE", [0x8012] = "OBJECT_ACCELERATORCHANGE",
        [0x8013] = "OBJECT_INVOKED", [0x8014] = "OBJECT_TEXTSELECTIONCHANGED", [0x8015] = "OBJECT_CONTENTSCROLLED",
        [0x8016] = "SYSTEM_ARRANGMENTPREVIEW", [0x8017] = "OBJECT_CLOAKED", [0x8018] = "OBJECT_UNCLOAKED",
        [0x8019] = "OBJECT_LIVEREGIONCHANGED", [0x8020] = "OBJECT_HOSTEDOBJECTSINVALIDATED",
        [0x8021] = "OBJECT_DRAGSTART", [0x8022] = "OBJECT_DRAGCANCEL", [0x8023] = "OBJECT_DRAGCOMPLETE",
        [0x8024] = "OBJECT_DRAGENTER", [0x8025] = "OBJECT_DRAGLEAVE", [0x8026] = "OBJECT_DRAGDROPPED",
        [0x8027] = "OBJECT_IME_SHOW", [0x8028] = "OBJECT_IME_HIDE", [0x8029] = "OBJECT_IME_CHANGE",
        [0x8030] = "OBJECT_TEXTEDIT_CONVERSIONTARGETCHANGED",
    };

    public static string Name(uint ev)
    {
        if (Names.TryGetValue(ev, out var n)) return n;
        if (ev is >= 0x4E00 and <= 0x4EFF) return $"UIA_EVENTID+0x{ev - 0x4E00:X2}";
        if (ev is >= 0x7500 and <= 0x75FF) return $"UIA_PROPID+0x{ev - 0x7500:X2}";
        if (ev is >= 0xA000 and <= 0xAFFF) return $"AIA+0x{ev - 0xA000:X3}";
        if (ev is >= 0x0101 and <= 0x01FF) return $"OEM+0x{ev - 0x0100:X2}";
        return $"0x{ev:X4}";
    }

    public static string ObjectId(int id) => id switch
    {
        0 => "WINDOW",
        -1 => "SYSMENU",
        -2 => "TITLEBAR",
        -3 => "MENU",
        -4 => "CLIENT",
        -5 => "VSCROLL",
        -6 => "HSCROLL",
        -7 => "SIZEGRIP",
        -8 => "CARET",
        -9 => "CURSOR",
        -10 => "ALERT",
        -11 => "SOUND",
        -16 => "NATIVEOM",
        -25 => "UIA_ROOT",
        _ => id.ToString(),
    };

    /// <summary>Shell hook codes whose lParam is an HWND.</summary>
    public static string? ShellCode(int code) => code switch
    {
        1 => "HSHELL_WINDOWCREATED",
        2 => "HSHELL_WINDOWDESTROYED",
        4 => "HSHELL_WINDOWACTIVATED",
        6 => "HSHELL_REDRAW",
        13 => "HSHELL_WINDOWREPLACED",
        14 => "HSHELL_WINDOWREPLACING",
        16 => "HSHELL_MONITORCHANGED",
        0x8004 => "HSHELL_RUDEAPPACTIVATED",
        0x8006 => "HSHELL_FLASH",
        _ => null,
    };
}
