using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace BlueSend;

public enum PacketType : byte
{
    Text = 0,
    FileStart = 1,
    FileChunk = 2,
    FileEnd = 3,
    ProxyConnect = 4,
    ProxyData = 5,
    ProxyClose = 6,
    ProxyStatus = 7,
}

public class BluetoothManager : IDisposable
{
    private static readonly Guid ChatServiceUuid = new("d4a8c5e0-9c4a-4f4a-9c8a-1a2b3c4d5e6f");
    private const int ChunkSize = 32768;
    internal const int MaxProxyPacketSize = 32768;

    private BluetoothListener? _listener;
    private BluetoothClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    private sealed class FileReceiveState
    {
        public string FileName = "";
        public string SavePath = "";
        public long TotalSize;
        public long ReceivedSize;
        public FileStream? Stream;
    }
    private FileReceiveState? _recvFile;
    private readonly object _recvLock = new();
    private readonly object _writeLock = new();

    public bool IsConnected => _stream?.CanRead == true && _stream?.CanWrite == true;
    public string? LocalAddress { get; private set; }

    public event EventHandler<string>? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<(string fileName, long fileSize)>? FileTransferStarted;
    public event EventHandler<(string fileName, int percentage)>? FileTransferProgress;
    public event EventHandler<(string fileName, string savedPath)>? FileTransferCompleted;

    public event EventHandler<(ushort connId, string host, ushort port)>? ProxyConnectReceived;
    public event EventHandler<(ushort connId, byte[] data)>? ProxyDataReceived;
    public event EventHandler<ushort>? ProxyCloseReceived;
    public event EventHandler<(ushort connId, bool success)>? ProxyStatusReceived;

    public string GetLocalAddress()
    {
        try
        {
            var radio = BluetoothRadio.Default;
            if (radio != null && radio.Mode != RadioMode.PowerOff)
                return radio.LocalAddress.ToString("C");
        }
        catch { }
        return "N/A";
    }

    public void StartServer()
    {
        Stop();
        try
        {
            LocalAddress = GetLocalAddress();
            _listener = new BluetoothListener(ChatServiceUuid);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"启动服务端失败: {ex.Message}");
        }
    }

    private void AcceptLoop(CancellationToken token)
    {
        try
        {
            var client = _listener!.AcceptBluetoothClient();
            if (token.IsCancellationRequested) return;

            _client = client;
            _stream = client.GetStream();

            var remoteAddr = GetClientAddress(client);
            Connected?.Invoke(this, remoteAddr);
            ReadLoop(token);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
                ErrorOccurred?.Invoke(this, $"接受连接失败: {ex.Message}");
        }
        finally
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string GetClientAddress(BluetoothClient client)
    {
        try
        {
            var ep = client.Client.RemoteEndPoint;
            if (ep is BluetoothEndPoint btep)
                return btep.Address.ToString("C");
            return ep?.ToString() ?? "对方";
        }
        catch
        {
            return client.RemoteMachineName ?? "对方";
        }
    }

    public void Connect(string address)
    {
        Stop();
        try
        {
            var addr = BluetoothAddress.Parse(address);
            _client = new BluetoothClient();
            _client.Connect(addr, ChatServiceUuid);
            _stream = _client.GetStream();

            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() =>
            {
                Connected?.Invoke(this, address);
                ReadLoop(_cts.Token);
            });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"连接失败: {ex.Message}");
        }
    }

    private void ReadLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var header = ReadFull(5, token);
                if (header == null) break;

                var type = (PacketType)header[0];
                var payloadLen = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (payloadLen < 0 || payloadLen > 1024 * 1024 * 100) break;

                var payload = payloadLen > 0 ? ReadFull(payloadLen, token) : [];
                if (payload == null) break;

                switch (type)
                {
                    case PacketType.Text:
                        MessageReceived?.Invoke(this, Encoding.UTF8.GetString(payload));
                        break;
                    case PacketType.FileStart:
                        HandleFileStart(payload);
                        break;
                    case PacketType.FileChunk:
                        HandleFileChunk(payload);
                        break;
                    case PacketType.FileEnd:
                        HandleFileEnd(payload);
                        break;
                    case PacketType.ProxyConnect:
                        if (payload.Length >= 4)
                        {
                            var connId = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0));
                            var hostLen = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));
                            if (payload.Length >= 4 + hostLen + 2)
                            {
                                var host = Encoding.UTF8.GetString(payload, 4, hostLen);
                                var port = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4 + hostLen));
                                ProxyConnectReceived?.Invoke(this, (connId, host, port));
                            }
                        }
                        break;
                    case PacketType.ProxyData:
                        if (payload.Length >= 6)
                        {
                            var connId = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0));
                            var data = new byte[payload.Length - 2];
                            Buffer.BlockCopy(payload, 2, data, 0, data.Length);
                            ProxyDataReceived?.Invoke(this, (connId, data));
                        }
                        break;
                    case PacketType.ProxyClose:
                        if (payload.Length >= 2)
                        {
                            var connId = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0));
                            ProxyCloseReceived?.Invoke(this, connId);
                        }
                        break;
                    case PacketType.ProxyStatus:
                        if (payload.Length >= 3)
                        {
                            var connId = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0));
                            var success = payload[2] == 0;
                            ProxyStatusReceived?.Invoke(this, (connId, success));
                        }
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
                ErrorOccurred?.Invoke(this, $"接收错误: {ex.Message}");
        }
        finally
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleFileStart(byte[] payload)
    {
        var offset = 0;
        var nameLen = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset)); offset += 4;
        var fileName = Encoding.UTF8.GetString(payload, offset, nameLen); offset += nameLen;
        var fileSize = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(offset));

        var saveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "BlueSend");
        Directory.CreateDirectory(saveDir);
        var savePath = GetUniquePath(saveDir, fileName);

        lock (_recvLock)
        {
            _recvFile = new FileReceiveState
            {
                FileName = fileName,
                SavePath = savePath,
                TotalSize = fileSize,
                ReceivedSize = 0,
                Stream = new FileStream(savePath + ".tmp", FileMode.Create, FileAccess.Write),
            };
        }

        FileTransferStarted?.Invoke(this, (fileName, fileSize));
    }

    private void HandleFileChunk(byte[] payload)
    {
        FileReceiveState? state;
        lock (_recvLock) { state = _recvFile; }
        if (state?.Stream == null) return;

        var seq = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0));
        var dataLen = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4));
        state.Stream.Write(payload, 8, dataLen);
        state.ReceivedSize += dataLen;

        var pct = state.TotalSize > 0 ? (int)(state.ReceivedSize * 100 / state.TotalSize) : 0;
        FileTransferProgress?.Invoke(this, (state.FileName, pct));
    }

    private void HandleFileEnd(byte[] payload)
    {
        FileReceiveState? state;
        lock (_recvLock) { state = _recvFile; _recvFile = null; }
        if (state?.Stream == null) return;

        state.Stream.Dispose();
        if (File.Exists(state.SavePath))
            File.Delete(state.SavePath);
        File.Move(state.SavePath + ".tmp", state.SavePath);

        FileTransferCompleted?.Invoke(this, (state.FileName, state.SavePath));
    }

    private static string GetUniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var noExt = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(dir, $"{noExt} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    // ====== Sending ======

    public void Send(string text)
    {
        if (_stream == null) return;
        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            var packet = new byte[5 + data.Length];
            packet[0] = (byte)PacketType.Text;
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1), data.Length);
            data.CopyTo(packet, 5);
            lock (_writeLock)
            {
                if (_stream == null) return;
                _stream.Write(packet);
                _stream.Flush();
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"发送失败: {ex.Message}");
        }
    }

    public void SendFile(string filePath)
    {
        Task.Run(() =>
        {
            try
            {
                var fi = new FileInfo(filePath);
                var fileName = fi.Name;
                var fileSize = fi.Length;

                SendPacket(PacketType.FileStart, w =>
                {
                    var nameBytes = Encoding.UTF8.GetBytes(fileName);
                    StreamHelpers.WriteBE32(w, nameBytes.Length);
                    w.Write(nameBytes, 0, nameBytes.Length);
                    StreamHelpers.WriteBE64(w, fileSize);
                });

                using var fs = fi.OpenRead();
                var buffer = new byte[ChunkSize];
                int seq = 0;
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var seqCapture = seq;
                    var readCapture = read;
                    SendPacket(PacketType.FileChunk, w =>
                    {
                        StreamHelpers.WriteBE32(w, seqCapture);
                        StreamHelpers.WriteBE32(w, readCapture);
                        w.Write(buffer, 0, readCapture);
                    });
                    seq++;
                }

                SendPacket(PacketType.FileEnd, w =>
                {
                    StreamHelpers.WriteBE32(w, seq - 1);
                });
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"发送文件失败: {ex.Message}");
            }
        });
    }

    private void SendPacket(PacketType type, Action<MemoryStream> writePayload)
    {
        if (_stream == null) return;
        using var ms = new MemoryStream();
        writePayload(ms);
        var payload = ms.ToArray();

        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);

        lock (_writeLock)
        {
            if (_stream == null) return;
            _stream.Write(header);
            if (payload.Length > 0)
                _stream.Write(payload);
            _stream.Flush();
        }
    }

    private void SendPacketRaw(PacketType type, byte[] payload)
    {
        if (_stream == null) return;
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);

        lock (_writeLock)
        {
            if (_stream == null) return;
            _stream.Write(header);
            if (payload.Length > 0)
                _stream.Write(payload);
            _stream.Flush();
        }
    }

    public void SendProxyConnect(ushort connId, string host, ushort port)
    {
        var hostBytes = Encoding.UTF8.GetBytes(host);
        var payload = new byte[2 + 2 + hostBytes.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0), connId);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), (ushort)hostBytes.Length);
        hostBytes.CopyTo(payload, 4);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4 + hostBytes.Length), port);
        SendPacketRaw(PacketType.ProxyConnect, payload);
    }

    public void SendProxyData(ushort connId, byte[] data)
    {
        var payload = new byte[2 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0), connId);
        data.CopyTo(payload, 2);
        SendPacketRaw(PacketType.ProxyData, payload);
    }

    public void SendProxyClose(ushort connId)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0), connId);
        SendPacketRaw(PacketType.ProxyClose, payload);
    }

    public void SendProxyStatus(ushort connId, bool success)
    {
        var payload = new byte[3];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0), connId);
        payload[2] = (byte)(success ? 0 : 1);
        SendPacketRaw(PacketType.ProxyStatus, payload);
    }

    // ====== Helpers ======

    private byte[]? ReadFull(int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            token.ThrowIfCancellationRequested();
            var read = _stream!.Read(buffer, offset, count - offset);
            if (read == 0) return null;
            offset += read;
        }
        return buffer;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        lock (_recvLock)
        {
            if (_recvFile?.Stream != null)
            {
                _recvFile.Stream.Dispose();
                try { File.Delete(_recvFile.SavePath + ".tmp"); } catch { }
            }
            _recvFile = null;
        }

        try { _stream?.Close(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _stream = null;

        try { _client?.Close(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;

        try { _listener?.Stop(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _listener = null;

        _workerTask = null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

internal static class BinaryWriterExtensions
{
    public static void Write(this MemoryStream ms, byte[] data) => ms.Write(data, 0, data.Length);
}

internal static class StreamHelpers
{
    public static void WriteBE32(this MemoryStream ms, int value)
    {
        ms.WriteByte((byte)((value >> 24) & 0xFF));
        ms.WriteByte((byte)((value >> 16) & 0xFF));
        ms.WriteByte((byte)((value >> 8) & 0xFF));
        ms.WriteByte((byte)(value & 0xFF));
    }

    public static void WriteBE64(this MemoryStream ms, long value)
    {
        ms.WriteByte((byte)((value >> 56) & 0xFF));
        ms.WriteByte((byte)((value >> 48) & 0xFF));
        ms.WriteByte((byte)((value >> 40) & 0xFF));
        ms.WriteByte((byte)((value >> 32) & 0xFF));
        ms.WriteByte((byte)((value >> 24) & 0xFF));
        ms.WriteByte((byte)((value >> 16) & 0xFF));
        ms.WriteByte((byte)((value >> 8) & 0xFF));
        ms.WriteByte((byte)(value & 0xFF));
    }
}
