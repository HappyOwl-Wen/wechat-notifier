using System.Globalization;
using System.Text;

namespace WeChatNotifier.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Privacy-safe log. Callers pass component names, states, counts, ids and latencies only — never chat text,
/// contact names or window titles. Files are size-capped and rotated, so logging can never grow without bound.
/// </summary>
public sealed class DiagnosticLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly Action<string>? _echo;
    private StreamWriter? _writer;

    public DiagnosticLog(string? directory, LogLevel minLevel, Action<string>? echo = null, long maxFileBytes = 1_000_000, int maxFiles = 3)
    {
        MinLevel = minLevel;
        _echo = echo;
        _maxFileBytes = maxFileBytes;
        _maxFiles = Math.Max(1, maxFiles);
        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "listener.log");
            Open();
        }
        catch (IOException) { _path = null; }
        catch (UnauthorizedAccessException) { _path = null; }
    }

    public static DiagnosticLog Disabled { get; } = new(null, LogLevel.Error);

    /// <summary>Changeable at run time (the debug-logging switch in the settings).</summary>
    public LogLevel MinLevel { get; set; }

    public string? FilePath => _path;

    public bool IsEnabled(LogLevel level) => level >= MinLevel;

    public void Debug(string component, string message) => Write(LogLevel.Debug, component, message);

    public void Info(string component, string message) => Write(LogLevel.Info, component, message);

    public void Warn(string component, string message) => Write(LogLevel.Warning, component, message);

    public void Error(string component, string message) => Write(LogLevel.Error, component, message);

    public void Write(LogLevel level, string component, string message)
    {
        if (level < MinLevel) return;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Label(level)} {component,-10} {message}");
        lock (_gate)
        {
            _echo?.Invoke(line);
            if (_writer is null) return;
            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
                if (_writer.BaseStream.Length > _maxFileBytes) Rotate();
            }
            catch (IOException) { _writer = null; }
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };

    private void Open() =>
        _writer = new StreamWriter(new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        for (int i = _maxFiles - 1; i >= 1; i--)
        {
            var from = i == 1 ? _path! : $"{_path}.{i - 1}";
            var to = $"{_path}.{i}";
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }
        if (_maxFiles == 1) File.Delete(_path!);
        Open();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
