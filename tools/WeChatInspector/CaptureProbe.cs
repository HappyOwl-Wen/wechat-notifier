using System.Diagnostics;
using System.Globalization;
using WeChatNotifier.Core.Imaging;
using WeChatNotifier.Diagnostics;
using WeChatNotifier.Listener.Reading;
using ListenerNative = WeChatNotifier.Listener.Interop.Native;

namespace WeChatInspector;

/// <summary>
/// Checks the avatar copies on the real window with the listener's own code: where the avatar sits in each
/// visible session cell (found by its pixels, <see cref="ImageAnalysis.FindAvatar"/>), as numbers relative to the
/// cell height — the basis for <see cref="WeChatUiProfile.AvatarRatio"/>. Privacy: avatars of people are only
/// measured, never saved; for WeChat's own system conversations (standard icons) the copied area and the round
/// notification avatar are written to inspector-output for a look. Needs the main window on screen.
/// </summary>
internal static class CaptureProbe
{
    private static readonly string[] SystemConversations = ["文件传输助手", "公众号", "服务号", "订阅号", "微信团队", "File Transfer", "Official Accounts"];

    public static int Run(Options o)
    {
        ListenerNative.SetThreadDpiAwarenessContext(ListenerNative.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var (hwnd, _, describe) = Commands.ResolveTarget(new Uia(), o);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine(describe);
            return 2;
        }
        if (!WindowCapture.CanCapture(hwnd))
        {
            Console.WriteLine("The main window is not on screen (minimized, in the tray or on another desktop): open WeChat and run again.");
            return 2;
        }

        var reader = new WeChatUiReader(new UiaSession(), DiagnosticLog.Disabled);
        if (reader.Capture(hwnd) is null)
        {
            Console.WriteLine("No conversation list on screen (another tab open?).");
            return 2;
        }
        var cells = reader.CellRects();
        var sw = Stopwatch.StartNew();
        var areas = WindowCapture.CaptureRegions(hwnd, cells.Select(c => WeChatUiProfile.SessionAvatarArea(c.Rect)).ToList());
        Console.WriteLine($"{cells.Count} conversations on screen; window DPI {ListenerNative.GetDpiForWindow(hwnd)}; one PrintWindow + crops: {sw.ElapsedMilliseconds} ms");
        if (areas is null)
        {
            Console.WriteLine("PrintWindow failed.");
            return 1;
        }

        Directory.CreateDirectory("inspector-output");
        var ratios = new List<(double X, double Y, double Side)>();
        for (int i = 0; i < cells.Count; i++)
        {
            var (title, rect, unread) = cells[i];
            var area = areas[i];
            bool system = SystemConversations.Contains(title);
            string label = system ? title : $"conversation {i + 1}";
            string badge = unread > 0 ? " [unread badge]" : "";
            if (area is null)
            {
                Console.WriteLine($"  {label,-14} cell {rect.Width}x{rect.Height}: outside the window");
                continue;
            }
            int h = rect.Height;
            var found = ImageAnalysis.FindAvatar(area, (int)(h * WeChatUiProfile.MinAvatarRatio), (int)(h * WeChatUiProfile.MaxAvatarRatio));
            if (found is not { } f)
            {
                Console.WriteLine($"  {label,-14} cell {rect.Width}x{rect.Height}: avatar not found by its pixels{badge}");
                continue;
            }
            var r = (f.X / (double)h, f.Y / (double)h, f.Width / (double)h);
            ratios.Add(r);
            string saved = "";
            if (system)
            {
                var areaFile = Path.Combine("inspector-output", $"avatar-{i + 1}-area.png");
                var roundFile = Path.Combine("inspector-output", $"avatar-{i + 1}-round.png");
                File.WriteAllBytes(areaFile, PngEncoder.Encode(area));
                File.WriteAllBytes(roundFile, PngEncoder.Encode(AvatarImage.Round(area.Crop(f), WeChatNotifier.Core.Notifications.ToastScale.Pixels(WeChatNotifier.Core.Notifications.ToastScale.AvatarEffectivePixels, WeChatNotifier.Core.Notifications.ToastScale.PrimaryDpi()))));
                saved = $" -> {areaFile}, {roundFile}";
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {label,-14} cell {rect.Width}x{h}  avatar at ({f.X},{f.Y}) side {f.Width}  = x {r.Item1:0.000}h  y {r.Item2:0.000}h  side {r.Item3:0.000}h{badge}{saved}"));
        }

        if (ratios.Count > 0)
        {
            double Median(IEnumerable<double> v) => v.OrderBy(x => x).ElementAt(ratios.Count / 2);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"median: x {Median(ratios.Select(t => t.X)):0.000}h  y {Median(ratios.Select(t => t.Y)):0.000}h  side {Median(ratios.Select(t => t.Side)):0.000}h " +
                $"(profile estimate: side {WeChatUiProfile.AvatarRatio:0.000}h, centred)"));
        }
        return 0;
    }
}
