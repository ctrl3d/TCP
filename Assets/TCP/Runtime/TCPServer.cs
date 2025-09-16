
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class TCPServer : IDisposable
    {
        private readonly IPAddress _address;
        private readonly int _port;
        private readonly ILogger _logger;

        private TcpListener _tcpListener;
        private readonly List<ClientConnection> _connections = new();
        private readonly Dictionary<string, ClientConnection> _clientsByName = new();
        private CancellationTokenSource _cancellationTokenSource;
        private int _nextClientId = 1;

        public event Action<int, string> OnClientConnected; // clientId, clientName
        public event Action<int, string> OnClientDisconnected; // clientId, clientName
        public event Action<int, string, string> OnMessageReceived; // clientId, clientName, message

        public int ConnectedClientsCount
        {
            get
            {
                lock (_connections)
                {
                    return _connections.Count;
                }
            }
        }

        public bool IsRunning => _tcpListener != null && _cancellationTokenSource is
        {
            Token:
            {
                IsCancellationRequested: false
            }
        };

        public TCPServer(string address, ushort port, ILogger logger = null)
        {
            if (!IPAddress.TryParse(address, out _address))
            {
                throw new ArgumentException("Invalid IP address", nameof(address));
            }

            _port = port;
            _logger = logger ?? new ConsoleLogger();
        }

        public void Start()
        {
            if (_tcpListener != null)
            {
                _logger.LogWarning("Server is already running.");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _tcpListener = new TcpListener(_address, _port);
            try
            {
                _tcpListener.Start();
                _logger.Log($"Server started on {_address}:{_port}. Waiting for connections...");

                Task.Run(() => CleanupLoop(_cancellationTokenSource.Token));
                AcceptConnectionsAsync(_cancellationTokenSource.Token);
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to start server: {e.Message}");
                Stop();
            }
        }

        private async void AcceptConnectionsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var clientId = _nextClientId++;
                    var clientConnection = new ClientConnection(tcpClient, this, clientId, _logger);

                    lock (_connections)
                    {
                        _connections.Add(clientConnection);
                    }

                    _logger.Log($"Client {clientId} connected: {tcpClient.Client.RemoteEndPoint}. Total clients: {_connections.Count}");
                    clientConnection.StartReceiving();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _logger.LogError($"Error accepting connection: {e.Message}");
                    }
                }
            }
        }

        private async Task CleanupLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, token);
                    CleanUpConnections();
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 클라이언트 이름 등록
        /// </summary>
        public bool RegisterClientName(ClientConnection connection, string clientName)
        {
            if (string.IsNullOrWhiteSpace(clientName))
                return false;

            lock (_clientsByName)
            {
                if (_clientsByName.ContainsKey(clientName))
                    return false;

                _clientsByName[clientName] = connection;
                OnClientConnected?.Invoke(connection.ClientId, clientName);
                return true;
            }
        }
        
        public void ClientDisconnected(ClientConnection clientConnection)
        {
            lock (_connections)
            {
                _connections.Remove(clientConnection);
            }

            lock (_clientsByName)
            {
                if (!string.IsNullOrEmpty(clientConnection.ClientName))
                {
                    _clientsByName.Remove(clientConnection.ClientName);
                }
            }

            _logger.Log($"Client {clientConnection.ClientId} ({clientConnection.ClientName}) disconnected. Total clients: {_connections.Count}");
            OnClientDisconnected?.Invoke(clientConnection.ClientId, clientConnection.ClientName);
        }

        public void CleanUpConnections()
        {
            List<ClientConnection> disconnectedClients;
            lock (_connections)
            {
                disconnectedClients = _connections.Where(c => !c.IsAlive).ToList();
            }

            if (disconnectedClients.Count > 0)
            {
                _logger.Log($"Cleaning up {disconnectedClients.Count} disconnected client(s).");
                foreach (var client in disconnectedClients)
                {
                    client.Disconnect();
                }
            }
        }

        public void BroadcastMessage(string message)
        {
            lock (_connections)
            {
                foreach (var connection in _connections.Where(c => c.IsNameRegistered).ToList())
                {
                    connection.SendData(message);
                }
            }
        }

        /// <summary>
        /// 클라이언트 ID로 메시지 전송
        /// </summary>
        public bool SendMessageToClient(int clientId, string message)
        {
            lock (_connections)
            {
                var client = _connections.FirstOrDefault(c => c.ClientId == clientId && c.IsNameRegistered);
                return client?.SendData(message) ?? false;
            }
        }

        /// <summary>
        /// 클라이언트 이름으로 메시지 전송
        /// </summary>
        public bool SendMessageToClient(string clientName, string message)
        {
            lock (_clientsByName)
            {
                return _clientsByName.TryGetValue(clientName, out var client) && client.SendData(message);
            }
        }

        /// <summary>
        /// 연결된 클라이언트 이름 목록 반환
        /// </summary>
        public string[] GetConnectedClientNames()
        {
            lock (_clientsByName)
            {
                return _clientsByName.Keys.ToArray();
            }
        }

        /// <summary>
        /// 클라이언트 정보 반환 (ID, 이름 매핑)
        /// </summary>
        public Dictionary<int, string> GetClientInfo()
        {
            lock (_connections)
            {
                return _connections
                    .Where(c => c.IsNameRegistered)
                    .ToDictionary(c => c.ClientId, c => c.ClientName);
            }
        }

        internal void OnMessageReceivedFromClient(int clientId, string clientName, string message)
        {
            OnMessageReceived?.Invoke(clientId, clientName, message);
        }

        public void Dispose()
        {
            if (_tcpListener == null) return;
            
            ClearAllEvents();
            
            Stop();
        }

        private void ClearAllEvents()
        {
            OnClientConnected = null;
            OnClientDisconnected = null;
            OnMessageReceived = null;
        }

        public void Stop()
        {
            if (_tcpListener == null) return;

            _logger.Log("Stopping server...");
            _cancellationTokenSource?.Cancel();
            _tcpListener?.Stop();

            lock (_connections)
            {
                foreach (var connection in _connections.ToList())
                {
                    connection.Disconnect();
                }
                _connections.Clear();
            }

            lock (_clientsByName)
            {
                _clientsByName.Clear();
            }

            _tcpListener = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _logger.Log("Server stopped.");
        }
    }
}