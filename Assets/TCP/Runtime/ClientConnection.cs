using System;
using System.Net.Sockets;
using System.Text;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class ClientConnection
    {
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _networkStream;
        private readonly byte[] _receiveBuffer;
        private readonly TCPServer _server;
        private readonly ILogger _logger;
        private bool _isConnected;
        
        // 로그 타입 제어
        private bool EnableMessageLogs => _server?.EnableMessageLogs ?? true;
        private bool EnableSystemLogs => _server?.EnableSystemLogs ?? true;
        private bool EnableErrorLogs => _server?.EnableErrorLogs ?? true;
        private bool EnableHeartbeatLogs => _server?.EnableHeartbeatLogs ?? false;
        private bool EnableClientStateLogs => _server?.EnableClientStateLogs ?? true;

        public int ClientId { get; }
        public string ClientName { get; private set; } = string.Empty;
        public bool IsNameRegistered { get; private set; }

        public bool IsAlive
        {
            get
            {
                if (!_isConnected || _tcpClient is not { Connected: true })
                {
                    return false;
                }

                try
                {
                    return !(_tcpClient.Client.Poll(1, SelectMode.SelectRead) && _tcpClient.Client.Available == 0);
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }

        public ClientConnection(TcpClient tcpClient, TCPServer server, int clientId, ILogger logger = null)
        {
            _tcpClient = tcpClient;
            _server = server;
            ClientId = clientId;
            _logger = logger ?? new ConsoleLogger();
            _networkStream = _tcpClient.GetStream();
            _receiveBuffer = new byte[_tcpClient.ReceiveBufferSize];
            _isConnected = true;
            IsNameRegistered = false;
        }
        
        #region Logging Methods
        
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

        public void StartReceiving()
        {
            try
            {
                // 클라이언트에게 이름 등록 요청
                SendSystemMessage("REGISTER_NAME");
                _networkStream.BeginRead(_receiveBuffer, 0, _receiveBuffer.Length, ReceiveData, null);
                LogClientState($"클라이언트 {ClientId} ({GetClientEndPoint()})가 데이터 수신을 시작했습니다");
            }
            catch (Exception e)
            {
                LogError($"데이터 수신 시작 오류: {e.Message}");
                Disconnect();
            }
        }

        private void ReceiveData(IAsyncResult result)
        {
            try
            {
                if (!_isConnected) return;

                var bytesRead = _networkStream.EndRead(result);
                if (bytesRead <= 0)
                {
                    LogClientState($"클라이언트 {ClientId} ({ClientName})가 정상적으로 연결 해제: {GetClientEndPoint()}");
                    Disconnect();
                    return;
                }

                var receivedBytes = new byte[bytesRead];
                Array.Copy(_receiveBuffer, receivedBytes, bytesRead);

                var receivedMessage = Encoding.UTF8.GetString(receivedBytes);
                LogMessage($"클라이언트 {ClientId} ({ClientName}) ({_tcpClient.Client.RemoteEndPoint})에서 수신: {receivedMessage}");

                ProcessReceivedData(receivedMessage);

                if (_isConnected)
                {
                    _networkStream.BeginRead(_receiveBuffer, 0, _receiveBuffer.Length, ReceiveData, null);
                }
            }
            catch (Exception e)
            {
                LogError($"클라이언트 {ClientId} ({ClientName})에서 데이터 수신 오류: {e.Message}");
                Disconnect();
            }
        }

        private void ProcessReceivedData(string data)
        {
            // 이름 등록 처리
            if (!IsNameRegistered && data.StartsWith("NAME:"))
            {
                var proposedName = data[5..].Trim();
                if (_server.RegisterClientName(this, proposedName))
                {
                    ClientName = proposedName;
                    IsNameRegistered = true;
                    SendSystemMessage("NAME_REGISTERED");
                    LogClientState($"클라이언트 {ClientId}가 이름을 등록했습니다: {ClientName}");
                }
                else
                {
                    SendSystemMessage("NAME_TAKEN");
                    LogWarning($"이름 '{proposedName}'은(는) 다른 클라이언트가 이미 사용 중입니다");
                }
                return;
            }

            // 이름이 등록되지 않은 경우 메시지 무시
            if (!IsNameRegistered)
            {
                SendSystemMessage("REGISTER_NAME_FIRST");
                LogWarning($"등록되지 않은 클라이언트 {ClientId}의 메시지를 무시합니다: {data}");
                return;
            }

            // 등록된 클라이언트의 메시지 처리
            ProcessRegisteredClientMessage(data);
        }

        private void ProcessRegisteredClientMessage(string message)
        {
            LogMessage($"등록된 클라이언트 {ClientName} ({ClientId})의 메시지 처리 중: {message}");

            // TO: 명령어 처리 (다른 사용자에게 메시지 전송)
            if (message.StartsWith("TO:"))
            {
                var parts = message.Split(':', 3);
                if (parts.Length == 3)
                {
                    var targetUser = parts[1];
                    var userMessage = parts[2];
                    
                    if (string.IsNullOrWhiteSpace(targetUser))
                    {
                        SendSystemMessage("INVALID_TARGET_USER");
                        LogWarning($"클라이언트 {ClientName}이(가) 비어 있는 대상 사용자로 메시지를 보내려고 시도했습니다");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(userMessage))
                    {
                        SendSystemMessage("EMPTY_MESSAGE");
                        LogWarning($"클라이언트 {ClientName}이(가) 비어 있는 메시지를 {targetUser}에게 보내려고 시도했습니다");
                        return;
                    }

                    var success = _server.SendMessageBetweenClients(ClientName, targetUser, userMessage);
                    if (!success)
                    {
                        SendSystemMessage($"USER_NOT_FOUND:{targetUser}");
                        LogWarning($"{ClientName}의 직접 메시지를 위한 사용자 {targetUser}를 찾을 수 없습니다");
                    }
                    else
                    {
                        LogMessage($"메시지를 {ClientName}에서 {targetUser}로 라우팅했습니다: {userMessage}");
                    }
                    return;
                }
                else
                {
                    SendSystemMessage("INVALID_TO_FORMAT");
                    LogWarning($"클라이언트 {ClientName}의 TO 메시지 형식이 잘못되었습니다: {message}");
                    return;
                }
            }

            // GET_USERS 명령어 처리 (사용자 목록 요청)
            if (message.Equals("GET_USERS", StringComparison.OrdinalIgnoreCase))
            {
                LogSystem($"클라이언트 {ClientName}이(가) 사용자 목록을 요청했습니다");
                _server.SendUserListToClient(ClientName);
                return;
            }

            // GET_CLIENTS 명령어 처리 (클라이언트 목록 요청 - 호환성)
            if (message.Equals("GET_CLIENTS", StringComparison.OrdinalIgnoreCase))
            {
                LogSystem($"클라이언트 {ClientName}이(가) 클라이언트 목록을 요청했습니다 (레거시)");
                var clientList = _server.GetConnectedClientNames();
                var clientListMessage = "CLIENT_LIST:" + string.Join(",", clientList);
                SendData(clientListMessage);
                return;
            }

            // BROADCAST: 명령어 처리
            if (message.StartsWith("BROADCAST:"))
            {
                var broadcastMessage = message[10..];
                if (string.IsNullOrWhiteSpace(broadcastMessage))
                {
                    SendSystemMessage("EMPTY_BROADCAST_MESSAGE");
                    LogWarning($"클라이언트 {ClientName}이(가) 비어 있는 브로드캐스트 메시지를 보내려고 시도했습니다");
                    return;
                }

                var formattedMessage = $"BROADCAST FROM {ClientName}: {broadcastMessage}";
                _server.BroadcastMessage(formattedMessage);
                LogMessage($"클라이언트 {ClientName}의 브로드캐스트 메시지: {broadcastMessage}");
                return;
            }

            // PING 명령어 처리 (연결 상태 확인)
            if (message.Equals("PING", StringComparison.OrdinalIgnoreCase))
            {
                LogHeartbeat($"클라이언트 {ClientName} ({ClientId})로부터 PING 받음");
                SendData("PONG");
                return;
            }

            // WHO 명령어 처리 (자신의 정보 확인)
            if (message.Equals("WHO", StringComparison.OrdinalIgnoreCase))
            {
                LogSystem($"클라이언트 {ClientName}이(가) 자신의 정보를 요청했습니다");
                SendSystemMessage($"YOU_ARE:{ClientName}:ID_{ClientId}");
                return;
            }

            // STATUS 명령어 처리 (서버 상태 확인)
            if (message.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            {
                var connectedCount = _server.ConnectedClientsCount;
                LogSystem($"클라이언트 {ClientName}이(가) 서버 상태를 요청했습니다");
                SendSystemMessage($"SERVER_STATUS:CLIENTS_{connectedCount}:RUNNING");
                return;
            }

            // HELP 명령어 처리
            if (message.Equals("HELP", StringComparison.OrdinalIgnoreCase))
            {
                LogSystem($"클라이언트 {ClientName}이(가) 도움말을 요청했습니다");
                SendHelpMessage();
                return;
            }

            // 일반 메시지는 서버로 전달 (기존 동작 유지)
            _server.OnMessageReceivedFromClient(ClientId, ClientName, message);
        }

        private void SendHelpMessage()
        {
            var helpMessage = @"사용 가능한 명령어:
TO:<사용자명>:<메시지> - 특정 사용자에게 메시지 전송
BROADCAST:<메시지> - 모든 사용자에게 메시지 전송
GET_USERS - 온라인 사용자 목록 가져오기
GET_CLIENTS - 온라인 클라이언트 목록 가져오기 (레거시)
PING - 연결 테스트
WHO - 자신의 사용자 정보 보기
STATUS - 서버 상태 확인
HELP - 이 도움말 메시지 표시";

            SendSystemMessage($"HELP_TEXT:{helpMessage}");
        }

        private void SendSystemMessage(string message)
        {
            try
            {
                var systemMessage = $"SYSTEM:{message}";
                var sendBytes = Encoding.UTF8.GetBytes(systemMessage);
                _networkStream.Write(sendBytes, 0, sendBytes.Length);
                _networkStream.Flush();
                LogSystem($"클라이언트 {ClientName} ({ClientId})에게 시스템 메시지 전송: {message}");
            }
            catch (Exception e)
            {
                LogError($"클라이언트 {ClientId}에게 시스템 메시지 전송 오류: {e.Message}");
                Disconnect();
            }
        }

        public bool SendData(string message)
        {
            if (!IsAlive)
            {
                Disconnect();
                return false;
            }

            try
            {
                var sendBytes = Encoding.UTF8.GetBytes(message);
                _networkStream.Write(sendBytes, 0, sendBytes.Length);
                _networkStream.Flush();
                LogMessage($"클라이언트 {ClientName} ({ClientId})에게 데이터 전송: {message}");
                return true;
            }
            catch (Exception e)
            {
                LogError($"클라이언트 {ClientId} ({ClientName}) ({GetClientEndPoint()})에게 데이터를 전송할 수 없습니다: {e.Message}. 클라이언트 연결이 해제됩니다.");
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// 클라이언트에게 다른 사용자로부터의 메시지 전달
        /// </summary>
        public bool SendUserMessage(string senderName, string message)
        {
            if (!IsAlive || !IsNameRegistered)
            {
                return false;
            }

            var formattedMessage = $"FROM:{senderName}:{message}";
            return SendData(formattedMessage);
        }

        /// <summary>
        /// 클라이언트에게 사용자 목록 전송
        /// </summary>
        public bool SendUserList(string[] userList)
        {
            if (!IsAlive || !IsNameRegistered)
            {
                return false;
            }

            var userListMessage = "SYSTEM:USER_LIST:" + string.Join(",", userList);
            return SendData(userListMessage);
        }

        /// <summary>
        /// 클라이언트 연결 강제 종료 (킥)
        /// </summary>
        public void Kick(string reason = "관리자에 의해 강제 퇴장되었습니다")
        {
            LogClientState($"클라이언트 {ClientName} ({ClientId}) 강제 퇴장 처리: {reason}");
            SendSystemMessage($"KICKED:{reason}");
            
            // 잠시 기다린 후 연결 해제 (메시지가 전송될 시간을 줌)
            System.Threading.Thread.Sleep(100);
            Disconnect();
        }

        public void Disconnect()
        {
            if (!_isConnected) return;
            _isConnected = false;

            try
            {
                _networkStream?.Close();
                _tcpClient?.Close();
            }
            catch (Exception e)
            {
                LogError($"클라이언트 {ClientId} 연결 해제 중 오류: {e.Message}");
            }

            _server.ClientDisconnected(this);
            LogClientState($"클라이언트 {ClientId} ({ClientName})이(가) {GetClientEndPoint()}에서 연결 해제되었습니다");
        }

        public string GetClientEndPoint()
        {
            try
            {
                return _tcpClient?.Client?.RemoteEndPoint?.ToString() ?? "알 수 없음";
            }
            catch
            {
                return "연결 해제됨";
            }
        }

        /// <summary>
        /// 클라이언트 정보를 문자열로 반환
        /// </summary>
        public override string ToString()
        {
            return $"Client[ID:{ClientId}, Name:{ClientName}, Registered:{IsNameRegistered}, Alive:{IsAlive}, EndPoint:{GetClientEndPoint()}]";
        }
    }
}