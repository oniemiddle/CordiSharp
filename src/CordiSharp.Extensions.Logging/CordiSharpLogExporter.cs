using CordiSharp.Logger;
using MLog = Microsoft.Extensions.Logging;

namespace CordiSharp.Extensions.Logging;

/// <summary>An <see cref="ILogExporter"/> that forwards CordiSharp log messages into a
/// Microsoft.Extensions.Logging <see cref="Microsoft.Extensions.Logging.ILogger"/>. Attach it to a
/// <see cref="LoggerService"/> via <c>Exporter(...)</c> or use the
/// <c>Context.UseLoggerFactory(...)</c> extension.</summary>
public sealed class CordiSharpLogExporter(MLog.ILogger logger) : ILogExporter
{
    private const string DefaultCategory = "CordiSharp";

    /// <summary>Creates an exporter that forwards messages to a logger created from
    /// <paramref name="factory"/> with the given category (default <c>CordiSharp</c>).
    /// The CordiSharp logger name is still included in the formatted text.</summary>
    public CordiSharpLogExporter(MLog.ILoggerFactory factory, string? categoryName = null)
        : this(factory.CreateLogger(categoryName ?? DefaultCategory))
    {
    }

    public void Export(LogMessage message)
    {
        var prefix = string.IsNullOrEmpty(message.Name) ? "" : $"[{message.Name}] ";
        var text = prefix + LoggerService.Format(message);
        using (CordiSharpLogBridge.EnterEcho())
        {
            logger.Log(ToMsLevel(message.Level), new MLog.EventId((int)message.Sn), text, null, static (s, _) => s);
        }

        return;

        static MLog.LogLevel ToMsLevel(LogLevel level) =>
            level switch
            {
                LogLevel.Error => MLog.LogLevel.Error,
                LogLevel.Warn => MLog.LogLevel.Warning,
                LogLevel.Info => MLog.LogLevel.Information,
                _ => MLog.LogLevel.Debug,
            };
    }
}
