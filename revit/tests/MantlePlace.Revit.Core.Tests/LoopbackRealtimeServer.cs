using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// An in-process WebSocket server on the loopback, standing in for Realtime so the client is driven
/// over a real socket with nothing leaving the machine (<c>HPS-42</c>).
/// </summary>
/// <remarks>
/// A raw <see cref="TcpListener"/> and the RFC 6455 upgrade by hand, then
/// <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>: no http.sys URL
/// reservation on Windows, no package, and the same code on the Linux runner.
/// </remarks>
internal sealed class LoopbackRealtimeServer : IDisposable
{
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly Channel<ServerConnection> _accepted = Channel.CreateUnbounded<ServerConnection>();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<ServerConnection> _all = [];

    internal LoopbackRealtimeServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    internal int Port { get; }

    /// <summary>The Realtime location a client is configured with.</summary>
    internal string Location => $"ws://127.0.0.1:{Port}/realtime/v1";

    /// <summary>How many sockets have been opened against this server.</summary>
    internal int Connections
    {
        get
        {
            lock (_all)
            {
                return _all.Count;
            }
        }
    }

    /// <summary>The next socket a client opens, or <c>null</c> when none arrives in time.</summary>
    internal async Task<ServerConnection?> AcceptAsync(TimeSpan timeout)
    {
        using CancellationTokenSource wait = new(timeout);
        try
        {
            return await _accepted.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_all)
        {
            foreach (ServerConnection connection in _all)
            {
                connection.Drop();
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                ServerConnection connection = await UpgradeAsync(client).ConfigureAwait(false);
                lock (_all)
                {
                    _all.Add(connection);
                }

                await _accepted.Writer.WriteAsync(connection).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
            {
                client.Dispose();
            }
        }
    }

    private static async Task<ServerConnection> UpgradeAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        StringBuilder head = new();
        byte[] one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one).ConfigureAwait(false) == 0)
            {
                throw new IOException("closed before the upgrade");
            }

            head.Append((char)one[0]);
        }

        string[] lines = head.ToString().Split("\r\n");
        string target = lines[0].Split(' ')[1];
        string key = lines
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1].Trim())
            .FirstOrDefault() ?? throw new InvalidOperationException("no Sec-WebSocket-Key");

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response).ConfigureAwait(false);

        WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.Zero,
        });
        return new ServerConnection(client, socket, target);
    }
}

/// <summary>One socket a client opened: what it sent, in order, and a way to answer or drop it.</summary>
internal sealed class ServerConnection
{
    private readonly TcpClient _client;
    private readonly WebSocket _socket;
    private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _send = new(1, 1);

    internal ServerConnection(TcpClient client, WebSocket socket, string requestTarget)
    {
        _client = client;
        _socket = socket;
        RequestTarget = requestTarget;
        _ = ReceiveLoopAsync();
    }

    /// <summary>The path and query the client asked for.</summary>
    internal string RequestTarget { get; }

    /// <summary>
    /// The next frame the client sent that is not a heartbeat (answered here, as Realtime would), or
    /// <c>null</c> when the socket closed or nothing came in time.
    /// </summary>
    internal async Task<RealtimeMessage?> NextAsync(TimeSpan timeout)
    {
        using CancellationTokenSource wait = new(timeout);
        while (true)
        {
            string frame;
            try
            {
                frame = await _frames.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
            {
                return null;
            }

            RealtimeMessage message = RealtimeFrames.Decode(frame);
            if (message.Event == "heartbeat")
            {
                await SendAsync($$"""{"topic":"phoenix","event":"phx_reply","ref":"{{message.Ref}}","payload":{"status":"ok","response":{ } } }""").ConfigureAwait(false);
                continue;
            }

            return message;
        }
    }

    /// <summary>Answers a push with <c>ok</c>, by its ref.</summary>
    internal Task ReplyOkAsync(RealtimeMessage push)
        => SendAsync($$"""{"topic":"{{push.Topic}}","event":"phx_reply","ref":"{{push.Ref}}","join_ref":"{{push.JoinRef}}","payload":{"status":"ok","response":{ } } }""");

    /// <summary>Answers a push with <c>error</c> and a reason, by its ref.</summary>
    internal Task ReplyErrorAsync(RealtimeMessage push, string reason)
        => SendAsync($$"""{"topic":"{{push.Topic}}","event":"phx_reply","ref":"{{push.Ref}}","join_ref":"{{push.JoinRef}}","payload":{"status":"error","response":{"reason":"{{reason}}"} } }""");

    internal async Task SendAsync(string frame)
    {
        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>Drops the TCP connection under the socket, with no close handshake: a network loss.</summary>
    internal void Drop()
    {
        // A dropped connection has no socket left; dropping it again (the server's dispose) is a no-op.
        if (_client.Client is { } socket)
        {
            try
            {
                socket.LingerState = new LingerOption(true, 0);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
            }
        }

        _client.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    await _frames.Writer.WriteAsync(Encoding.UTF8.GetString(message.ToArray())).ConfigureAwait(false);
                    message.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }
}
