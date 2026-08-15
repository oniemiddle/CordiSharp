namespace CordiSharp;

/// <summary>Lifecycle state of a <see cref="Fiber"/>.</summary>
public enum FiberState
{
    /// <summary>Waiting for injected services (or not yet loaded).</summary>
    Pending,
    /// <summary>Loading (plugin callback / init is running).</summary>
    Loading,
    /// <summary>Fully loaded and running.</summary>
    Active,
    /// <summary>Loading failed; the error is recorded on the fiber.</summary>
    Failed,
    /// <summary>The fiber has been disposed (uid cleared).</summary>
    Disposed,
    /// <summary>Unloading (disposers are being run).</summary>
    Unloading,
}
