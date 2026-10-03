namespace WeChatNotifier.Settings;

/// <summary>Where WeChatNotifier.exe is: next to this program when installed, else in a development checkout's build output.</summary>
internal static class AppLocator
{
    private const string NotifierName = "WeChatNotifier.exe";

    public static string? NotifierExecutable()
    {
        var here = Path.Combine(AppContext.BaseDirectory, NotifierName);
        if (File.Exists(here)) return here;

        // src\WeChatNotifier.Settings\bin\<configuration>\<framework>\<rid>\  →  src\WeChatNotifier.App\bin\<configuration>\…
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir?.Parent is { } parent; dir = parent)
        {
            if (!string.Equals(dir.Name, "bin", StringComparison.OrdinalIgnoreCase) || parent.Parent is not { } src) continue;
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).FullName[(dir.FullName.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];
            var dev = Path.Combine(src.FullName, "WeChatNotifier.App", "bin", configuration);
            return Directory.Exists(dev) ? Directory.EnumerateFiles(dev, NotifierName, SearchOption.AllDirectories).FirstOrDefault() : null;
        }
        return null;
    }
}
