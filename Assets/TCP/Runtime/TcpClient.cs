using System;
using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using work.ctrl3d.Logger;

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
        
        public async Task ConnectToServerAsync()
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
                var connectTask = _tcpClient.ConnectAsync(_address, _port);
                var timeoutTask = Task.Delay(5000);
            
                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
                    throw new TimeoutException("Connection timed out.");
                }
                await connectTask; // 예외가 있다면 여기서 다시 던짐
                
                IsConnecting = false;
                _networkStream = _tcpClient.GetStream();
                _cts = new CancellationTokenSource();

                _logger.Log(LogFilter.Connection, "Connected!");
                OnConnected?.Invoke();
            
                if (!string.IsNullOrEmpty(ClientName))
                {
                    // 이름 등록 요청
                    await SendRawMessageAsync(TcpProtocol.Pack("CONNECT", ClientName));
                }

                // 수신 루프 시작 (Fire and forget)
                _ = ReceiveLoopAsync(_cts.Token);
            }
            catch (Exception e)
            {
                IsConnecting = false;
                _logger.LogError(LogFilter.Error, $"Connection failed: {e.Message}");
                OnConnectionFailed?.Invoke(e.Message);
                Disconnect();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            // 헤더 버퍼는 작으므로 재사용
            var headerBuffer = new byte[4]; 

            try
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    var bytesRead = await ReadExactAsync(headerBuffer, 4, token);
                    if (bytesRead == 0) break; 

                    var bodyLength = BitConverter.ToInt32(headerBuffer, 0);
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

        private async Task<int> ReadExactAsync(byte[] buffer, int count, CancellationToken token)
        {
            if (_networkStream == null) return 0;
            var offset = 0;
            while (offset < count)
            {
                var read = await _networkStream.ReadAsync(buffer, offset, count - offset, token);
                if (read == 0) return 0;
                offset += read;
            }
            return offset;
        }

        public void Send(string message)
        {
            if (!IsConnected) return;
            _ = SendRawMessageAsync(message);
        }

        private async Task SendRawMessageAsync(string message)
        {
            try
            {
                var packet = TcpProtocol.CreatePacket(message);
                
                await _writeLock.WaitAsync();
                try
                {
                    if (_networkStream != null && _networkStream.CanWrite)
                    {
                        await _networkStream.WriteAsync(packet, 0, packet.Length);
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
             try {
                 return !(_tcpClient.Client.Poll(1, SelectMode.SelectRead) && _tcpClient.Client.Available == 0);
             } catch { return false; }
        }

        private void ProcessMessage(string message)
        {
            if (message == TcpProtocol.CmdPong)
            {
                _logger.Log(LogFilter.Heartbeat, "Received PONG");
                return;
            }

            if (message.StartsWith("SYSTEM:"))
            {
                ProcessSystemMessage(message.Substring(7));
                return;
            }

            if (message.StartsWith("FROM:"))
            {
                var parts = message.Split(new[] { TcpProtocol.CmdSeparator }, 3);
                if (parts.Length >= 3)
                {
                    OnDirectMessageReceived?.Invoke(parts[1], parts[2]);
                }
                else if (parts.Length >= 2) 
                {
                    OnMessageReceived?.Invoke(message);
                }
                return;
            }

            OnMessageReceived?.Invoke(message);
        }

        private void ProcessSystemMessage(string content)
        {
            OnSystemMessageReceived?.Invoke(content);

            if (content.StartsWith("USER_LIST:"))
            {
                var users = content[10..].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                OnClientListReceived?.Invoke(users);
                if (!IsNameRegistered && Array.Exists(users, u => u == ClientName))
                {
                    _isNameRegistered = true;
                    OnNameRegistered?.Invoke(ClientName);
                }
            }
            else if (content.StartsWith("USER_NOT_FOUND:"))
            {
                _logger.LogWarning(LogFilter.Error, $"User not found: {content.Substring(15)}");
            }
            else if (content == "NAME_TAKEN")
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
            catch { /* Ignored */ }

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