using CordiSharp.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using CLog = CordiSharp.Logger;

namespace CordiSharp.Tests;

public class LoggingTests
{
    [Fact]
    public void AddCordiSharpLogging_ForwardsMelEntriesIntoRootContext()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddCordiSharpLogging());
        using var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<Context>();
        var logger = provider.GetRequiredService<ILogger<LoggingTests>>();
        logger.LogInformation("hello {Name}", "world");

        var message = Assert.Single(ctx.LoggerService.Buffer);
        Assert.Equal(CLog.LogLevel.Info, message.Level);
        Assert.Equal("CordiSharp.Tests.LoggingTests", message.Name);
        Assert.Equal("hello world", CLog.LoggerService.Format(message));
    }

    [Fact]
    public void AddCordiSharpLogging_MapsMelLevelsToCordiSharpLevels()
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddCordiSharpLogging();
            b.SetMinimumLevel(LogLevel.Trace);
        });
        using var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<LoggingTests>>();
        logger.LogTrace("trace");
        logger.LogDebug("debug");
        logger.LogWarning("warn");
        logger.LogError("error");
        logger.LogCritical("critical");

        var ctx = provider.GetRequiredService<Context>();
        var levels = ctx.LoggerService.Buffer.Select(m => m.Level).ToList();
        Assert.Equal(new[]
        {
            CLog.LogLevel.Debug, // Trace -> Debug
            CLog.LogLevel.Debug,
            CLog.LogLevel.Warn,
            CLog.LogLevel.Error,
            CLog.LogLevel.Error, // Critical -> Error
        }, levels);
    }

    [Fact]
    public void AddCordiSharpLogging_RegistersProviderOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddCordiSharpLogging();
            b.AddCordiSharpLogging();
        });
        services.AddCordiSharpLogging();
        using var provider = services.BuildServiceProvider();

        var providers = provider.GetServices<ILoggerProvider>().OfType<CordiSharpLoggerProvider>();
        Assert.Single(providers);
    }

    [Fact]
    public void Exporter_ForwardsCordiSharpMessagesIntoMel()
    {
        var root = Context.Create();
        var capture = new CaptureLogger();
        using (root.LoggerService.Exporter(new CordiSharpLogExporter(capture)))
        {
            root.Logger().Info("hello %s", "world");
            root.Logger("chat").Warn("n = %d", 42);
        }

        Assert.Equal(2, capture.Entries.Count);

        var first = capture.Entries[0];
        Assert.Equal(LogLevel.Information, first.Level);
        Assert.Equal("[root] hello world", first.Message);

        var second = capture.Entries[1];
        Assert.Equal(LogLevel.Warning, second.Level);
        Assert.Equal("[chat] n = 42", second.Message);
    }

    [Fact]
    public void UseLoggerFactory_BridgesRootContextLogs()
    {
        var root = Context.Create();
        var captureProvider = new CaptureLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(captureProvider));

        using var handle = root.UseLoggerFactory(factory);
        root.Logger("chat").Error(new InvalidOperationException("boom"));

        var entry = Assert.Single(captureProvider.Logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.StartsWith("[chat] ", entry.Message);
    }

    [Fact]
    public void CombinedBridge_CordiSharpMessagesAreNotEchoed()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddCordiSharpLogging());
        using var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<Context>();
        using var bridge = ctx.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());

        ctx.Logger().Info("from cordisharp");

        Assert.Single(ctx.LoggerService.Buffer);
    }

    [Fact]
    public void CombinedBridge_MelEntriesAreNotEchoedBack()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddCordiSharpLogging());
        using var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<Context>();
        using var bridge = ctx.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());

        provider.GetRequiredService<ILogger<LoggingTests>>().LogInformation("hello");

        Assert.Single(ctx.LoggerService.Buffer);
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public CaptureLogger Logger { get; } = new();

        public ILogger CreateLogger(string categoryName) => Logger;

        public void Dispose()
        {
        }
    }
}
