using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CordiSharp.Logger;

/// <summary>Log severity levels.</summary>
public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
}

/// <summary>A single log message.</summary>
public sealed class LogMessage
{
    public long Sn { get; init; }
    public DateTimeOffset Ts { get; init; }
    public string Name { get; init; } = "";
    public LogLevel Level { get; init; }
    public object?[] Args { get; init; } = [];
    public Fiber? Fiber { get; init; }
}

/// <summary>Receives formatted log messages.</summary>
public interface ILogExporter
{
    void Export(LogMessage message);
}

/// <summary>A named logger with leveled methods (mirrors cordis Logger).</summary>
public sealed class Logger
{
    private readonly LoggerService _service;
    public string Name { get; }
    public LogLevel? Level { get; }

    internal Logger(LoggerService service, string name, LogLevel? level)
    {
        _service = service;
        Name = name;
        Level = level;
    }

    public void Error(object? format, params object?[] args) => _service.Log(Name, LogLevel.Error, format, args);
    public void Warn(object? format, params object?[] args) => _service.Log(Name, LogLevel.Warn, format, args);
    public void Info(object? format, params object?[] args) => _service.Log(Name, LogLevel.Info, format, args);
    public void Debug(object? format, params object?[] args) => _service.Log(Name, LogLevel.Debug, format, args);

    public void Error(Exception error) => _service.Log(Name, LogLevel.Error, error, []);
}

/// <summary>The logger service: exporters + buffer + named loggers.
/// Ports cordis <c>LoggerService</c>.</summary>
public sealed partial class LoggerService
{
    private readonly Context _ctx;
    private readonly Dictionary<int, ILogExporter> _exporters = new();
    private long _sn;
    private int _exporterSn;

    public int BufferSize { get; set; } = 1000;
    public List<LogMessage> Buffer { get; } = [];

    internal LoggerService(Context ctx)
    {
        _ctx = ctx;
        Exporter(new BufferExporter(this));
    }

    private sealed class BufferExporter(LoggerService service) : ILogExporter
    {
        public void Export(LogMessage message)
        {
            service.Buffer.Add(message);
            var overflow = service.Buffer.Count - service.BufferSize;
            if (overflow >= 0 && overflow < service.BufferSize)
            {
                service.Buffer.RemoveRange(0, Math.Min(overflow + 1, service.Buffer.Count));
            }
            else if (overflow >= service.BufferSize)
            {
                service.Buffer.Clear();
                service.Buffer.Add(message);
            }
        }
    }

    /// <summary>Registers a log exporter (disposed on fiber unload).</summary>
    public IDisposable Exporter(ILogExporter exporter)
    {
        return _ctx.Fiber.Effect(() =>
        {
            var id = ++_exporterSn;
            _exporters[id] = exporter;
            return Disposer.From(() => _exporters.Remove(id));
        }, "ctx.logger.exporter()");
    }

    internal void Log(string name, LogLevel level, object? format, object?[] args)
    {
        var message = new LogMessage
        {
            Sn = ++_sn,
            Ts = DateTimeOffset.Now,
            Name = name,
            Level = level,
            Args = PrependFormat(format, args),
            Fiber = _ctx.Fiber,
        };
        foreach (var exporter in _exporters.Values)
        {
            exporter.Export(message);
        }
    }

    private static object?[] PrependFormat(object? format, object?[] args)
    {
        while (true)
        {
            if (format is Exception error)
            {
                if (error.InnerException is not null)
                {
                    format = error.InnerException;
                    continue;
                }

                return new object?[] { "%s", error.StackTrace ?? error.Message }.Concat(args).ToArray();
            }

            return new[] { format }.Concat(args).ToArray();
        }
    }

    /// <summary>Gets a named logger (mirrors the callable cordis logger service).</summary>
    public Logger Get(string? name = null)
    {
        name ??= _ctx.Name;
        return new Logger(this, name, null);
    }

    /// <summary>Removes buffered log messages that reference the given fiber (used by the
    /// assembly loader before unloading a plugin assembly, so the fiber can be collected).</summary>
    internal void DropFiberLogs(Fiber fiber) => Buffer.RemoveAll(m => ReferenceEquals(m.Fiber, fiber));

    public void Error(object? format, params object?[] args) => Log(_ctx.Name, LogLevel.Error, format, args);
    public void Warn(object? format, params object?[] args) => Log(_ctx.Name, LogLevel.Warn, format, args);
    public void Info(object? format, params object?[] args) => Log(_ctx.Name, LogLevel.Info, format, args);
    public void Debug(object? format, params object?[] args) => Log(_ctx.Name, LogLevel.Debug, format, args);

    /// <summary>Formats a message using cordis-style placeholders (%s %d %f %o %% etc.).</summary>
    public static string Format(LogMessage message)
    {
        var args = message.Args.ToList();
        var format = args.Count > 0 ? Convert.ToString(args[0]) ?? "" : "";
        if (args.Count > 0) args.RemoveAt(0);
        var result = ArgumentRegex().Replace(format, match =>
        {
            if (match.Value == "%%") return "%";
            var spec = match.Groups[1].Value;
            if (args.Count == 0) return match.Value;
            var value = args[0];
            args.RemoveAt(0);
            return spec switch
            {
                "s" => Convert.ToString(value) ?? "",
                "d" or "i" => Convert.ToInt64(value).ToString(),
                "f" => Convert.ToDouble(value).ToString(CultureInfo.InvariantCulture),
                "o" or "O" => JsonSerializer.Serialize(value),
                "c" or "C" => "",
                _ => match.Value,
            };
        });
        foreach (var arg in args)
        {
            result += " " + arg switch
            {
                null => "null",
                string s => s,
                _ => JsonSerializer.Serialize(arg)
            };
        }
        return result;
    }

    [GeneratedRegex("%([a-zA-Z%])")]
    private static partial Regex ArgumentRegex();
}
