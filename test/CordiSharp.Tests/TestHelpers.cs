using CordiSharp.Events;

namespace CordiSharp.Tests;

public static class TestEvents
{
    public static readonly EventKey<object?> Custom = EventKey.Create<object?>("custom-event");
    public static readonly EventKey<int> Waterfall = EventKey.Create<int>("test/waterfall");
}

public static class TestHelpers
{
    /// <summary>Session-like thisArg that filters hooks by context filter (mirrors cordis tests).</summary>
    public sealed class Session(bool flag) : IContextFilter
    {
        public bool Flag { get; } = flag;

        public bool FilterContext(Context ctx) => ctx.Filter?.Invoke(this) ?? true;
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(10);
        }
    }

    public static IReadOnlyDictionary<string, int> HookSnapshot(Context ctx) => ctx.Events.HookSnapshot();
}
