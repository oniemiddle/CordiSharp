namespace CordiSharp.Samples.Extensions;

public interface INotifier { void Notify(string message); }

public sealed class ConsoleNotifier : INotifier
{
    public void Notify(string message) => Console.WriteLine($"  {message}");
}