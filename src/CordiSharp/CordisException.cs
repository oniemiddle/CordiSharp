namespace CordiSharp;

/// <summary>Base exception thrown by the CordiSharp framework.</summary>
public class CordisException : Exception
{
    public CordisException(string message) : base(message) { }
    public CordisException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when an operation is attempted on an inactive (disposed) fiber/context.</summary>
public sealed class InactiveEffectException() : CordisException("cannot create effect on inactive context");

/// <summary>Thrown when a required service cannot be resolved.</summary>
public sealed class ServiceResolutionException(string message) : CordisException(message);

/// <summary>Thrown when an invalid plugin is supplied to <c>Plugin()</c>.</summary>
public sealed class InvalidPluginException(string message) : CordisException(message);
