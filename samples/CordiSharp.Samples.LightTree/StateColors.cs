using Avalonia.Media;

namespace CordiSharp.Samples.LightTree;

/// <summary>Maps <see cref="FiberState"/> to display colors and labels.
/// Active=绿 / Pending=黄 / Failed=红 / Disposed=灰；Loading/Unloading 为过渡色。</summary>
public static class StateColors
{
    public static readonly IBrush Active = new SolidColorBrush(Color.Parse("#2E7D32"));
    public static readonly IBrush Pending = new SolidColorBrush(Color.Parse("#F9A825"));
    public static readonly IBrush Loading = new SolidColorBrush(Color.Parse("#8BC34A"));
    public static readonly IBrush Unloading = new SolidColorBrush(Color.Parse("#FBC02D"));
    public static readonly IBrush Failed = new SolidColorBrush(Color.Parse("#C62828"));
    public static readonly IBrush Disposed = new SolidColorBrush(Color.Parse("#9E9E9E"));

    public static IBrush For(FiberState state) => state switch
    {
        FiberState.Active => Active,
        FiberState.Pending => Pending,
        FiberState.Loading => Loading,
        FiberState.Unloading => Unloading,
        FiberState.Failed => Failed,
        _ => Disposed,
    };

    public static string Text(FiberState state) => state switch
    {
        FiberState.Active => "Active",
        FiberState.Pending => "Pending",
        FiberState.Loading => "Loading",
        FiberState.Unloading => "Unloading",
        FiberState.Failed => "Failed",
        _ => "Disposed",
    };
}
