using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.Settings;

/// <summary>
/// Requests to the running notifier (<see cref="ControlProtocol"/>): WM_COPYDATA to its hidden window, answers to
/// a message-only window of ours. Synchronous with a short time-out; used on the UI thread. Local window
/// messages only — nothing leaves the session.
/// </summary>
internal sealed class NotifierChannel : IDisposable
{
    private const string ReplyWindowClass = "WeChatNotifier.Settings.Reply";
    private const uint TimeoutMs = 3000;

    private readonly Native.WndProc _proc; // referenced by the window class while the window lives
    private readonly IntPtr _replyWindow;
    private string? _reply;

    public NotifierChannel()
    {
        _proc = OnMessage;
        var instance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            lpszClassName = ReplyWindowClass,
        };
        Native.RegisterClassExW(ref wc);
        _replyWindow = Native.CreateWindowExW(0, ReplyWindowClass, "", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
    }

    public bool NotifierRunning => Notifier() != IntPtr.Zero;

    /// <summary>Sends a command; true when the notifier handled it (and, for a test notification, showed it).</summary>
    public bool Send(long command)
    {
        var notifier = Notifier();
        if (notifier == IntPtr.Zero) return false;
        var data = new Native.COPYDATASTRUCT { dwData = (IntPtr)command };
        return Native.SendMessageTimeoutW(notifier, Native.WM_COPYDATA, _replyWindow, ref data, Native.SMTO_ABORTIFHUNG, TimeoutMs, out var result) != IntPtr.Zero
               && result != IntPtr.Zero;
    }

    /// <summary>The notifier's state; null when it does not run or does not answer. The answer arrives while we wait.</summary>
    public StatusSnapshot? RequestStatus()
    {
        _reply = null;
        if (!Send(ControlProtocol.StatusRequest)) return null;
        return _reply is { } json ? StatusSnapshot.FromJson(json) : null;
    }

    /// <summary>Starts WeChatNotifier.exe (normal start: it announces itself with a notification).</summary>
    public static bool StartNotifier()
    {
        if (AppLocator.NotifierExecutable() is not { } exe) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_replyWindow != IntPtr.Zero) Native.DestroyWindow(_replyWindow);
    }

    private static IntPtr Notifier() => Native.FindWindowW(ControlProtocol.WindowClass, null);

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_COPYDATA)
        {
            var data = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(lParam);
            if (data.dwData.ToInt64() == ControlProtocol.StatusReply && data.lpData != IntPtr.Zero)
            {
                _reply = Marshal.PtrToStringUTF8(data.lpData, data.cbData);
                return 1;
            }
            return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
