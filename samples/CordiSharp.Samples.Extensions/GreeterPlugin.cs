using CordiSharp.Registry;

namespace CordiSharp.Samples.Extensions;

[Plugin("greeter")]
public sealed class GreeterPlugin : IPlugin<GreeterConfig>
{
    public void Load(Context ctx, GreeterConfig config)
    {
        // emit a few ticks so the notifier plugin reacts
        ctx.Effect(() =>
        {
            var timer = new Timer(_ => ctx.Emit(TestEvents.Tick, Random.Shared.Next(100)), null, 0, 1000);
            ctx.Logger().Info("greeter plugin loaded (name = %s)", config.Name);
            return () =>
            {
                timer.Dispose();
                ctx.Logger().Info("greeter plugin disposed");
            };
        }, "ticker");
    }
}