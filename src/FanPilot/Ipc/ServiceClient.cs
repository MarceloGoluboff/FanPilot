using System.IO.Pipes;

namespace FanPilot.Ipc;

/// <summary>Window side of the pipe. Reconnects every 2 s while the service is unreachable.</summary>
public sealed class ServiceClient : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly object _writeGate = new();
    StreamWriter? _writer;

    /// <summary>Raised on a background thread.</summary>
    public event Action<PipeMessage>? MessageReceived;

    /// <summary>Raised on a background thread with the new connection state.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start() => Task.Run(Loop);

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, _cts.Token);

                using var reader = new StreamReader(pipe);
                lock (_writeGate) _writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
                ConnectionChanged?.Invoke(true);

                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (Protocol.Deserialize(line) is { } message) MessageReceived?.Invoke(message);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
            finally
            {
                bool wasConnected;
                lock (_writeGate)
                {
                    wasConnected = _writer is not null;
                    _writer = null;
                }
                if (wasConnected) ConnectionChanged?.Invoke(false);
            }

            try { await Task.Delay(2000, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public bool Send(PipeMessage message)
    {
        lock (_writeGate)
        {
            if (_writer is null) return false;
            try
            {
                _writer.WriteLine(Protocol.Serialize(message));
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
