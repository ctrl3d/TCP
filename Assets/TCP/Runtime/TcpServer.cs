using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class TcpServer : IDisposable
    {
        private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
        private readonly ConcurrentDictionary<string, int> _clientNameMap = new();

        private TcpListener _tcpListener;
        private CancellationTokenSource _cts;
        private readonly ILogger _logger;
        private int _nextClientId = 1;
        private readonly int _port;

        public event Action<int, string> OnClientConnected;
        public event Action<int, string> OnClientDisconnected;
        public event Action<int, string, string> OnMessageReceived;

        public bool IsRunning => _tcpListener != null;
    
        public TcpServer(int port, ILogger logger)
        {
            _port = port;
            _logger = logger;
        }

        public void Start()
        {
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _tcpListener = new TcpListener(IPAddress.Any, _port);
    
            try
            {
                _tcpListener.Start();
                _logger.Log(LogFilter.System, $"서버 시작됨 (Port: {_port})");
                
                // [Review Fix] async Task 실행을 명시적으로 처리 (Fire and forget)
                _ = AcceptConnectionsAsync(_cts.Token);
            }
            catch (Exception e)
            {
                _logger.LogError(LogFilter.Error, $"서버 시작 실패: {e.Message}");
            }
        }

        // [Review Fix] async void -> async Task
        private async Task AcceptConnectionsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var clientId = _nextClientId++;
            
                    var session = new ClientSession(clientId, tcpClient, this, _logger);
                    _sessions.TryAdd(clientId, session);
            
                    _logger.Log(LogFilter.Connection, $"클라이언트 접속: ID {clientId}");
            
                    _ = Task.Run(() => session.StartReceivingAsync(token), token);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    // 리스너 오류는 치명적일 수 있으나, 루프를 유지할지 결정 필요. 
                    // 여기서는 로그만 남기고 계속 시도.
                    _logger.LogError(LogFilter.Error, $"Accept Error: {e.Message}");
                }
            }
        }

        internal void HandleMessage(int clientId, string clientName, string message)
        {
            if (message.Equals(TcpProtocol.CmdPing, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log(LogFilter.Heartbeat, $"Received PING from {clientName} ({clientId})");
                SendToClient(clientId, TcpProtocol.CmdPong);
                return;
            }

            var parts = message.Split(new[] { TcpProtocol.CmdSeparator }, 3);
            var command = parts[0];

            switch (command)
            {
                case TcpProtocol.CmdTo:
                    if (parts.Length >= 3)
                        HandlePrivateMessage(clientName, targetName: parts[1], content: parts[2]);
                    break;

                case TcpProtocol.CmdBroadcast:
                    var broadcastMsg = message.Substring(TcpProtocol.CmdBroadcast.Length + 1);
                    Broadcast(TcpProtocol.Pack("FROM", clientName, broadcastMsg));
                    break;

                case TcpProtocol.CmdGetUsers:
                    SendUserList(clientId);
                    break;

                default:
                    OnMessageReceived?.Invoke(clientId, clientName, message);
                    break;
            }
        }

        private void HandlePrivateMessage(string senderName, string targetName, string content)
        {
            if (_clientNameMap.TryGetValue(targetName, out var targetId))
            {
                SendToClient(targetId, TcpProtocol.Pack("FROM", senderName, content));
            }
            else
            {
                 if (_clientNameMap.TryGetValue(senderName, out int senderId))
                    SendToClient(senderId, TcpProtocol.Pack(TcpProtocol.SystemUserNotFound, targetName));
            }
        }

        private void SendUserList(int targetClientId)
        {
            var userList = string.Join(",", _clientNameMap.Keys);
            SendToClient(targetClientId, TcpProtocol.Pack(TcpProtocol.SystemUserList, userList));
        }

        internal bool RegisterClientName(int clientId, string name)
        {
            if (!_clientNameMap.TryAdd(name, clientId)) return false;
            OnClientConnected?.Invoke(clientId, name);
            return true;
        }

        internal void UnregisterClient(int clientId, string name)
        {
            _sessions.TryRemove(clientId, out _);
            if (!string.IsNullOrEmpty(name))
            {
                _clientNameMap.TryRemove(name, out _);
            }
            OnClientDisconnected?.Invoke(clientId, name);
        }

        public void SendToClient(int clientId, string message)
        {
            if (_sessions.TryGetValue(clientId, out var session))
            {
                session.Send(message);
            }
        }

        public void Broadcast(string message)
        {
            foreach (var session in _sessions.Values)
            {
                session.Send(message);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _tcpListener?.Stop();
            foreach (var session in _sessions.Values)
            {
                session.Disconnect();
            }
            _sessions.Clear();
            _clientNameMap.Clear();
            _logger.Log(LogFilter.System, "서버 종료됨");
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }

    public class ClientSession
    {
        public int ClientId { get; }
        public string ClientName { get; private set; }

        private readonly System.Net.Sockets.TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly TcpServer _server;
        private readonly ILogger _logger;

        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public ClientSession(int id, System.Net.Sockets.TcpClient client, TcpServer server, ILogger logger)
        {
            ClientId = id;
            _client = client;
            _stream = client.GetStream();
            _server = server;
            _logger = logger;
        }

        public async void Send(string message)
        {
            try
            {
                var packet = TcpProtocol.CreatePacket(message);
        
                await _writeLock.WaitAsync();
                try
                {
                    if (_stream != null && _stream.CanWrite)
                    {
                        await _stream.WriteAsync(packet, 0, packet.Length);
                    }
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(LogFilter.Error, $"Send Error (ID {ClientId}): {e.Message}");
                Disconnect();
            }
        }

        public async Task StartReceivingAsync(CancellationToken token)
        {
            var headerBuffer = new byte[4]; 

            try
            {
                while (!token.IsCancellationRequested && _client.Connected)
                {
                    var bytesRead = await ReadExactAsync(headerBuffer, 4, token);
                    if (bytesRead == 0) break; 

                    var bodyLength = BitConverter.ToInt32(headerBuffer, 0);
            
                    if (bodyLength is < 0 or > TcpProtocol.MaxPacketSize)
                    {
                        _logger.LogError(LogFilter.Error, $"Invalid packet size (ID {ClientId}): {bodyLength}. Disconnecting.");
                        break;
                    }
                    
                    var bodyBuffer = ArrayPool<byte>.Shared.Rent(bodyLength);
                    try 
                    {
                        bytesRead = await ReadExactAsync(bodyBuffer, bodyLength, token);
                        if (bytesRead != bodyLength) break;
                        
                        var message = Encoding.UTF8.GetString(bodyBuffer, 0, bodyLength);
            
                        if (string.IsNullOrEmpty(ClientName)) 
                        {
                            if (message.StartsWith("CONNECT:"))
                            {
                                var parts = message.Split(TcpProtocol.CmdSeparator);
                                if (parts.Length > 1)
                                {
                                    var requestedName = parts[1];
                                    if (_server.RegisterClientName(ClientId, requestedName))
                                    {
                                        ClientName = requestedName;
                                    }
                                    else
                                    {
                                        Send($"{TcpProtocol.SystemNameTaken}");
                                        _logger.LogWarning(LogFilter.Error, $"Client ID {ClientId} tried to take existing name: {requestedName}");
                                        break; 
                                    }
                                }
                            }
                        }
                        else
                        {
                            _server.HandleMessage(ClientId, ClientName, message);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(bodyBuffer);
                    }
                }
            }
            catch (Exception e)
            {
                if (!token.IsCancellationRequested)
                    _logger.LogError(LogFilter.Error, $"Receive Error (ID {ClientId}): {e.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

        private async Task<int> ReadExactAsync(byte[] buffer, int count, CancellationToken token)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await _stream.ReadAsync(buffer, offset, count - offset, token);
                if (read == 0) return 0; 
                offset += read;
            }
            return offset;
        }

        public void Disconnect()
        {
            _server.UnregisterClient(ClientId, ClientName);
            try { _client.Close(); } catch {}
            _writeLock?.Dispose(); 
        }
    }
}