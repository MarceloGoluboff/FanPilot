using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;

namespace FanControl.Ipc;

/// <summary>
/// Service side of the pipe. Each client gets its own bounded send queue so a stalled window can
/// never block the fan engine's thread.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    const int MaxClients = 8;

    readonly Func<PipeMessage> _greeting;
    readonly Action<PipeMessage> _received;
    readonly CancellationTokenSource _cts = new();
    readonly List<Channel<string>> _clients = [];

    public PipeServer(Func<PipeMessage> greeting, Action<PipeMessage> received)
    {
        _greeting = greeting;
        _received = received;
    }

    public void Start() => Task.Run(AcceptLoop);

    public void Broadcast(PipeMessage message)
    {
        string line = Protocol.Serialize(message);
        lock (_clients)
            foreach (var queue in _clients) queue.Writer.TryWrite(line);
    }

    static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Whoever is signed in at the console can read and change fan settings, nobody else.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>Never exits until disposed: if it died, no window could reach the fans again.</summary>
    async Task AcceptLoop()
    {
        var security = CreateSecurity();
        bool first = true;
        string? lastError = null;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = NamedPipeServerStreamAcl.Create(Protocol.PipeName, PipeDirection.InOut, MaxClients,
                    PipeTransmissionMode.Byte, options, 0, 0, security);
                first = false;
                await pipe.WaitForConnectionAsync(_cts.Token);
                var connected = pipe;
                pipe = null;
                lastError = null;
                _ = Task.Run(() => Serve(connected));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // All instances busy, a client that hung up before we accepted, or someone else
                // holding the name (FirstPipeInstance refuses to share it).
                if (ex.Message != lastError) Log.Write($"Pipe accept failed: {ex.Message}");
                lastError = ex.Message;
                try { await Task.Delay(1000, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (pipe is not null) await pipe.DisposeAsync();
            }
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        // Join the broadcast list before building the greeting, so a status change in between
        // isn't missed. A broadcast may then arrive before the greeting; the window handles either order.
        lock (_clients) _clients.Add(queue);
        queue.Writer.TryWrite(Protocol.Serialize(_greeting()));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };

            var sending = Task.Run(async () =>
            {
                await foreach (var line in queue.Reader.ReadAllAsync(linked.Token))
                    await writer.WriteLineAsync(line.AsMemory(), linked.Token);
            });

            while (!linked.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(linked.Token);
                if (line is null) break;
                if (Protocol.Deserialize(line) is not { } message) continue;
                try { _received(message); }
                catch (Exception ex) { Log.Write($"Bad message from window: {ex.Message}"); }
            }

            linked.Cancel();
            await sending.ContinueWith(_ => { });
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            linked.Cancel();
            lock (_clients) _clients.Remove(queue);
            await pipe.DisposeAsync();
        }
    }

    public void Dispose() => _cts.Cancel();
}
