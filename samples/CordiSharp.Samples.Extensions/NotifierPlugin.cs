using CordiSharp.Registry;

namespace CordiSharp.Samples.Extensions;

[Plugin("notifier")]
public sealed class NotifierPlugin(INotifier notifier) : IPlugin<NotifierConfig>
{
    // resolved from MSDI

    public void Load(Context ctx, NotifierConfig config)
    {
        ctx.On(TestEvents.Tick, (_, tick) =>
        {
            notifier.Notify($"{config.Prefix} tick {tick}");
            return null;
        });
        ctx.Logger().Info("notifier plugin loaded (waits for ticks)");
    }
}