using System;
using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using work.ctrl3d.Logger;
#if USE_UNITASK
using Cysharp.Threading.Tasks;

#else
using System.Threading.Tasks;
#endif

namespace work.ctrl3d
{
    public class TcpClient : IDisposable
    {
        private readonly string _address;
        private readonly int _port;
        private readonly ILogger _logger;

        private System.Net.Sockets.TcpClient _tcpClient;
        private NetworkStream _networkStream;
        private CancellationTokenSource _cts;
        private bool _disposed;
        private bool _isNameRegistered;

        // 스레드 안전한 전송을 위한 락
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public event Action OnConnected;
        public event Action<string> OnMessageReceived;
        public event Action<string> OnSystemMessageReceived;
        public event Action OnDisconnected;
        public event Action<string> OnNameRegistered;
        public event Action OnNameTaken;
        public event Action<string[]> OnClientListReceived;
        public event Action<string, string> OnDirectMessageReceived;
        public event Action<string> OnConnectionFailed;
        public event Action<string> OnKicked;

        public bool IsConnected => _tcpClient is { Connected: true };
        public bool IsNameRegistered => _isNameRegistered;
        public string ClientName { get; private set; }
        public bool IsConnecting { get; private set; }

        public TcpClient(string address, int port, string clientName, ILogger logger)
        {
            _address = address;
            _port = port;
            _logger = logger;
            ClientName = clientName ?? string.Empty;
        }

        // [Review Fix] UniTask를 사용하도록 변경
#if USE_UNITASK
        public async UniTask ConnectToServerAsync()
#else
        public async Task ConnectToServerAsync()
#endif
        {
            if (IsConnected || IsConnecting) return;
            if (_disposed) throw new ObjectDisposedException(nameof(TcpClient));

            try
            {
                IsConnecting = true;
                _tcpClient = new System.Net.Sockets.TcpClient();
                _tcpClient.ReceiveTimeout = 10000;
                _tcpClient.SendTimeout = 10000;

                _logger.Log(LogFilter.Connection, $"Connecting to {_address}:{_port}...");
#if USE_UNITASK
                await _tcpClient.ConnectAsync(_address, _port).AsUniTask().Timeout(TimeSpan.FromSeconds(5));
#else
                var connectTask = _tcpClient.ConnectAsync(_address, _port);
                var timeoutTask = Task.Delay(5000);
            
                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
                    throw new TimeoutException("Connection timed out.");
                }
                await connectTask;
#endif

                IsConnecting = false;
                _networkStream = _tcpClient.GetStream();
                _cts = new CancellationTokenSource();

                _logger.Log(LogFilter.Connection, "Connected!");
                OnConnected?.Invoke();

                if (!string.IsNullOrEmpty(ClientName))
                {
                    await SendRawMessageAsync(TcpProtocol.Pack(TcpProtocol.CmdConnect, ClientName));
                }

#if USE_UNITASK
                ReceiveLoopAsync(_cts.Token).Forget();
#else
                _ = ReceiveLoopAsync(_cts.Token);
#endif
            }
            catch (Exception e)
            {
                IsConnecting = false;
                _logger.LogError(LogFilter.Error, $"Connection failed: {e.Message}");
                OnConnectionFailed?.Invoke(e.Message);
                Disconnect();
            }
        }

#if USE_UNITASK
        private async UniTaskVoid ReceiveLoopAsync(CancellationToken token)
#else
        private async Task ReceiveLoopAsync(CancellationToken token)
#endif
        {
            // 헤더 버퍼는 작으므로 재사용
            var headerBuffer = new byte[4];

            try
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    var bytesRead = await ReadExactAsync(headerBuffer, 4, token);
                    if (bytesRead == 0) break;

                    var bodyLength = TcpProtocol.DecodeInt32BE(headerBuffer, 0);
                    if (bodyLength is < 0 or > TcpProtocol.MaxPacketSize)
                    {
                        _logger.LogError(LogFilter.Error, $"Invalid packet size: {bodyLength}. Disconnecting.");
                        break;
                    }

                    var bodyBuffer = ArrayPool<byte>.Shared.Rent(bodyLength);
                    try
                    {
                        bytesRead = await ReadExactAsync(bodyBuffer, bodyLength, token);
                        if (bytesRead != bodyLength) break;

                        var message = Encoding.UTF8.GetString(bodyBuffer, 0, bodyLength);
                        ProcessMessage(message);
                    }
                    finally
                    {
                        // 반드시 버퍼 반환
                        ArrayPool<byte>.Shared.Return(bodyBuffer);
                    }
                }
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                    _logger.LogError(LogFilter.Error, $"Receive error: {e.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

#if USE_UNITASK
        private async UniTask<int> ReadExactAsync(byte[] buffer, int count, CancellationToken token)
#else
        private async Task<int> ReadExactAsync(byte[] buffer, int count, CancellationToken token)
#endif
        {
            if (_networkStream == null) return 0;
            var offset = 0;
            while (offset < count)
            {
#if USE_UNITASK
                var read = await _networkStream.ReadAsync(buffer, offset, count - offset, token).AsUniTask();
#else
                var read = await _networkStream.ReadAsync(buffer, offset, count - offset, token);
#endif
                if (read == 0) return 0;
                offset += read;
            }

            return offset;
        }

        public void Send(string message)
        {
            if (!IsConnected) return;
#if USE_UNITASK
            SendRawMessageAsync(message).Forget();
#else
            _ = SendRawMessageAsync(message);
#endif
        }

#if USE_UNITASK
        private async UniTask SendRawMessageAsync(string message)
#else
        private async Task SendRawMessageAsync(string message)
#endif
        {
            var (packet, totalLength) = TcpProtocol.RentPacket(message);
            try
            {
                await _writeLock.WaitAsync();
                try
                {
                    if (_networkStream != null && _networkStream.CanWrite)
                    {
#if USE_UNITASK
                        await _networkStream.WriteAsync(packet, 0, totalLength).AsUniTask();
#else
                        await _networkStream.WriteAsync(packet, 0, totalLength);
#endif
                        var filter = message is TcpProtocol.CmdPing or TcpProtocol.CmdPong
                            ? LogFilter.Heartbeat
                            : LogFilter.Message;

                        _logger.Log(filter, $"Sent: {message}");
                    }
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(LogFilter.Error, $"Send error: {e.Message}");
                Disconnect();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }

        public void SendToClient(string targetName, string message)
        {
            Send(TcpProtocol.Pack(TcpProtocol.CmdTo, targetName, message));
        }

        public void Broadcast(string message)
        {
            Send(TcpProtocol.Pack(TcpProtocol.CmdBroadcast, message));
        }

        public void RequestUserList() => Send(TcpProtocol.CmdGetUsers);
        public void RequestClientList() => RequestUserList();

        public void Ping()
        {
            Send(TcpProtocol.CmdPing);
            _logger.Log(LogFilter.Heartbeat, "PING sent");
        }

        public bool TestConnection()
        {
            if (!IsConnected) return false;
            try
            {
                return !(_tcpClient.Client.Poll(1, SelectMode.SelectRead) && _tcpClient.Client.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        private void ProcessMessage(string message)
        {
            if (message == TcpProtocol.CmdPong)
            {
                _logger.Log(LogFilter.Heartbeat, "Received PONG");
                return;
            }

            var (command, args) = TcpProtocol.Unpack(message);

            if (command == TcpProtocol.SystemPrefix)
            {
                if (args.Length > 0)
                {
                    var sysParts = args[0].Split(new[] { TcpProtocol.CmdSeparator }, 2);
                    var sysCmd = sysParts[0];
                    var sysContent = sysParts.Length > 1 ? sysParts[1] : string.Empty;
                    ProcessSystemMessage(sysCmd, sysContent);
                }

                return;
            }

            if (command == TcpProtocol.FromPrefix)
            {
                if (args.Length >= 2)
                {
                    OnDirectMessageReceived?.Invoke(args[0], args[1]);
                }
                else if (args.Length >= 1)
                {
                    OnMessageReceived?.Invoke(args[0]);
                }

                return;
            }

            OnMessageReceived?.Invoke(message);
        }

        private void ProcessSystemMessage(string systemCmd, string content)
        {
            OnSystemMessageReceived?.Invoke($"{systemCmd}:{content}");

            if (systemCmd == TcpProtocol.UserListPrefix)
            {
                var users = content.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                OnClientListReceived?.Invoke(users);
                if (!IsNameRegistered && Array.Exists(users, u => u == ClientName))
                {
                    _isNameRegistered = true;
                    OnNameRegistered?.Invoke(ClientName);
                }
            }
            else if (systemCmd == TcpProtocol.UserNotFoundPrefix)
            {
                _logger.LogWarning(LogFilter.Error, $"User not found: {content}");
            }
            else if (systemCmd == TcpProtocol.NameTakenPrefix)
            {
                OnNameTaken?.Invoke();
                Disconnect();
            }
        }

        public void Disconnect()
        {
            if (_tcpClient == null && !IsConnecting) return;

            _cts?.Cancel();
            try
            {
                _networkStream?.Close();
                _tcpClient?.Close();
            }
            catch
            {
                /* Ignored */
            }

            _tcpClient = null;
            _networkStream = null;
            _isNameRegistered = false;
            IsConnecting = false;

            _logger.Log(LogFilter.Connection, "Disconnected");
            OnDisconnected?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _cts?.Dispose();
            _writeLock?.Dispose();
        }

        public string GetStatusInfo() => $"Connected: {IsConnected}, Name: {ClientName}";
        public string GetConnectionInfo() => IsConnected ? $"Connected to {_address}:{_port}" : "Not connected";
        public string GetLogSettings() => "Logs controlled via ILogger";
    }
}