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

    async Task AcceptLoop()
    {
        var security = CreateSecurity();
        bool first = true;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = NamedPipeServerStreamAcl.Create(Protocol.PipeName, PipeDirection.InOut, MaxClients,
                    PipeTransmissionMode.Byte, options, 0, 0, security);
                first = false;
            }
            catch (IOException)
            {
                // All instances busy, or a stale instance from a previous run.
                await Task.Delay(1000, _cts.Token).ContinueWith(_ => { });
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        queue.Writer.TryWrite(Protocol.Serialize(_greeting()));
        lock (_clients) _clients.Add(queue);

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
                if (Protocol.Deserialize(line) is { } message) _received(message);
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
