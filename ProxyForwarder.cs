using System.Collections.Concurrent;
using System.Net.Sockets;

namespace BlueSend;

internal sealed class ProxyForwarder : IDisposable
{
    private readonly BluetoothManager _bt;
    private readonly int _maxConnections;
    private readonly ConcurrentDictionary<ushort, TcpRelayState> _connections = new();
    private bool _started;
    private readonly object _stopLock = new();

    public int ActiveConnections => _connections.Count;
    public int MaxConnections => _maxConnections;
    public bool IsRunning => _started;

    private sealed class TcpRelayState
    {
        public ushort ConnId;
        public TcpClient? Tcp;
        public NetworkStream? Stream;
        public CancellationTokenSource? Cts;

        public void Close()
        {
            Cts?.Cancel();
            try { Stream?.Close(); } catch { }
            try { Tcp?.Close(); } catch { }
        }
    }

    public ProxyForwarder(BluetoothManager bt, int maxConnections = 10)
    {
        _bt = bt;
        _maxConnections = maxConnections;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _bt.ProxyConnectReceived += OnProxyConnect;
        _bt.ProxyDataReceived += OnProxyData;
        _bt.ProxyCloseReceived += OnProxyClose;
        _bt.Disconnected += OnDisconnected;
    }

    public void Stop()
    {
        lock (_stopLock)
        {
            if (!_started) return;
            _started = false;
            _bt.ProxyConnectReceived -= OnProxyConnect;
            _bt.ProxyDataReceived -= OnProxyData;
            _bt.ProxyCloseReceived -= OnProxyClose;
            _bt.Disconnected -= OnDisconnected;
            foreach (var kv in _connections)
                kv.Value.Close();
            _connections.Clear();
        }
    }

    private void OnProxyConnect(object? sender, (ushort connId, string host, ushort port) e)
    {
        if (!_started) return;

        if (_connections.Count >= _maxConnections)
        {
            _bt.SendProxyStatus(e.connId, false);
            return;
        }

        _ = ConnectAndRelayAsync(e.connId, e.host, e.port);
    }

    private async Task ConnectAndRelayAsync(ushort connId, string host, ushort port)
    {
        TcpClient? tcp = null;
        try
        {
            tcp = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, timeoutCts.Token);

            var state = new TcpRelayState
            {
                ConnId = connId,
                Tcp = tcp,
                Stream = tcp.GetStream(),
                Cts = new CancellationTokenSource()
            };

            if (!_connections.TryAdd(connId, state))
            {
                tcp.Close();
                _bt.SendProxyStatus(connId, false);
                return;
            }

            _bt.SendProxyStatus(connId, true);

            var token = state.Cts.Token;
            _ = Task.Run(() => TcpToBtLoop(state, token), token);
        }
        catch
        {
            tcp?.Close();
            _bt.SendProxyStatus(connId, false);
        }
    }

    private void TcpToBtLoop(TcpRelayState state, CancellationToken token)
    {
        try
        {
            var buffer = new byte[BluetoothManager.MaxProxyPacketSize];
            while (!token.IsCancellationRequested && state.Tcp!.Connected)
            {
                var read = state.Stream!.Read(buffer, 0, buffer.Length);
                if (read == 0) break;

                var data = new byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                _bt.SendProxyData(state.ConnId, data);
            }
        }
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

    private void OnProxyData(object? sender, (ushort connId, byte[] data) e)
    {
        if (!_started) return;
        if (_connections.TryGetValue(e.connId, out var state))
        {
            try
            {
                state.Stream?.Write(e.data, 0, e.data.Length);
                state.Stream?.Flush();
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

    public void Dispose()
    {
        Stop();
    }
}
