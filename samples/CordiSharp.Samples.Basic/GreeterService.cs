using CordiSharp.Registry;

namespace CordiSharp.Samples.Basic;

[Service("greeter")]
public sealed class GreeterService(Context ctx) : Service(ctx)
{
    public string Greet(string name) => $"Hello, {name}!";
}