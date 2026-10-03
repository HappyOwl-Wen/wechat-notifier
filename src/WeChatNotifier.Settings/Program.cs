using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WeChatNotifier.Core.Configuration;

namespace WeChatNotifier.Settings;

/// <summary>Entry point: one settings window per session — a second start brings the open one to the front.</summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var single = new Mutex(initiallyOwned: true, ControlProtocol.SettingsMutex, out bool first);
        if (!first)
        {
            Native.BringToFront(Native.FindWindowW(null, ControlProtocol.SettingsWindowTitle));
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(startup =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        single.ReleaseMutex();
        return 0;
    }
}
