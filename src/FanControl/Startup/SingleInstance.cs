namespace FanControl.Startup;

/// <summary>
/// One window per session. A second launch (taskbar pin, Start menu) signals the named event
/// and exits, and the running instance shows its window.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string EventName = @"Local\MarceloGoluboff.FanControl.Show";

    readonly EventWaitHandle _show;
    readonly ManualResetEvent _stop = new(false);

    SingleInstance(EventWaitHandle show) => _show = show;

    /// <summary>Returns null when another instance already owns the event.</summary>
    public static SingleInstance? TryAcquire()
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out bool createdNew);
        if (createdNew) return new SingleInstance(handle);

        handle.Dispose();
        return null;
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public static void SignalExisting()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(EventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    /// <summary>Calls <paramref name="onShow"/> on a background thread each time the event is signaled.</summary>
    public void Listen(Action onShow)
    {
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _stop, _show };
            while (WaitHandle.WaitAny(handles) == 1) onShow();
        })
        {
            IsBackground = true,
            Name = "ShowListener",
        };
        thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _show.Dispose();
    }
}
