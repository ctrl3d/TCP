
using System;
using System.Collections.Concurrent;
using System.IO;
using Alchemy.Inspector;
using UnityEngine;
using UnityEngine.Events;
using work.ctrl3d.Config;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class UnityTCPServer : MonoBehaviour
    {
#if USE_JSONCONFIG
        [Header("JsonConfig Settings")] 
        [SerializeField] private bool enableJsonConfig = true;
        [SerializeField] private string configFileName = "TcpServerConfig.json";
#endif
        
        [Header("Network Settings")] 
        [SerializeField] private string address = "0.0.0.0";
        
        [SerializeField] private ushort port = 7777;
        [SerializeField] private string serverName = "";
        [SerializeField] private bool listenOnStart = true;

        [Header("Log Settings")]
        [SerializeField] private bool enableLogging = true;
        [SerializeField] private Color logColor = Color.cyan;
        [SerializeField] private LogLevel logLevel = LogLevel.Info;
        
        [Header("Detail Log Settings")]
        [SerializeField] private bool logConnections = true;
        [SerializeField] private bool logMessages = true;
        [SerializeField] private bool logSystem = true;
        [SerializeField] private bool logErrors = true;
        [SerializeField] private bool logHeartbeat = false;
        [SerializeField] private bool logClientState = true;

        private TcpServerLogger _serverLogger;

        [Header("Unity Events")] 
        public UnityEvent<int, string> onClientConnected; // clientId, clientName
        public UnityEvent<int, string, string> onMessageReceived; // clientId, clientName, message
        public UnityEvent<int, string> onClientDisconnected; // clientId, clientName

        private TCPServer _server;

        // 네트워크 스레드의 이벤트를 메인 스레드에서 처리하기 위한 큐
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();

        #region Public Properties

        public bool IsRunning => _server?.IsRunning ?? false;
        public int ConnectedClientsCount => _server?.ConnectedClientsCount ?? 0;

        #endregion

        #region Unity Lifecycle Methods

        private void Awake()
        {
            _serverLogger = new TcpServerLogger();
            
#if USE_JSONCONFIG
            if (enableJsonConfig)
            {
                var tcpServerConfigPath = Path.Combine(Application.dataPath, configFileName);
                var tcpServerConfig = new JsonConfig<TcpServerConfig>(tcpServerConfigPath).GetConfig();
            
                address = tcpServerConfig.address;
                port = tcpServerConfig.port;
                serverName = tcpServerConfig.serverName;
            
                ColorUtility.TryParseHtmlString(tcpServerConfig.logSettings.logColor, out logColor);
                enableLogging = tcpServerConfig.logSettings.enableLogging;
                logLevel = tcpServerConfig.logSettings.logLevel;
                logConnections = tcpServerConfig.logSettings.logConnections;
                logMessages = tcpServerConfig.logSettings.logMessages;
                logSystem = tcpServerConfig.logSettings.logSystem;
                logErrors = tcpServerConfig.logSettings.logErrors;
                logHeartbeat = tcpServerConfig.logSettings.logHeartbeat;
                logClientState = tcpServerConfig.logSettings.logClientState;
            }
#endif

            // 로거 설정 적용
            _serverLogger.IsLogEnabled = enableLogging;
            _serverLogger.LogColor = logColor;
            _serverLogger.LogLevel = logLevel;

            _server = new TCPServer(address, port, _serverLogger);

            // TCP 서버의 로그 설정 적용
            ApplyLogSettings();
            
            // 이벤트 연결
            _server.OnClientConnected += HandleClientConnected;
            _server.OnClientDisconnected += HandleClientDisconnected;
            _server.OnMessageReceived += HandleMessageReceived;
        }

        private void Start()
        {
            if (listenOnStart)
            {
                StartServer();
            }
        }

        private void Update()
        {
            // 메인 스레드 큐에 쌓인 작업들을 실행
            while (_mainThreadActions.TryDequeue(out var action))
            {
                action.Invoke();
            }
        }

        private void OnDestroy()
        {
            // 이벤트 해제
            if (_server == null) return;
            _server.OnClientConnected -= HandleClientConnected;
            _server.OnClientDisconnected -= HandleClientDisconnected;
            _server.OnMessageReceived -= HandleMessageReceived;
                
            // 서버 종료
            _server.Dispose();
            _server = null;
        }

        #endregion

        #region Log Settings

        /// <summary>
        /// 로그 설정을 TCP 서버에 적용합니다.
        /// </summary>
        private void ApplyLogSettings()
        {
            if (_server == null) return;

            _server.EnableConnectionLogs = logConnections;
            _server.EnableMessageLogs = logMessages;
            _server.EnableSystemLogs = logSystem;
            _server.EnableErrorLogs = logErrors;
            _server.EnableHeartbeatLogs = logHeartbeat;
            _server.EnableClientStateLogs = logClientState;
        }

        /// <summary>
        /// 모든 로그를 활성화합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void EnableAllLogs()
        {
            logConnections = true;
            logMessages = true;
            logSystem = true;
            logErrors = true;
            logHeartbeat = true;
            logClientState = true;

            if (_serverLogger != null)
            {
                _serverLogger.IsLogEnabled = true;
                _serverLogger.LogLevel = LogLevel.Debug;
            }
            
            _server?.EnableAllLogs();
            
            Log("모든 로그가 활성화되었습니다.");
        }

        /// <summary>
        /// 모든 로그를 비활성화합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void DisableAllLogs()
        {
            logConnections = false;
            logMessages = false;
            logSystem = false;
            logErrors = false;
            logHeartbeat = false;
            logClientState = false;

            if (_serverLogger != null)
            {
                _serverLogger.IsLogEnabled = false;
            }

            _server?.DisableAllLogs();
            
            Log("모든 로그가 비활성화되었습니다.");
        }

        /// <summary>
        /// 에러 로그만 활성화합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void EnableErrorLogsOnly()
        {
            logConnections = false;
            logMessages = false;
            logSystem = false;
            logErrors = true;
            logHeartbeat = false;
            logClientState = false;

            if (_serverLogger != null)
            {
                _serverLogger.IsLogEnabled = true;
                _serverLogger.LogLevel = LogLevel.Error;
            }

            _server?.EnableErrorLogsOnly();
            
            Log("에러 로그만 활성화되었습니다.");
        }

        /// <summary>
        /// 하트비트 로그 상태를 토글합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void ToggleHeartbeatLogs()
        {
            logHeartbeat = !logHeartbeat;
            if (_server != null)
            {
                _server.EnableHeartbeatLogs = logHeartbeat;
            }
            
            Log($"하트비트 로그: {(logHeartbeat ? "활성화" : "비활성화")}");
        }

        /// <summary>
        /// 현재 로그 설정을 표시합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void ShowLogSettings()
        {
            var serverLogSettings = _server?.GetLogSettings() ?? "서버가 초기화되지 않았습니다.";
            Log($"로그 설정: 연결({logConnections}), 메시지({logMessages}), 시스템({logSystem}), " +
                $"에러({logErrors}), 하트비트({logHeartbeat}), 클라이언트 상태({logClientState})");
            Log($"서버 로그 설정: {serverLogSettings}");
        }

        #endregion

        #region Public Methods

        [Button, HorizontalGroup("Server Controls")]
        public void StartServer()
        {
            _server?.Start();
            Log($"서버 시작: {address}:{port}");
        }

        [Button, HorizontalGroup("Server Controls")]
        public void StopServer()
        {
            _server?.Stop();
            Log("서버 중지됨");
        }

        [Button, Group("Message Controls")]
        public void SendToClientById(int clientId, string message) => _server?.SendMessageToClient(clientId, message);

        [Button, Group("Message Controls")]
        public void SendToClientByName(string clientName, string message) =>
            _server?.SendMessageToClient(clientName, message);

        [Button, Group("Message Controls")]
        public void Broadcast(string message) => _server?.BroadcastMessage(message);

        [Button, Group("Client-to-Client Messages")]
        public void SendBetweenClients(string senderName, string receiverName, string message) =>
            _server?.SendMessageBetweenClients(senderName, receiverName, message);

        [Button, Group("Client-to-Client Messages")]
        public void SendBetweenClientsById(int senderId, int receiverId, string message) =>
            _server?.SendMessageBetweenClients(senderId, receiverId, message);

        [Button, Group("User List Management")]
        public void SendUserListToClient(string clientName) =>
            _server?.SendUserListToClient(clientName);

        [Button, Group("User List Management")]
        public void SendUserListToClientById(int clientId) =>
            _server?.SendUserListToClient(clientId);

        [Button, Group("User List Management")]
        public void BroadcastUserList() => _server?.BroadcastUserList();

        [Button, Group("Information")]
        public void GetConnectedUsers()
        {
            var users = _server?.GetConnectedClientNames();
            Log(users is { Length: > 0 } ? $"연결된 사용자: {string.Join(", ", users)}" : "연결된 사용자 없음");
        }

        [Button, Group("Information")]
        public void GetClientInfo()
        {
            var clientInfo = _server?.GetClientInfo();
            if (clientInfo != null && clientInfo.Count > 0)
            {
                Log("연결된 클라이언트:");
                foreach (var kvp in clientInfo)
                {
                    Log($"  ID: {kvp.Key}, 이름: {kvp.Value}");
                }
            }
            else
            {
                Log("연결된 클라이언트 없음");
            }
        }

        [Button, HorizontalGroup("Maintenance")]
        public void CleanupConnections() => _server?.CleanUpConnections();

        /// <summary>
        /// 특정 클라이언트에게 시스템 메시지 전송
        /// </summary>
        public void SendSystemMessageToClient(string clientName, string systemMessage)
        {
            _server?.SendMessageToClient(clientName, $"SYSTEM:{systemMessage}");
        }

        /// <summary>
        /// 모든 클라이언트에게 시스템 메시지 브로드캐스트
        /// </summary>
        public void BroadcastSystemMessage(string systemMessage)
        {
            _server?.BroadcastMessage($"SYSTEM:{systemMessage}");
        }

        /// <summary>
        /// 특정 클라이언트가 온라인인지 확인
        /// </summary>
        public bool IsClientOnline(string clientName)
        {
            var connectedClients = _server?.GetConnectedClientNames();
            return connectedClients != null && Array.Exists(connectedClients, name => name == clientName);
        }

        /// <summary>
        /// 클라이언트 연결 강제 해제
        /// </summary>
        [Button, Group("Admin Controls")]
        public void KickClient(string clientName)
        {
            if (IsClientOnline(clientName))
            {
                SendSystemMessageToClient(clientName, "KICKED: Kicked by administrator");
                Log($"클라이언트 {clientName} 강제 퇴장 처리되었습니다.");
            }
            else
            {
                LogWarning($"클라이언트 {clientName}이(가) 온라인 상태가 아닙니다.");
            }
        }

        /// <summary>
        /// 서버 상태 정보 출력
        /// </summary>
        [Button, Group("Information")]
        public void ShowServerStatus()
        {
            var status = _server?.GetStatusInfo() ?? "서버가 초기화되지 않았습니다.";
            Log($"서버 상태: {status}");
            
            var clients = _server?.GetConnectedClientNames();
            if (clients != null && clients.Length > 0)
            {
                Log($"클라이언트 목록: {string.Join(", ", clients)}");
            }
            else
            {
                Log("연결된 클라이언트가 없습니다.");
            }
        }

        #endregion

        #region Private Event Handlers (Thread-Safe)

        private void HandleClientConnected(int clientId, string clientName)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"클라이언트 연결됨. ID: {clientId}, 이름: {clientName} | 총 접속자: {ConnectedClientsCount}");
                onClientConnected?.Invoke(clientId, clientName);
                
                // 새로운 클라이언트에게 환영 메시지 전송 (선택사항)
                _server?.SendMessageToClient(clientName, $"Welcome to the server, {clientName}!");
                
                // 모든 클라이언트에게 업데이트된 사용자 목록 브로드캐스트 (선택사항)
                _server?.BroadcastUserList();
            });
        }

        private void HandleClientDisconnected(int clientId, string clientName)
        {
            _mainThreadActions.Enqueue(() =>
            {
                LogWarning(
                    $"클라이언트 연결 해제됨. ID: {clientId}, 이름: {clientName} | 총 접속자: {ConnectedClientsCount}");
                onClientDisconnected?.Invoke(clientId, clientName);
                
                // 모든 클라이언트에게 업데이트된 사용자 목록 브로드캐스트 (선택사항)
                _server?.BroadcastUserList();
            });
        }

        private void HandleMessageReceived(int clientId, string clientName, string message)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"메시지 수신 (ID: {clientId}, 이름: {clientName}): {message}");
                onMessageReceived?.Invoke(clientId, clientName, message);
            });
        }
        
        #endregion
        
        #region Logging

        private void Log(string message)
        {
            if (!enableLogging || logLevel < LogLevel.Info) return;
            Debug.Log($"[{serverName}] {message}".WithColor(logColor));
        }

        private void LogWarning(string message)
        {
            if (!enableLogging || logLevel < LogLevel.Warning) return;
            Debug.LogWarning($"[{nameof(UnityTCPServer)}] {message}".WithColor(logColor));
        }

        private void LogError(string message)
        {
            if (!enableLogging || !logErrors || logLevel < LogLevel.Error) return;
            Debug.LogError($"[{nameof(UnityTCPServer)}] {message}".WithColor(logColor));
        }

        #endregion

        #region Inspector Debug Info

        [Header("Debug Info")] [SerializeField, ReadOnly]
        private bool _isRunning;

        [SerializeField, ReadOnly] private int _connectedClients;

        [SerializeField, ReadOnly] private string[] _connectedClientNames = Array.Empty<string>();

        private void LateUpdate()
        {
            // Inspector에서 실시간 정보 업데이트
            _isRunning = IsRunning;
            _connectedClients = ConnectedClientsCount;
            _connectedClientNames = _server?.GetConnectedClientNames() ?? Array.Empty<string>();
        }
        
        #endregion

        #region Public API for External Scripts

        /// <summary>
        /// 외부 스크립트에서 클라이언트 간 메시지 전송을 위한 API
        /// </summary>
        public bool SendClientToClientMessage(string fromClient, string toClient, string message)
        {
            if (_server == null || !IsRunning)
            {
                LogError("서버가 실행중이 아닙니다.");
                return false;
            }

            return _server.SendMessageBetweenClients(fromClient, toClient, message);
        }

        /// <summary>
        /// 외부 스크립트에서 브로드캐스트 메시지 전송을 위한 API
        /// </summary>
        public void SendBroadcastMessage(string message)
        {
            if (_server == null || !IsRunning)
            {
                LogError("서버가 실행중이 아닙니다.");
                return;
            }

            _server.BroadcastMessage(message);
        }

        /// <summary>
        /// 연결된 클라이언트 목록을 반환하는 API
        /// </summary>
        public string[] GetConnectedClientNamesList()
        {
            return _server?.GetConnectedClientNames() ?? Array.Empty<string>();
        }

        /// <summary>
        /// 특정 클라이언트가 온라인 상태인지 확인하는 API
        /// </summary>
        public bool CheckClientOnlineStatus(string clientName)
        {
            return IsClientOnline(clientName);
        }

        /// <summary>
        /// 현재 서버의 상태 정보를 반환하는 API
        /// </summary>
        public string GetServerStatusInfo()
        {
            return _server?.GetStatusInfo() ?? "서버가 초기화되지 않았습니다.";
        }

        #endregion
    }
}