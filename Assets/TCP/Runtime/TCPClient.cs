using System;
using System.Net.Sockets;
using System.Text;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class TCPClient : IDisposable
    {
        private readonly string _address;
        private readonly ushort _port;
        private readonly ILogger _logger;

        private TcpClient _tcpClient;
        private NetworkStream _networkStream;
        private byte[] _receiveBuffer;
        private bool _disposed;
        private bool _isNameRegistered;

        public event Action OnConnected;
        public event Action<string> OnMessageReceived;
        public event Action<string> OnSystemMessageReceived;
        public event Action OnDisconnected;
        public event Action<string> OnNameRegistered;
        public event Action OnNameTaken;
        public event Action<string[]> OnClientListReceived;
        public event Action<string, string> OnDirectMessageReceived; // sender, message
        public event Action<string> OnConnectionFailed; // 연결 실패 이벤트
        public event Action<string> OnKicked; // 킥 당했을 때 이벤트

        public bool IsConnected => _tcpClient is { Connected: true };
        public bool IsNameRegistered => _isNameRegistered;
        public string ClientName { get; }
        public bool IsConnecting { get; private set; } // 연결 시도 중 상태

        // 로그 제어 프로퍼티
        public bool EnableConnectionLogs { get; set; } = true;
        public bool EnableMessageLogs { get; set; } = true;
        public bool EnableSystemLogs { get; set; } = true;
        public bool EnableErrorLogs { get; set; } = true;
        public bool EnableHeartbeatLogs { get; set; } = false;

        public TCPClient(string address, ushort port, string clientName = null, ILogger logger = null)
        {
            _address = address;
            _port = port;
            _logger = logger ?? new ConsoleLogger();
            ClientName = clientName ?? string.Empty;
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

        #endregion

        #region Public Methods

        public void ConnectToServer()
        {
            if (IsConnected)
            {
                LogWarning("Already connected to the server.");
                return;
            }

            if (IsConnecting)
            {
                LogWarning("Connection attempt already in progress.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            try
            {
                IsConnecting = true;
                _tcpClient = new TcpClient();
                
                // 연결 타임아웃 설정
                _tcpClient.ReceiveTimeout = 10000; // 10초
                _tcpClient.SendTimeout = 10000; // 10초
                
                _tcpClient.BeginConnect(_address, _port, ConnectCallback, null);
                LogConnection($"Attempting to connect to {_address}:{_port}...");
            }
            catch (Exception e)
            {
                IsConnecting = false;
                LogError($"Error connecting to server: {e.Message}");
                OnConnectionFailed?.Invoke(e.Message);
                Disconnect();
            }
        }

        public void Send(string message)
        {
            if (!IsConnected)
            {
                LogError("Not connected to the server. Cannot send message.");
                return;
            }

            if (!_isNameRegistered)
            {
                LogError("Name not registered yet. Cannot send regular messages.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            SendRawMessage(message);
        }

        /// <summary>
        /// 원시 메시지 전송 (내부 사용)
        /// </summary>
        private void SendRawMessage(string message)
        {
            try
            {
                var sendBytes = Encoding.UTF8.GetBytes(message);
                _networkStream.WriteAsync(sendBytes, 0, sendBytes.Length);
                LogMessage($"Sent: {message}");
            }
            catch (Exception e)
            {
                LogError($"Error sending data: {e.Message}");
                Disconnect();
            }
        }
        
        /// <summary>
        /// 특정 클라이언트에게 메시지 전송
        /// </summary>
        public void SendToClient(string targetClientName, string message)
        {
            if (!IsConnected)
            {
                LogError("Not connected to the server. Cannot send message.");
                return;
            }

            if (!_isNameRegistered)
            {
                LogError("Name not registered yet. Cannot send messages.");
                return;
            }

            if (string.IsNullOrWhiteSpace(targetClientName))
            {
                LogError("Target client name cannot be empty.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            var formattedMessage = $"TO:{targetClientName}:{message}";
            SendRawMessage(formattedMessage);
        }

        /// <summary>
        /// 모든 클라이언트에게 브로드캐스트 메시지 전송
        /// </summary>
        public void Broadcast(string message)
        {
            if (!IsConnected)
            {
                LogError("Not connected to the server. Cannot send message.");
                return;
            }

            if (!_isNameRegistered)
            {
                LogError("Name not registered yet. Cannot send messages.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            var formattedMessage = $"BROADCAST:{message}";
            SendRawMessage(formattedMessage);
        }

        /// <summary>
        /// 연결된 클라이언트 목록 요청
        /// </summary>
        public void RequestClientList()
        {
            if (!IsConnected)
            {
                LogError("Not connected to the server. Cannot request client list.");
                return;
            }

            if (!_isNameRegistered)
            {
                LogError("Name not registered yet. Cannot request client list.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            SendRawMessage("GET_CLIENTS");
        }

        /// <summary>
        /// 사용자 목록 요청 (서버의 새로운 명령어 지원)
        /// </summary>
        public void RequestUserList()
        {
            if (!IsConnected)
            {
                LogError("Not connected to the server. Cannot request user list.");
                return;
            }

            if (!_isNameRegistered)
            {
                LogError("Name not registered yet. Cannot request user list.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            SendRawMessage("GET_USERS");
        }

        /// <summary>
        /// 서버에 PING 전송
        /// </summary>
        public void Ping()
        {
            if (!IsConnected)
            {
                LogError("Not connected to the server. Cannot ping.");
                return;
            }

            if (!_isNameRegistered)
            {
                LogError("Name not registered yet. Cannot ping.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            SendRawMessage("PING");
            LogHeartbeat("PING sent to server");
        }

        /// <summary>
        /// 연결 상태 확인 (하트비트)
        /// </summary>
        public bool TestConnection()
        {
            if (!IsConnected) return false;

            try
            {
                // 소켓 상태를 확인
                var isAlive = !(_tcpClient.Client.Poll(1, SelectMode.SelectRead) && _tcpClient.Client.Available == 0);
                LogHeartbeat($"Connection test result: {isAlive}");
                return isAlive;
            }
            catch (Exception e)
            {
                LogError($"Connection test failed: {e.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            if (!IsConnected && _tcpClient == null && !IsConnecting) return;

            LogConnection("Disconnecting from the server.");
            IsConnecting = false;

            try
            {
                _networkStream?.Close();
                _tcpClient?.Close();
            }
            catch (Exception e)
            {
                LogError($"Error during disconnect: {e.Message}");
            }

            _networkStream = null;
            _tcpClient = null;
            _isNameRegistered = false;

            OnDisconnected?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            Disconnect();
            ClearAllEvents();
            GC.SuppressFinalize(this);
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

        #region Private Callbacks

        private void ConnectCallback(IAsyncResult result)
        {
            try
            {
                _tcpClient.EndConnect(result);
                IsConnecting = false;
                
                if (!_tcpClient.Connected)
                {
                    LogError("Failed to connect to the server.");
                    OnConnectionFailed?.Invoke("Connection failed");
                    Disconnect();
                    return;
                }

                _networkStream = _tcpClient.GetStream();
                _receiveBuffer = new byte[_tcpClient.ReceiveBufferSize];
                _networkStream.BeginRead(_receiveBuffer, 0, _receiveBuffer.Length, ReceiveCallback, null);

                LogConnection("Successfully connected to the server!");
                OnConnected?.Invoke();
            }
            catch (Exception e)
            {
                IsConnecting = false;
                LogError($"Error in connection callback: {e.Message}");
                OnConnectionFailed?.Invoke(e.Message);
                Disconnect();
            }
        }

        private void ReceiveCallback(IAsyncResult result)
        {
            try
            {
                if (!IsConnected) return;

                int bytesRead = _networkStream.EndRead(result);
                if (bytesRead <= 0)
                {
                    LogConnection("Server has closed the connection.");
                    Disconnect();
                    return;
                }

                var receivedMessage = Encoding.UTF8.GetString(_receiveBuffer, 0, bytesRead);
                LogMessage($"Received: {receivedMessage}");

                ProcessMessage(receivedMessage);

                if (IsConnected && _networkStream != null && _networkStream.CanRead)
                {
                    _networkStream.BeginRead(_receiveBuffer, 0, _receiveBuffer.Length, ReceiveCallback, null);
                }
            }
            catch (Exception e)
            {
                LogError($"Error receiving data: {e.Message}");
                Disconnect();
            }
        }

        private void ProcessMessage(string message)
        {
            // 시스템 메시지 처리
            if (message.StartsWith("SYSTEM:"))
            {
                var systemMessage = message[7..];
                LogSystem($"System message received: {systemMessage}");
                OnSystemMessageReceived?.Invoke(systemMessage);

                // 시스템 메시지별 처리
                ProcessSystemMessage(systemMessage);
                return;
            }
            
            // Direct Message 처리
            if (message.StartsWith("FROM:"))
            {
                var parts = message.Split(':', 3);
                if (parts.Length == 3)
                {
                    var senderName = parts[1];
                    var directMessage = parts[2];
                    LogMessage($"Direct message from {senderName}: {directMessage}");
                    OnDirectMessageReceived?.Invoke(senderName, directMessage);
                    return;
                }
            }

            // PONG 응답 처리
            if (message.Equals("PONG", StringComparison.OrdinalIgnoreCase))
            {
                LogHeartbeat("Received PONG from server");
                return;
            }

            // 브로드캐스트 메시지 처리
            if (message.StartsWith("BROADCAST FROM "))
            {
                LogMessage($"Broadcast message: {message}");
                OnMessageReceived?.Invoke(message);
                return;
            }

            // 일반 메시지 처리
            LogMessage($"Regular message: {message}");
            OnMessageReceived?.Invoke(message);
        }

        private void ProcessSystemMessage(string systemMessage)
        {
            switch (systemMessage)
            {
                case "REGISTER_NAME":
                    // 서버가 이름 등록을 요청하면 자동으로 등록
                    if (!string.IsNullOrWhiteSpace(ClientName))
                    {
                        SendRawMessage($"NAME:{ClientName}");
                        LogSystem($"Auto-registering name: {ClientName}");
                    }
                    else
                    {
                        LogWarning("No client name provided for registration.");
                    }
                    break;

                case "NAME_REGISTERED":
                    _isNameRegistered = true;
                    LogSystem($"Name '{ClientName}' registered successfully!");
                    OnNameRegistered?.Invoke(ClientName);
                    break;

                case "NAME_TAKEN":
                    LogWarning($"Name '{ClientName}' is already taken!");
                    OnNameTaken?.Invoke();
                    break;

                case "REGISTER_NAME_FIRST":
                    LogWarning("Must register name before sending messages.");
                    break;

                default:
                    // 특수 시스템 메시지 처리
                    ProcessSpecialSystemMessage(systemMessage);
                    break;
            }
        }

        private void ProcessSpecialSystemMessage(string systemMessage)
        {
            // 클라이언트 목록 응답 처리
            if (systemMessage.StartsWith("CLIENT_LIST:"))
            {
                var clientListStr = systemMessage[12..];
                var clientList = string.IsNullOrEmpty(clientListStr) ? Array.Empty<string>() : clientListStr.Split(',');
                LogSystem($"Received client list: {string.Join(", ", clientList)}");
                OnClientListReceived?.Invoke(clientList);
                return;
            }

            // 사용자 목록 응답 처리
            if (systemMessage.StartsWith("USER_LIST:"))
            {
                var userListStr = systemMessage[10..];
                var userList = string.IsNullOrEmpty(userListStr) ? Array.Empty<string>() : userListStr.Split(',');
                LogSystem($"Received user list: {string.Join(", ", userList)}");
                OnClientListReceived?.Invoke(userList); // 같은 이벤트 사용
                return;
            }

            // 사용자를 찾을 수 없음
            if (systemMessage.StartsWith("USER_NOT_FOUND:"))
            {
                var notFoundUser = systemMessage[15..];
                LogWarning($"User not found: {notFoundUser}");
                return;
            }

            // 자신의 정보 응답
            if (systemMessage.StartsWith("YOU_ARE:"))
            {
                LogSystem($"Server info: {systemMessage}");
                return;
            }

            // 서버 상태 응답
            if (systemMessage.StartsWith("SERVER_STATUS:"))
            {
                LogSystem($"Server status: {systemMessage}");
                return;
            }

            // 도움말 텍스트
            if (systemMessage.StartsWith("HELP_TEXT:"))
            {
                var helpText = systemMessage[10..];
                LogSystem($"Server help:\n{helpText}");
                return;
            }

            // 킥 메시지 처리 (두 가지 형태 모두 지원)
            if (systemMessage.StartsWith("KICKED"))
            {
                var reason = "Kicked by administrator";
                
                // KICKED:reason 형태인 경우 이유 추출
                if (systemMessage.StartsWith("KICKED:") && systemMessage.Length > 7)
                {
                    reason = systemMessage[7..];
                }
                
                LogWarning($"Kicked from server: {reason}");
                OnKicked?.Invoke(reason);
                Disconnect();
                return;
            }

            // 기타 에러 메시지들
            if (systemMessage == "INVALID_TARGET_USER")
            {
                LogError("Invalid target user specified.");
                return;
            }

            if (systemMessage == "EMPTY_MESSAGE")
            {
                LogError("Empty message cannot be sent.");
                return;
            }

            if (systemMessage == "INVALID_TO_FORMAT")
            {
                LogError("Invalid TO: message format.");
                return;
            }

            if (systemMessage == "EMPTY_BROADCAST_MESSAGE")
            {
                LogError("Empty broadcast message cannot be sent.");
                return;
            }

            // 알려지지 않은 시스템 메시지
            LogSystem($"Unknown system message: {systemMessage}");
        }

        private void ClearAllEvents()
        {
            OnConnected = null;
            OnMessageReceived = null;
            OnSystemMessageReceived = null;
            OnDisconnected = null;
            OnNameRegistered = null;
            OnNameTaken = null;
            OnClientListReceived = null;
            OnDirectMessageReceived = null;
            OnConnectionFailed = null;
            OnKicked = null;
        }

        ~TCPClient()
        {
            Dispose();
        }

        #endregion

        #region Public Properties for Status

        /// <summary>
        /// 클라이언트 상태 정보를 문자열로 반환
        /// </summary>
        public string GetStatusInfo()
        {
            return $"TCPClient[Address:{_address}:{_port}, Connected:{IsConnected}, " +
                   $"Connecting:{IsConnecting}, NameRegistered:{IsNameRegistered}, " +
                   $"ClientName:{ClientName}, Disposed:{_disposed}]";
        }

        /// <summary>
        /// 연결 정보를 문자열로 반환
        /// </summary>
        public string GetConnectionInfo()
        {
            if (!IsConnected) return "Not connected";
            
            try
            {
                return $"Connected to {_tcpClient?.Client?.RemoteEndPoint}";
            }
            catch
            {
                return "Connection info unavailable";
            }
        }

        /// <summary>
        /// 로그 설정 정보를 문자열로 반환
        /// </summary>
        public string GetLogSettings()
        {
            return $"LogSettings[Connection:{EnableConnectionLogs}, Message:{EnableMessageLogs}, " +
                   $"System:{EnableSystemLogs}, Error:{EnableErrorLogs}, Heartbeat:{EnableHeartbeatLogs}]";
        }

        #endregion
    }
}