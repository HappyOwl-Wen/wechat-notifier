using System.Globalization;
using System.Text;

namespace WeChatInspector;

internal static class Program
{
    private const string Usage = """
        WeChatInspector - Phase 0 research tool for the WeChat Windows UI (read-only, no injection, no network)

        Commands
          processes                 Detect WeChat processes (name hints + version resource) and the main window.
              --deep                  Check the version resource of every process (finds renamed executables).
          windows                   List WeChat top-level windows with their UIA root, child counts and MSAA view.
              --helpers               Include helper processes (WeChatAppEx, ...).  --all  probe non-Qt windows too.
          dump                      Dump the UIA tree of the main window (or --hwnd).
              --bulk                  One BuildUpdatedCache(Subtree) call instead of a TreeWalker walk (timed).
              --control               Control view instead of raw view.
              --depth N / --max N     Limits (default 80 / 20000).
          locate                    Find the semantic elements (session list, session cells, current chat, message list,
                                    message cells) with the WeChatUiProfile rules and parse them. Re-run after updates.
          events                    Record UIA events + WinEvents + shell hook messages from WeChat.
              --seconds N             Stop after N seconds (default: until Ctrl+C).
              --ancestry              Resolve the ancestor chain of every UIA event sender.
              --resolve               Resolve WinEvents to UIA elements (AccessibleObjectFromEvent).
              --bounds / --location / --textsel   Include chatty BoundingRectangle / LOCATIONCHANGE / TextSelection events.
              --no-uia / --no-winevent / --no-shell / --no-foreground / --no-follow   Disable a channel.
          readprobe                 On every new-message trigger (taskbar flash / message-list scroll), report what
                                    can be read in the current window state (visible / minimized / tray).
              --seconds N             Stop after N seconds (default 600).  --stop-event <name>  stop from outside.
          hover                     Describe the UIA element under the mouse cursor (manual exploration aid).
          trayprobe                 Find WeChat's notification-area icon the way a notification click does (opens and
                                    closes the "Show hidden icons" flyout if needed; never clicks WeChat's icon).
          captureprobe              Copy the avatars of the visible conversations the way notifications do and judge
                                    them (people's avatars are never saved; WeChat's system conversations are).

        Common options
          --hwnd 0x1234             Target a specific window instead of auto-detecting the main window.
          --out <path-without-ext>  Output base name; writes <base>.log and <base>.jsonl
                                    (default: inspector-output\<command>-<timestamp>).
          --quiet                   Do not echo every line to the console.
          --show-text               Disable redaction of chat text (only use on your own machine).
          --reveal <regex>          Keep text matching <regex> unredacted (e.g. a test marker such as "WXN\d+").
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        Options o;
        try { o = Options.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.WriteLine(Usage);
            return 64;
        }

        try
        {
            return o.Command switch
            {
                "processes" => Commands.Processes(o),
                "windows" => Commands.Windows(o),
                "dump" => Commands.Dump(o),
                "events" => Commands.Events(o),
                "hover" => Commands.Hover(o),
                "readprobe" => Commands.ReadProbe(o),
                "locate" => Commands.Locate(o),
                "trayprobe" => TrayProbe.Run(),
                "captureprobe" => CaptureProbe.Run(o),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex}");
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(Usage);
        return 64;
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private set; } = "";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            o.Command = args[0].ToLowerInvariant();
            i = 1;
        }
        for (; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{a}'");
            var key = a[2..];
            string? value = null;
            int eq = key.IndexOf('=');
            if (eq >= 0)
            {
                value = key[(eq + 1)..];
                key = key[..eq];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            o._values[key] = value;
        }
        return o;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var n) ? n : fallback;

    public IntPtr? GetHwnd(string key)
    {
        var v = Get(key);
        if (string.IsNullOrEmpty(v)) return null;
        long n = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(v, CultureInfo.InvariantCulture);
        return new IntPtr(n);
    }

    public Redactor CreateRedactor() => new(
        enabled: !Has("show-text"),
        revealPattern: Get("reveal"),
        identityPrefixes: WeChatUiProfile.AutomationIdPrefixesWithIdentity,
        safeTitles: [.. WeChatUiProfile.MainWindowTitles, .. WeChatUiProfile.TechnicalWindowTitles]);

    public string OutBase(string command) =>
        Get("out") ?? Path.Combine("inspector-output", $"{command}-{DateTime.Now:yyyyMMdd-HHmmss}");
}
