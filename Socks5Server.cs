using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BlueSend;

internal sealed class Socks5Server : IDisposable
{
    private readonly BluetoothManager _bt;
    private readonly int _port;
    private readonly int _maxConnections;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly ConcurrentDictionary<ushort, SocksRelayState> _connections = new();
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<bool>> _pending = new();
    private ushort _nextConnId;
    private readonly object _idLock = new();
    private bool _started;
    private readonly object _stopLock = new();

    public int Port => _port;
    public int ActiveConnections => _connections.Count;
    public int MaxConnections => _maxConnections;
    public bool IsRunning => _started;

    public event EventHandler<bool>? RunningChanged;

    private sealed class SocksRelayState
    {
        public ushort ConnId;
        public TcpClient? SocksTcp;
        public NetworkStream? SocksStream;
        public CancellationTokenSource? Cts;

        public void Close()
        {
            Cts?.Cancel();
            try { SocksStream?.Close(); } catch { }
            try { SocksTcp?.Close(); } catch { }
        }
    }

    public Socks5Server(BluetoothManager bt, int port = 1080, int maxConnections = 10)
    {
        _bt = bt;
        _port = port;
        _maxConnections = maxConnections;
    }

    public void Start()
    {
        lock (_stopLock)
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _bt.ProxyStatusReceived += OnProxyStatus;
            _bt.ProxyDataReceived += OnProxyData;
            _bt.ProxyCloseReceived += OnProxyClose;
            _bt.Disconnected += OnDisconnected;
            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
            RunningChanged?.Invoke(this, true);
        }
    }

    public void Stop()
    {
        lock (_stopLock)
        {
            if (!_started) return;
            _started = false;
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _bt.ProxyStatusReceived -= OnProxyStatus;
            _bt.ProxyDataReceived -= OnProxyData;
            _bt.ProxyCloseReceived -= OnProxyClose;
            _bt.Disconnected -= OnDisconnected;
            foreach (var kv in _connections)
                kv.Value.Close();
            _connections.Clear();
            foreach (var kv in _pending)
                kv.Value.TrySetResult(false);
            _pending.Clear();
            _acceptTask = null;
            RunningChanged?.Invoke(this, false);
        }
    }

    private void AcceptLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var tcp = _listener!.AcceptTcpClient();
                if (token.IsCancellationRequested)
                {
                    tcp.Close();
                    break;
                }
                _ = HandleSocksConnectionAsync(tcp, token);
            }
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SOCKS accept error: {ex.Message}");
        }
    }

    private async Task HandleSocksConnectionAsync(TcpClient tcp, CancellationToken token)
    {
        NetworkStream? stream = null;
        try
        {
            stream = tcp.GetStream();

            // SOCKS5 handshake
            var handshake = await ReadExactAsync(stream, 2, token);
            if (handshake == null || handshake[0] != 5) { tcp.Close(); return; }
            var nMethods = handshake[1];
            var methods = await ReadExactAsync(stream, nMethods, token);
            if (methods == null) { tcp.Close(); return; }
            // Respond NO AUTH (0x00)
            await stream.WriteAsync([5, 0], 0, 2, token);

            // Read request
            var reqHeader = await ReadExactAsync(stream, 4, token);
            if (reqHeader == null || reqHeader[0] != 5 || reqHeader[1] != 1)
            {
                await SendSocksReply(stream, 0x07, token);
                tcp.Close();
                return;
            }

            string host;
            switch (reqHeader[3])
            {
                case 1: // IPv4
                {
                    var addr = await ReadExactAsync(stream, 4, token);
                    if (addr == null) { tcp.Close(); return; }
                    host = new IPAddress(addr).ToString();
                    break;
                }
                case 3: // Domain name
                {
                    var lenByte = await ReadExactAsync(stream, 1, token);
                    if (lenByte == null) { tcp.Close(); return; }
                    var domain = await ReadExactAsync(stream, lenByte[0], token);
                    if (domain == null) { tcp.Close(); return; }
                    host = System.Text.Encoding.UTF8.GetString(domain);
                    break;
                }
                case 4: // IPv6
                {
                    var addr = await ReadExactAsync(stream, 16, token);
                    if (addr == null) { tcp.Close(); return; }
                    host = new IPAddress(addr).ToString();
                    break;
                }
                default:
                    await SendSocksReply(stream, 0x08, token);
                    tcp.Close();
                    return;
            }

            var portBytes = await ReadExactAsync(stream, 2, token);
            if (portBytes == null) { tcp.Close(); return; }
            var port = (ushort)((portBytes[0] << 8) | portBytes[1]);

            if (_connections.Count >= _maxConnections)
            {
                await SendSocksReply(stream, 0x05, token);
                tcp.Close();
                return;
            }

            ushort connId;
            lock (_idLock) { connId = _nextConnId++; }

            var tcs = new TaskCompletionSource<bool>();
            _pending.TryAdd(connId, tcs);

            _bt.SendProxyConnect(connId, host, port);

            // Wait for ProxyStatus with timeout
            var success = false;
            try
            {
                success = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }

            _pending.TryRemove(connId, out _);

            if (!success)
            {
                await SendSocksReply(stream, 0x04, token);
                tcp.Close();
                return;
            }

            // SOCKS5 success reply
            await SendSocksReply(stream, 0x00, token);

            var state = new SocksRelayState
            {
                ConnId = connId,
                SocksTcp = tcp,
                SocksStream = stream,
                Cts = new CancellationTokenSource()
            };

            if (!_connections.TryAdd(connId, state))
            {
                state.Close();
                return;
            }

            var relayToken = state.Cts.Token;
            _ = Task.Run(() => SocksToBtLoop(state, relayToken), relayToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SOCKS handler error: {ex.Message}");
            if (stream != null)
            {
                try { await SendSocksReply(stream, 0x01, CancellationToken.None); } catch { }
            }
            tcp.Close();
        }
    }

    private async Task SocksToBtLoop(SocksRelayState state, CancellationToken token)
    {
        try
        {
            var buffer = new byte[BluetoothManager.MaxProxyPacketSize];
            while (!token.IsCancellationRequested && state.SocksTcp!.Connected)
            {
                var read = await state.SocksStream!.ReadAsync(buffer, 0, buffer.Length, token);
                if (read == 0) break;

                var data = new byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                _bt.SendProxyData(state.ConnId, data);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (_connections.TryRemove(state.ConnId, out var removed))
            {
                removed.Close();
                _bt.SendProxyClose(state.ConnId);
            }
        }
    }

    private void OnProxyStatus(object? sender, (ushort connId, bool success) e)
    {
        if (_pending.TryRemove(e.connId, out var tcs))
            tcs.TrySetResult(e.success);
    }

    private void OnProxyData(object? sender, (ushort connId, byte[] data) e)
    {
        if (!_started) return;
        if (_connections.TryGetValue(e.connId, out var state))
        {
            try
            {
                state.SocksStream?.Write(e.data, 0, e.data.Length);
                state.SocksStream?.Flush();
            }
            catch
            {
                if (_connections.TryRemove(e.connId, out var removed))
                    removed.Close();
            }
        }
    }

    private void OnProxyClose(object? sender, ushort connId)
    {
        if (_connections.TryRemove(connId, out var state))
            state.Close();
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        Stop();
    }

    private static async Task SendSocksReply(NetworkStream stream, byte rep, CancellationToken token)
    {
        var reply = new byte[] { 5, rep, 0, 1, 0, 0, 0, 0, 0, 0 };
        await stream.WriteAsync(reply, 0, reply.Length, token);
        await stream.FlushAsync(token);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, offset, count - offset, token);
            if (read == 0) return null;
            offset += read;
        }
        return buffer;
    }

    public void Dispose()
    {
        Stop();
    }
}
