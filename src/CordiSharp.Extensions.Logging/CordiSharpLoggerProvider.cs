using Microsoft.Extensions.Logging;
using CordiSharpLoggerService = CordiSharp.Logger.LoggerService;

namespace CordiSharp.Extensions.Logging;

/// <summary>An <see cref="ILoggerProvider"/> that writes Microsoft.Extensions.Logging
/// entries into a CordiSharp <see cref="CordiSharp.Logger.LoggerService"/> (the MEL category
/// becomes the CordiSharp logger name).</summary>
public sealed class CordiSharpLoggerProvider : ILoggerProvider
{
    private readonly CordiSharpLoggerService _service;

    public CordiSharpLoggerProvider(CordiSharpLoggerService service)
    {
        _service = service;
    }

    public ILogger CreateLogger(string categoryName) => new CordiSharpLogger(_service, categoryName);

    public void Dispose()
    {
    }

    private sealed class CordiSharpLogger(CordiSharpLoggerService service, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None || CordiSharpLogBridge.IsEchoing) return;

            var text = formatter(state, exception);
            var logger = service.Get(category);
            switch (logLevel)
            {
                case LogLevel.Critical:
                case LogLevel.Error:
                    logger.Error(text);
                    break;
                case LogLevel.Warning:
                    logger.Warn(text);
                    break;
                case LogLevel.Information:
                    logger.Info(text);
                    break;
                default:
                    logger.Debug(text);
                    break;
            }
        }
    }
}
