using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatInspector;

/// <summary>Thread-safe output: human-readable lines to console (+ optional .log) and records to optional .jsonl.</summary>
internal sealed class Sink : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly StreamWriter? _text;
    private readonly StreamWriter? _json;

    public Sink(string? outBase, bool console)
    {
        Console = console;
        if (outBase is null) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(outBase));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _text = new StreamWriter(outBase + ".log", false, new UTF8Encoding(false)) { AutoFlush = false };
        _json = new StreamWriter(outBase + ".jsonl", false, new UTF8Encoding(false)) { AutoFlush = false };
        TextPath = outBase + ".log";
        JsonPath = outBase + ".jsonl";
    }

    public bool Console { get; }
    public string? TextPath { get; }
    public string? JsonPath { get; }

    public void Line(string line)
    {
        lock (_gate)
        {
            if (Console) System.Console.WriteLine(line);
            _text?.WriteLine(line);
        }
    }

    /// <summary>Always goes to the console, even in --quiet mode (headers, summaries).</summary>
    public void Always(string line)
    {
        lock (_gate)
        {
            System.Console.WriteLine(line);
            _text?.WriteLine(line);
        }
    }

    public void Record(object record)
    {
        if (_json is null) return;
        var s = JsonSerializer.Serialize(record, JsonOptions);
        lock (_gate) _json.WriteLine(s);
    }

    public void Flush()
    {
        lock (_gate)
        {
            _text?.Flush();
            _json?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _text?.Dispose();
            _json?.Dispose();
        }
    }
}
