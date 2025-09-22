using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using work.ctrl3d.Logger;
using work.ctrl3d.Constants;

namespace work.ctrl3d
{
    /// <summary>
    /// TCP 서버 구현 클래스
    /// </summary>
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

        // 이벤트 정의
        public event Action<int, string> OnClientConnected; // clientId, clientName
        public event Action<int, string> OnClientDisconnected; // clientId, clientName
        public event Action<int, string, string> OnMessageReceived; // clientId, clientName, message

        // 로그 제어 프로퍼티
        public bool EnableConnectionLogs { get; set; } = true;
        public bool EnableMessageLogs { get; set; } = true;
        public bool EnableSystemLogs { get; set; } = true;
        public bool EnableErrorLogs { get; set; } = true;
        public bool EnableHeartbeatLogs { get; set; } = false;
        public bool EnableClientStateLogs { get; set; } = true;

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
                throw new ArgumentException("유효하지 않은 IP 주소입니다.", nameof(address));
            }

            _port = port;
            _logger = logger ?? new ConsoleLogger();
        }

        #region Logging Methods

        private void LogConnection(string message)
        {
            if (EnableConnectionLogs)
                _logger.Log(message);
        }

        private void LogMessage(string message)
        {
            if (EnableMessageLogs)
                _logger.Log(message);
        }

        private void LogSystem(string message)
        {
            if (EnableSystemLogs)
                _logger.Log(message);
        }

        private void LogError(string message)
        {
            if (EnableErrorLogs)
                _logger.LogError(message);
        }

        private void LogWarning(string message)
        {
            if (EnableErrorLogs) // 경고도 에러 로그 설정을 따름
                _logger.LogWarning(message);
        }

        private void LogHeartbeat(string message)
        {
            if (EnableHeartbeatLogs)
                _logger.Log(message);
        }

        private void LogClientState(string message)
        {
            if (EnableClientStateLogs)
                _logger.Log(message);
        }

        #endregion

        #region Log Control Methods

        /// <summary>
        /// 모든 로그 활성화
        /// </summary>
        public void EnableAllLogs()
        {
            EnableConnectionLogs = true;
            EnableMessageLogs = true;
            EnableSystemLogs = true;
            EnableErrorLogs = true;
            EnableHeartbeatLogs = true;
            EnableClientStateLogs = true;
            
            if (_logger is ILogger logger)
            {
                logger.IsLogEnabled = true;
                logger.LogLevel = LogLevel.Debug;
            }
        }

        /// <summary>
        /// 모든 로그 비활성화
        /// </summary>
        public void DisableAllLogs()
        {
            EnableConnectionLogs = false;
            EnableMessageLogs = false;
            EnableSystemLogs = false;
            EnableErrorLogs = false;
            EnableHeartbeatLogs = false;
            EnableClientStateLogs = false;
            
            if (_logger is ILogger logger)
            {
                logger.IsLogEnabled = false;
            }
        }

        /// <summary>
        /// 에러 로그만 활성화
        /// </summary>
        public void EnableErrorLogsOnly()
        {
            EnableConnectionLogs = false;
            EnableMessageLogs = false;
            EnableSystemLogs = false;
            EnableErrorLogs = true;
            EnableHeartbeatLogs = false;
            EnableClientStateLogs = false;
            
            if (_logger is ILogger logger)
            {
                logger.IsLogEnabled = true;
                logger.LogLevel = LogLevel.Error;
            }
        }

        /// <summary>
        /// 디버깅용 로그 설정
        /// </summary>
        public void SetDebugLogging(bool enabled)
        {
            if (enabled)
            {
                EnableAllLogs();
            }
            else
            {
                EnableErrorLogsOnly();
            }
        }

        #endregion

        #region Server Control

        /// <summary>
        /// 서버를 시작합니다.
        /// </summary>
        public void Start()
        {
            if (_tcpListener != null)
            {
                LogWarning("서버가 이미 실행 중입니다.");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _tcpListener = new TcpListener(_address, _port);
            try
            {
                _tcpListener.Start();
                LogConnection($"서버가 {_address}:{_port}에서 시작되었습니다. 연결을 기다리는 중...");

                Task.Run(() => CleanupLoop(_cancellationTokenSource.Token));
                AcceptConnectionsAsync(_cancellationTokenSource.Token);
            }
            catch (Exception e)
            {
                LogError($"서버 시작 실패: {e.Message}");
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

                    LogConnection($"클라이언트 {clientId} 연결됨: {tcpClient.Client.RemoteEndPoint}. 총 클라이언트 수: {_connections.Count}");
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
                        LogError($"연결 수락 중 오류 발생: {e.Message}");
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

        #endregion

        #region Client Management

        /// <summary>
        /// 클라이언트 이름 등록
        /// </summary>
        public bool RegisterClientName(ClientConnection connection, string clientName)
        {
            if (string.IsNullOrWhiteSpace(clientName))
                return false;

            lock (_clientsByName)
            {
                if (!_clientsByName.TryAdd(clientName, connection))
                    return false;

                LogClientState($"클라이언트 {connection.ClientId}가 이름을 등록했습니다: {clientName}");
                OnClientConnected?.Invoke(connection.ClientId, clientName);
                return true;
            }
        }
        
        /// <summary>
        /// 클라이언트 연결 해제 처리
        /// </summary>
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

            LogClientState($"클라이언트 {clientConnection.ClientId} ({clientConnection.ClientName}) 연결 해제. 총 클라이언트 수: {_connections.Count}");
            OnClientDisconnected?.Invoke(clientConnection.ClientId, clientConnection.ClientName);
        }

        /// <summary>
        /// 연결이 끊어진 클라이언트를 정리합니다.
        /// </summary>
        public void CleanUpConnections()
        {
            List<ClientConnection> disconnectedClients;
            lock (_connections)
            {
                disconnectedClients = _connections.Where(c => !c.IsAlive).ToList();
            }

            if (disconnectedClients.Count > 0)
            {
                LogConnection($"연결이 끊어진 {disconnectedClients.Count}개의 클라이언트를 정리합니다.");
                foreach (var client in disconnectedClients)
                {
                    client.Disconnect();
                }
            }
        }

        #endregion

        #region Messaging

        /// <summary>
        /// 모든 연결된 클라이언트에게 메시지를 브로드캐스트합니다.
        /// </summary>
        public void BroadcastMessage(string message)
        {
            lock (_connections)
            {
                foreach (var connection in _connections.Where(c => c.IsNameRegistered).ToList())
                {
                    connection.SendData(message);
                }
            }
            LogMessage($"모든 클라이언트에게 메시지를 브로드캐스트합니다: {message}");
        }

        /// <summary>
        /// 클라이언트 ID로 메시지 전송
        /// </summary>
        public bool SendMessageToClient(int clientId, string message)
        {
            lock (_connections)
            {
                var client = _connections.FirstOrDefault(c => c.ClientId == clientId && c.IsNameRegistered);
                var result = client?.SendData(message) ?? false;
                if (result)
                    LogMessage($"클라이언트 ID {clientId}에게 메시지 전송: {message}");
                else
                    LogWarning($"클라이언트 ID {clientId}에게 메시지 전송 실패: {message}");
                return result;
            }
        }

        /// <summary>
        /// 클라이언트 이름으로 메시지 전송
        /// </summary>
        public bool SendMessageToClient(string clientName, string message)
        {
            lock (_clientsByName)
            {
                var result = _clientsByName.TryGetValue(clientName, out var client) && client.SendData(message);
                if (result)
                    LogMessage($"클라이언트 {clientName}에게 메시지 전송: {message}");
                else
                    LogWarning($"클라이언트 {clientName}에게 메시지 전송 실패: {message}");
                return result;
            }
        }

        /// <summary>
        /// 클라이언트가 다른 클라이언트에게 메시지 전송 (클라이언트 이름으로)
        /// </summary>
        public bool SendMessageBetweenClients(string senderName, string receiverName, string message)
        {
            lock (_clientsByName)
            {
                if (_clientsByName.TryGetValue(receiverName, out var receiver))
                {
                    var formattedMessage = SystemMessages.FormatFromMessage(senderName, message);
                    LogMessage($"{senderName}에서 {receiverName}으로 메시지 라우팅: {message}");
                    return receiver.SendData(formattedMessage);
                }
                
                // 받는 사람이 없으면 발신자에게 알림
                if (_clientsByName.TryGetValue(senderName, out var sender))
                {
                    sender.SendData(SystemMessages.FormatSystemMessage(SystemMessages.FormatUserNotFoundMessage(receiverName)));
                }
                
                LogWarning($"{senderName}이(가) 보낸 메시지를 위한 사용자 {receiverName}을(를) 찾을 수 없습니다.");
                return false;
            }
        }

        /// <summary>
        /// 클라이언트가 다른 클라이언트에게 메시지 전송 (클라이언트 ID로)
        /// </summary>
        public bool SendMessageBetweenClients(int senderId, int receiverId, string message)
        {
            lock (_connections)
            {
                var sender = _connections.FirstOrDefault(c => c.ClientId == senderId && c.IsNameRegistered);
                var receiver = _connections.FirstOrDefault(c => c.ClientId == receiverId && c.IsNameRegistered);
                
                if (sender != null && receiver != null)
                {
                    var formattedMessage = SystemMessages.FormatFromMessage(sender.ClientName, message);
                    LogMessage($"{sender.ClientName}에서 {receiver.ClientName}으로 메시지 라우팅: {message}");
                    return receiver.SendData(formattedMessage);
                }
                
                // 받는 사람이 없으면 발신자에게 알림
                if (sender != null)
                {
                    sender.SendData(SystemMessages.FormatSystemMessage(SystemMessages.FormatUserNotFoundMessage($"{SystemMessages.IDPrefix}{receiverId}")));
                }
                
                LogWarning($"{senderId}가 보낸 메시지를 위한 사용자 ID {receiverId}를 찾을 수 없습니다.");
                return false;
            }
        }

        /// <summary>
        /// 온라인 사용자 목록을 특정 클라이언트에게 전송
        /// </summary>
        public void SendUserListToClient(string clientName)
        {
            var userList = GetConnectedClientNames();
            var userListMessage = SystemMessages.FormatSystemMessage(SystemMessages.FormatUserListMessage(string.Join(",", userList)));
            
            lock (_clientsByName)
            {
                if (_clientsByName.TryGetValue(clientName, out var client))
                {
                    LogSystem($"{clientName}에게 사용자 목록 전송: {userList.Length}명의 사용자");
                    client.SendData(userListMessage);
                }
                else
                {
                    LogWarning($"사용자 목록을 전송할 수 없습니다: 클라이언트 {clientName}을(를) 찾을 수 없습니다.");
                }
            }
        }

        /// <summary>
        /// 온라인 사용자 목록을 클라이언트 ID로 전송
        /// </summary>
        public void SendUserListToClient(int clientId)
        {
            var userList = GetConnectedClientNames();
            var userListMessage = SystemMessages.FormatSystemMessage(SystemMessages.FormatUserListMessage(string.Join(",", userList)));
            
            lock (_connections)
            {
                var client = _connections.FirstOrDefault(c => c.ClientId == clientId && c.IsNameRegistered);
                if (client != null)
                {
                    LogSystem($"클라이언트 ID {clientId}에게 사용자 목록 전송: {userList.Length}명의 사용자");
                    client.SendData(userListMessage);
                }
                else
                {
                    LogWarning($"사용자 목록을 전송할 수 없습니다: 클라이언트 ID {clientId}을(를) 찾을 수 없습니다.");
                }
            }
        }

        /// <summary>
        /// 모든 클라이언트에게 온라인 사용자 목록 브로드캐스트
        /// </summary>
        public void BroadcastUserList()
        {
            var userList = GetConnectedClientNames();
            var userListMessage = SystemMessages.FormatSystemMessage(SystemMessages.FormatUserListMessage(string.Join(",", userList)));
            
            LogSystem($"모든 클라이언트에게 사용자 목록 브로드캐스트: {userList.Length}명의 사용자");
            lock (_connections)
            {
                foreach (var connection in _connections.Where(c => c.IsNameRegistered).ToList())
                {
                    connection.SendData(userListMessage);
                }
            }
        }

        /// <summary>
        /// 클라이언트로부터 받은 메시지를 처리하고 라우팅
        /// </summary>
        public void ProcessClientMessage(int clientId, string clientName, string message)
        {
            LogMessage($"{clientName} ({clientId})로부터 메시지 처리 중: {message}");

            // TO: 명령어 처리 (다른 사용자에게 메시지 전송)
            if (message.StartsWith(SystemMessages.ToPrefix))
            {
                var parts = message.Split(':', 3);
                if (parts.Length == 3)
                {
                    var targetUser = parts[1];
                    var userMessage = parts[2];
                    SendMessageBetweenClients(clientName, targetUser, userMessage);
                    return;
                }
            }

            // GET_USERS 명령어 처리 (사용자 목록 요청)
            if (message.Equals(SystemMessages.GetUsers, StringComparison.OrdinalIgnoreCase))
            {
                SendUserListToClient(clientName);
                return;
            }

            // BROADCAST: 명령어 처리
            if (message.StartsWith(SystemMessages.BroadcastPrefix))
            {
                var broadcastMessage = message[SystemMessages.BroadcastPrefix.Length..];
                var formattedMessage = SystemMessages.FormatBroadcastFromMessage(clientName, broadcastMessage);
                BroadcastMessage(formattedMessage);
                return;
            }

            // PING 명령어 처리
            if (message.Equals(SystemMessages.Ping, StringComparison.OrdinalIgnoreCase))
            {
                LogHeartbeat($"{clientName} ({clientId})로부터 PING 받음");
                SendMessageToClient(clientName, SystemMessages.Pong);
                return;
            }

            // 일반 메시지는 기존 이벤트로 처리
            OnMessageReceived?.Invoke(clientId, clientName, message);
        }

        #endregion

        #region Client Management Methods

        /// <summary>
        /// 특정 클라이언트를 강제로 킥합니다 (이름으로)
        /// </summary>
        public bool KickClient(string clientName, string reason = "관리자에 의해 강제 퇴장되었습니다")
        {
            lock (_clientsByName)
            {
                if (_clientsByName.TryGetValue(clientName, out var client))
                {
                    LogSystem($"클라이언트 {clientName} 킥 처리: {reason}");
                    client.Kick(reason);
                    return true;
                }
                
                LogWarning($"킥할 클라이언트 {clientName}을(를) 찾을 수 없습니다.");
                return false;
            }
        }

        /// <summary>
        /// 특정 클라이언트를 강제로 킥합니다 (ID로)
        /// </summary>
        public bool KickClient(int clientId, string reason = "관리자에 의해 강제 퇴장되었습니다")
        {
            lock (_connections)
            {
                var client = _connections.FirstOrDefault(c => c.ClientId == clientId);
                if (client != null)
                {
                    LogSystem($"클라이언트 ID {clientId} ({client.ClientName}) 킥 처리: {reason}");
                    client.Kick(reason);
                    return true;
                }
                
                LogWarning($"킥할 클라이언트 ID {clientId}를 찾을 수 없습니다.");
                return false;
            }
        }

        /// <summary>
        /// 모든 클라이언트를 킥합니다
        /// </summary>
        public void KickAllClients(string reason = "서버 종료로 인한 연결 해제")
        {
            List<ClientConnection> clientsToKick;
            lock (_connections)
            {
                clientsToKick = _connections.ToList();
            }

            LogSystem($"모든 클라이언트 킥 처리: {clientsToKick.Count}명의 클라이언트");
            
            foreach (var client in clientsToKick)
            {
                client.Kick(reason);
            }
        }

        #endregion

        #region Information

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

        /// <summary>
        /// 모든 클라이언트 연결 정보 반환 (등록된 이름 포함)
        /// </summary>
        public List<(int ClientId, string ClientName, bool IsNameRegistered, bool IsAlive, string EndPoint)> GetAllClientInfo()
        {
            lock (_connections)
            {
                return _connections
                    .Select(c => (c.ClientId, c.ClientName, c.IsNameRegistered, c.IsAlive, c.GetClientEndPoint()))
                    .ToList();
            }
        }

        /// <summary>
        /// 클라이언트로부터 받은 메시지를 내부적으로 처리
        /// </summary>
        internal void OnMessageReceivedFromClient(int clientId, string clientName, string message)
        {
            // 메시지 처리 및 라우팅
            ProcessClientMessage(clientId, clientName, message);
        }

        #endregion

        #region Server Commands

        /// <summary>
        /// 모든 클라이언트에게 서버 공지사항 전송
        /// </summary>
        public void SendServerAnnouncement(string announcement)
        {
            var formattedMessage = $"[서버 공지] {announcement}";
            BroadcastMessage(formattedMessage);
            LogSystem($"서버 공지사항 전송: {announcement}");
        }

        /// <summary>
        /// 특정 클라이언트에게 서버 메시지 전송
        /// </summary>
        public bool SendServerMessage(string clientName, string message)
        {
            var formattedMessage = $"[서버 메시지] {message}";
            var result = SendMessageToClient(clientName, formattedMessage);
            if (result)
                LogSystem($"{clientName}에게 서버 메시지 전송: {message}");
            return result;
        }

        /// <summary>
        /// 서버 통계 정보 반환
        /// </summary>
        public string GetServerStats()
        {
            lock (_connections)
            {
                var totalConnections = _connections.Count;
                var registeredConnections = _connections.Count(c => c.IsNameRegistered);
                var aliveConnections = _connections.Count(c => c.IsAlive);
                
                return $"서버 통계 - 총 연결: {totalConnections}, 등록된 클라이언트: {registeredConnections}, " +
                       $"활성 연결: {aliveConnections}, 서버 주소: {_address}:{_port}";
            }
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// 서버를 중지하고 리소스를 해제합니다.
        /// </summary>
        public void Stop()
        {
            if (_tcpListener == null) return;

            LogConnection("서버를 중지합니다...");
            
            // 모든 클라이언트에게 서버 종료 알림
            KickAllClients("서버가 종료됩니다.");
            
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
            LogConnection("서버가 중지되었습니다.");
        }

        /// <summary>
        /// 서버를 종료하고 리소스를 정리합니다.
        /// </summary>
        public void Dispose()
        {
            if (_tcpListener == null) return;
            
            ClearAllEvents();
            Stop();
        }

        /// <summary>
        /// 모든 이벤트 구독을 제거합니다.
        /// </summary>
        private void ClearAllEvents()
        {
            OnClientConnected = null;
            OnClientDisconnected = null;
            OnMessageReceived = null;
        }

        #endregion

        #region Server Status

        /// <summary>
        /// 서버 상태 정보를 문자열로 반환
        /// </summary>
        public string GetStatusInfo()
        {
            return $"TCPServer[Address:{_address}:{_port}, Running:{IsRunning}, " +
                   $"Clients:{ConnectedClientsCount}]";
        }

        /// <summary>
        /// 로그 설정 정보를 문자열로 반환
        /// </summary>
        public string GetLogSettings()
        {
            return $"LogSettings[Connection:{EnableConnectionLogs}, Message:{EnableMessageLogs}, " +
                   $"System:{EnableSystemLogs}, Error:{EnableErrorLogs}, Heartbeat:{EnableHeartbeatLogs}, " +
                   $"ClientState:{EnableClientStateLogs}]";
        }

        /// <summary>
        /// 서버의 상세한 상태 정보를 반환
        /// </summary>
        public string GetDetailedStatusInfo()
        {
            var stats = GetServerStats();
            var logSettings = GetLogSettings();
            var uptime = IsRunning ? "실행 중" : "중지됨";
            
            return $"=== TCP 서버 상태 ===\n" +
                   $"상태: {uptime}\n" +
                   $"{stats}\n" +
                   $"{logSettings}\n" +
                   $"다음 정리 작업: 5초마다";
        }

        #endregion
    }
}