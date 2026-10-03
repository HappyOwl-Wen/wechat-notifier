using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace WeChatNotifier.Notifications;

/// <summary>
/// Connects this unpackaged process to the installed Windows App Runtime (framework-dependent deployment).
/// Replaces the SDK's auto-initializer, which would show a dialog and exit the process when the runtime is
/// missing; here a missing runtime only disables notifications and the listener keeps working.
/// </summary>
internal static class WindowsAppRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>Required runtime, from the Microsoft.WindowsAppSDK.Runtime package this build references.</summary>
    public static string RequiredVersion => global::Microsoft.WindowsAppSDK.Runtime.Version.DotQuadString;

    /// <returns>0 on success, otherwise the bootstrapper's HRESULT.</returns>
    public static int Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return 0;
            var minVersion = new PackageVersion(global::Microsoft.WindowsAppSDK.Runtime.Version.UInt64);
            if (!Bootstrap.TryInitialize(global::Microsoft.WindowsAppSDK.Release.MajorMinor, global::Microsoft.WindowsAppSDK.Release.VersionTag,
                    minVersion, Bootstrap.InitializeOptions.None, out int hr))
            {
                return hr == 0 ? unchecked((int)0x80004005) : hr;
            }
            _initialized = true;
            return 0;
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_initialized) return;
            Bootstrap.Shutdown();
            _initialized = false;
        }
    }
}
