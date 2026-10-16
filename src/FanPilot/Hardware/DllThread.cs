using System.Collections.Concurrent;

namespace FanPilot.Hardware;

/// <summary>
/// Runs every call on one dedicated thread. ASUS's AsusWinIO64.dll is thread-affine: calls from a
/// thread other than the one that has been using it stalled for about 20 seconds, and fan duty
/// writes from such threads were silently ignored. Fan setup, which awaits between steps and so
/// hops between pool threads, ran into both.
/// </summary>
internal sealed class DllThread : IDisposable
{
    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;

    public DllThread(string name)
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        })
        {
            IsBackground = true,
            Name = name,
        };
        _thread.Start();
    }

    public T Invoke<T>(Func<T> func)
    {
        if (Thread.CurrentThread == _thread) return func();

        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { result.SetResult(func()); }
                catch (Exception ex) { result.SetException(ex); }
            });
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(DllThread));
        }
        return result.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action) => Invoke(() =>
    {
        action();
        return 0;
    });

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
