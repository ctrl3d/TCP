using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Alchemy.Inspector;
using UnityEngine;
using work.ctrl3d.Config;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class UnityTcpServer : MonoBehaviour
    {
#if USE_JSONCONFIG
        [Header("JsonConfig Settings")] 
        [SerializeField] private bool enableJsonConfig = true;
        [SerializeField] private string configFileName = "TcpServerConfig.json";
#endif

        [Header("Network Settings")] 
        [SerializeField] private string address = "0.0.0.0"; 
        [SerializeField] private int port = 7777; 
        [SerializeField] private string serverName = "MyServer";
        [SerializeField] private bool listenOnStart = true;

        [Header("Log Settings")]
        [SerializeField] private bool enableLogging = true;
        [SerializeField] private Color logColor = Color.cyan;
        
        [Header("Detail Log Settings")]
        [SerializeField] private LogFilter logFilter = LogFilter.All;
    
        private TcpLogger _activeLogger;
    
        // [Review Fix] UnityEvent 제거, 순수 C# 이벤트 사용
        public event Action<int, string> OnClientConnected;
        public event Action<int, string, string> OnMessageReceived;
        public event Action<int, string> OnClientDisconnected;

        private TcpServer _server;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();
        private readonly Dictionary<int, string> _clientMap = new();

        public bool IsRunning => _server?.IsRunning ?? false;
        public int ConnectedClientsCount => _clientMap.Count;

        private void Awake()
        {
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
            }
#endif
            InitializeServer();
        }

        private void InitializeServer()
        {
            LogFilter initialFilter = enableLogging ? logFilter : LogFilter.None;
            _activeLogger = new TcpLogger($"[{serverName}]", initialFilter, logColor);

            _server = new TcpServer(port, _activeLogger);
    
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
            while (_mainThreadActions.TryDequeue(out var action))
            {
                action.Invoke();
            }
        }

        private void OnDestroy()
        {
            if (_server == null) return;
            _server.Dispose();
            _server = null;
        }

        #region Log Settings

        [Button, Group("Logging Controls")]
        public void EnableAllLogs()
        {
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.All;
            logFilter = LogFilter.All;
            Log("모든 로그가 활성화되었습니다.");
        }

        [Button, Group("Logging Controls")]
        public void DisableAllLogs()
        {
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.None;
            logFilter = LogFilter.None;
            Log("모든 로그가 비활성화되었습니다.");
        }

        [Button, Group("Logging Controls")]
        public void EnableErrorLogsOnly()
        {
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.Error;
            logFilter = LogFilter.Error;
            Log("에러 로그만 활성화되었습니다.");
        }

        #endregion

        #region Public Methods

        [Button, HorizontalGroup("Server Controls")]
        public void StartServer()
        {
            _server?.Start();
        }

        [Button, HorizontalGroup("Server Controls")]
        public void StopServer()
        {
            _server?.Stop();
            _clientMap.Clear();
        }

        [Button, Group("Message Controls")]
        public void SendToClientById(int clientId, string message)
        {
            _server?.SendToClient(clientId, message);
        }

        [Button, Group("Message Controls")]
        public void SendToClientByName(string clientName, string message)
        {
            var target = _clientMap.FirstOrDefault(x => x.Value == clientName);
            if (target.Value != null)
            {
                _server?.SendToClient(target.Key, message);
            }
            else
            {
                LogWarning($"클라이언트를 찾을 수 없음: {clientName}");
            }
        }

        [Button, Group("Message Controls")]
        public void Broadcast(string message) => _server?.Broadcast(message);

        [Button, Group("Client-to-Client Messages")]
        public void SendBetweenClients(string senderName, string receiverName, string message)
        {
            SendToClientByName(receiverName, $"{TcpProtocol.CmdTo}:{senderName}:{message}");
        }

        [Button, Group("Client-to-Client Messages")]
        public void SendBetweenClientsById(int senderId, int receiverId, string message)
        {
            if (_clientMap.TryGetValue(senderId, out var senderName))
            {
                _server?.SendToClient(receiverId, $"{TcpProtocol.CmdTo}:{senderName}:{message}");
            }
        }

        [Button, Group("User List Management")]
        public void SendUserListToClient(string clientName)
        {
            var userListStr = string.Join(",", _clientMap.Values);
            SendToClientByName(clientName, $"{TcpProtocol.SystemUserList}:{userListStr}");
        }

        [Button, Group("User List Management")]
        public void SendUserListToClientById(int clientId)
        {
            var userListStr = string.Join(",", _clientMap.Values);
            _server?.SendToClient(clientId, $"{TcpProtocol.SystemUserList}:{userListStr}");
        }

        [Button, Group("User List Management")]
        public void BroadcastUserList()
        {
            var userListStr = string.Join(",", _clientMap.Values);
            _server?.Broadcast($"{TcpProtocol.SystemUserList}:{userListStr}");
        }

        [Button, Group("Information")]
        public void GetConnectedUsers()
        {
            var users = _clientMap.Values.ToArray();
            Log(users.Length > 0 ? $"연결된 사용자: {string.Join(", ", users)}" : "연결된 사용자 없음");
        }

        [Button, Group("Information")]
        public void GetClientInfo()
        {
            if (_clientMap.Count > 0)
            {
                Log("연결된 클라이언트:");
                foreach (var kvp in _clientMap)
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
        public void CleanupConnections() 
        {
            Log("CleanUp은 TcpServer 내부에서 자동으로 처리됩니다.");
        }

        public void SendSystemMessageToClient(string clientName, string systemMessage)
        {
            SendToClientByName(clientName, $"SYSTEM:{systemMessage}");
        }

        public void BroadcastSystemMessage(string systemMessage)
        {
            _server?.Broadcast($"SYSTEM:{systemMessage}");
        }

        public bool IsClientOnline(string clientName)
        {
            return _clientMap.ContainsValue(clientName);
        }

        [Button, Group("Admin Controls")]
        public void KickClient(string clientName)
        {
            if (IsClientOnline(clientName))
            {
                SendSystemMessageToClient(clientName, "KICKED: Kicked by administrator");
                Log($"클라이언트 {clientName}에게 퇴장 메시지를 전송했습니다.");
            }
            else
            {
                LogWarning($"클라이언트 {clientName}이(가) 온라인 상태가 아닙니다.");
            }
        }

        [Button, Group("Information")]
        public void ShowServerStatus()
        {
            var status = IsRunning ? "Running" : "Stopped";
            Log($"서버 상태: {status} | 접속자 수: {ConnectedClientsCount}");
    
            if (_clientMap.Count > 0)
            {
                Log($"클라이언트 목록: {string.Join(", ", _clientMap.Values)}");
            }
        }

        #endregion

        #region Private Event Handlers

        private void HandleClientConnected(int clientId, string clientName)
        {
            _mainThreadActions.Enqueue(() =>
            {
                _clientMap[clientId] = clientName;
                _activeLogger?.Log(LogFilter.Connection, $"클라이언트 연결됨. ID: {clientId}, 이름: {clientName}");
                OnClientConnected?.Invoke(clientId, clientName);
                _server?.SendToClient(clientId, $"Welcome to the server, {clientName}!");
                BroadcastUserList();
            });
        }

        private void HandleClientDisconnected(int clientId, string clientName)
        {
            _mainThreadActions.Enqueue(() => 
            {
                if (_clientMap.ContainsKey(clientId))
                    _clientMap.Remove(clientId);

                _activeLogger?.LogWarning(LogFilter.Connection, $"클라이언트 연결 해제됨. ID: {clientId}, 이름: {clientName}");
                OnClientDisconnected?.Invoke(clientId, clientName);
                BroadcastUserList();
            });
        }

        private void HandleMessageReceived(int clientId, string clientName, string message)
        {
            _mainThreadActions.Enqueue(() =>
            {
                _activeLogger?.Log(LogFilter.Message, $"메시지 수신 (ID: {clientId}, 이름: {clientName}): {message}");
                OnMessageReceived?.Invoke(clientId, clientName, message);
            });
        }

        #endregion

        #region Logging Helpers

        private void Log(string message)
        {
            _activeLogger?.Log(LogFilter.System, message);
        }

        private void LogWarning(string message)
        {
            _activeLogger?.LogWarning(LogFilter.System, message);
        }

        public bool SendClientToClientMessage(string fromClient, string toClient, string message)
        {
            if (_server == null || !IsRunning) return false;
            SendBetweenClients(fromClient, toClient, message);
            return true;
        }

        public void SendBroadcastMessage(string message)
        {
            if (_server == null || !IsRunning) return;
            _server.Broadcast(message);
        }

        public string[] GetConnectedClientNamesList()
        {
            return _clientMap.Values.ToArray();
        }

        public bool CheckClientOnlineStatus(string clientName)
        {
            return IsClientOnline(clientName);
        }

        public string GetServerStatusInfo()
        {
            return $"Running: {IsRunning}, Clients: {ConnectedClientsCount}";
        }

        #endregion

        #region Inspector Debug Info

        [Header("Debug Info")] 
        [SerializeField, ReadOnly] private bool _isRunning;
        [SerializeField, ReadOnly] private int _connectedClientsCount;
        [SerializeField, ReadOnly] private string[] _connectedClientNames = Array.Empty<string>();

        private void LateUpdate()
        {
            _isRunning = IsRunning;
            _connectedClientsCount = ConnectedClientsCount;
            _connectedClientNames = _clientMap.Values.ToArray();
        }

        #endregion
    }
}