namespace CordiSharp.Samples.Contracts;

/// <summary>Shared service contract, declared OUTSIDE the plugin assembly: the service
/// catalog source generator maps it to the plugin's internal service type, and the host can
/// resolve it via <c>set.GetService&lt;IGreeter&gt;()</c> without pinning the plugin.</summary>
public interface IGreeter
{
    string Greet(string name);
}
