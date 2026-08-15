namespace CordiSharp.Extensions.Logging;

/// <summary>Reentrancy guard shared by <see cref="CordiSharpLoggerProvider"/> and
/// <see cref="CordiSharpLogExporter"/> so that a CordiSharp message re-exported into
/// Microsoft.Extensions.Logging is not echoed back into the same CordiSharp
/// <c>LoggerService</c> (which would loop forever).</summary>
internal static class CordiSharpLogBridge
{
    private static readonly AsyncLocal<bool> _echoing = new();

    /// <summary>Whether the current call stack is inside a CordiSharp → MEL re-export.</summary>
    public static bool IsEchoing => _echoing.Value;

    /// <summary>Marks the current async flow as re-exporting; restore on dispose.</summary>
    public static IDisposable EnterEcho()
    {
        var previous = _echoing.Value;
        _echoing.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _echoing.Value = previous;
        }
    }
}
