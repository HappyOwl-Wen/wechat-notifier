using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace WeChatNotifier.Setup;

/// <summary>
/// The Windows App Runtime inside the installer: the MSIX packages of the Microsoft.WindowsAppSDK.Runtime NuGet
/// package the notifier is built against — framework, DDLM, Main and Singleton, what Microsoft's
/// WindowsAppRuntimeInstall.exe installs — embedded by scripts\package.ps1. Like that installer, those not installed
/// yet in their version or newer are installed for the current user (no admin rights, nothing downloaded), framework
/// first; here with Add-AppxPackage, the PowerShell front end of the same Windows package manager.
/// </summary>
internal static class RuntimeBundle
{
    private const string ResourcePrefix = "runtime/";

    /// <summary>The last part of the family name of every Microsoft package (publisher "CN=Microsoft Corporation, …").</summary>
    private const string MicrosoftPublisherId = "8wekyb3d8bbwe";

    /// <summary>
    /// One per machine for every Windows App Runtime version; its background process (push notifications) may be
    /// running. Microsoft's installer updates it with "force application shutdown" — the only package it does that
    /// for — and so does this one. The notifier starts it again when it registers for notifications.
    /// </summary>
    private const string SingletonName = "MicrosoftCorporationII.WinAppRuntime.Singleton";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    internal sealed class Package(string resource, string name, Version version, string architecture, bool isFramework)
    {
        public string Resource { get; } = resource;

        public string Name { get; } = name;

        public Version Version { get; } = version;

        public string Architecture { get; } = architecture;

        /// <summary>The framework package; the others depend on it.</summary>
        public bool IsFramework { get; } = isFramework;
    }

    internal sealed class InstallFailedException(string message) : Exception(message);

    /// <summary>The bundled packages not installed for the current user in their version or newer (none in a build without them).</summary>
    public static IReadOnlyList<Package> Missing() => Bundled().Where(p => !IsInstalled(p)).ToList();

    /// <summary>Installs <paramref name="packages"/>, framework first; throws <see cref="InstallFailedException"/> when any is still missing afterwards.</summary>
    public static void Install(IReadOnlyList<Package> packages)
    {
        var folder = Path.Combine(Path.GetTempPath(), "WeChatNotifierSetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        try
        {
            var files = new List<(string Path, bool Force)>();
            foreach (var package in packages.OrderByDescending(p => p.IsFramework))
            {
                var file = Path.Combine(folder, package.Resource.Substring(ResourcePrefix.Length));
                using (var source = Resource(package.Resource))
                using (var target = File.Create(file))
                {
                    source.CopyTo(target);
                }
                files.Add((file, package.Name == SingletonName));
            }

            string error = AddPackages(files, Path.Combine(folder, "error.txt"));
            // Judged by what is installed now: a newer version that went in meanwhile counts as well.
            var stillMissing = packages.Where(p => !IsInstalled(p)).Select(p => p.Name).ToList();
            if (stillMissing.Count > 0)
            {
                throw new InstallFailedException($"Windows App Runtime 没有装上（{string.Join("、", stillMissing)}）。"
                                                 + (error.Length > 0 ? "\n\n" + Shorten(error) : ""));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs Add-AppxPackage on each file in turn. Returns the error message, or "" when PowerShell reported none.</summary>
    private static string AddPackages(IReadOnlyList<(string Path, bool Force)> files, string errorFile)
    {
        // A plain script with no double quotes inside. The files travel in an environment variable, '|'-separated and
        // '*'-marked for "force application shutdown" (neither can occur in a path); the error message comes back in a
        // file, so no console encodings are involved.
        const string script =
            "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
            "foreach ($f in $env:WECHATNOTIFIER_PACKAGES.Split('|')) { $force = $f.StartsWith('*'); " +
            "try { Add-AppxPackage -Path $f.TrimStart('*') -ForceApplicationShutdown:$force } " +
            "catch { [IO.File]::WriteAllText($env:WECHATNOTIFIER_ERROR, $_.Exception.Message); exit 1 } }";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.EnvironmentVariables["WECHATNOTIFIER_PACKAGES"] = string.Join("|", files.Select(f => (f.Force ? "*" : "") + f.Path));
        start.EnvironmentVariables["WECHATNOTIFIER_ERROR"] = errorFile;

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            throw new InstallFailedException($"无法启动 Windows PowerShell 来安装 Windows App Runtime：{ex.Message}");
        }
        using (process)
        {
            if (process is null) return "";
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                return $"安装超过了 {Timeout.TotalMinutes:0} 分钟，已停止。";
            }
            if (process.ExitCode == 0) return "";
            return File.Exists(errorFile) ? File.ReadAllText(errorFile, Encoding.UTF8).Trim() : $"PowerShell 退出代码 {process.ExitCode}。";
        }
    }

    /// <summary>The packages inside this installer, read from their manifests.</summary>
    private static List<Package> Bundled()
    {
        var packages = new List<Package>();
        foreach (var resource in typeof(RuntimeBundle).Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            using var msix = new ZipArchive(Resource(resource), ZipArchiveMode.Read);
            using var manifest = (msix.GetEntry("AppxManifest.xml") ?? throw new InvalidDataException($"{resource}：没有 AppxManifest.xml")).Open();
            var root = XDocument.Load(manifest).Root ?? throw new InvalidDataException($"{resource}：AppxManifest.xml 是空的");
            var ns = root.Name.Namespace;
            var identity = root.Element(ns + "Identity") ?? throw new InvalidDataException($"{resource}：AppxManifest.xml 里没有 Identity");
            packages.Add(new Package(
                resource,
                (string?)identity.Attribute("Name") ?? "",
                Version.Parse((string?)identity.Attribute("Version") ?? "0.0.0.0"),
                (string?)identity.Attribute("ProcessorArchitecture") ?? "neutral",
                string.Equals((string?)root.Element(ns + "Properties")?.Element(ns + "Framework"), "true", StringComparison.OrdinalIgnoreCase)));
        }
        return packages;
    }

    /// <summary>Whether a package of the same family and architecture, this version or newer, is installed for the current user.</summary>
    private static bool IsInstalled(Package package) =>
        // Full names: Name_Version_Architecture_ResourceId_PublisherId.
        Native.PackagesOfFamily($"{package.Name}_{MicrosoftPublisherId}").Any(fullName =>
        {
            var parts = fullName.Split('_');
            return parts.Length == 5 && string.Equals(parts[2], package.Architecture, StringComparison.OrdinalIgnoreCase)
                   && Version.TryParse(parts[1], out var installed) && installed >= package.Version;
        });

    private static Stream Resource(string name) =>
        typeof(RuntimeBundle).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException($"安装程序里没有 {name}。");

    private static string Shorten(string text) => text.Length <= 600 ? text : text.Substring(0, 600) + "…";
}
