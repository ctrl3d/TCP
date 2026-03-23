using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using work.ctrl3d.Config;
using work.ctrl3d.Logger;

#if USE_UNITASK
using Cysharp.Threading.Tasks;
#endif

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
    
        public event Action<int, string> OnClientConnected;
        public event Action<int, string, string> OnMessageReceived;
        public event Action<int, string> OnClientDisconnected;

        private TcpServer _server;
        
        private enum ServerEventType
        {
            ClientConnected,
            ClientDisconnected,
            MessageReceived
        }

        private struct ServerEventData
        {
            public ServerEventType Type;
            public int ClientId;
            public string ClientName;
            public string Message;
        }

        private readonly ConcurrentQueue<ServerEventData> _eventQueue = new();
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
            while (_eventQueue.TryDequeue(out var ev))
            {
                switch (ev.Type)
                {
                    case ServerEventType.ClientConnected:
                        _clientMap[ev.ClientId] = ev.ClientName;
                        _activeLogger?.Log(LogFilter.Connection, $"클라이언트 연결됨. ID: {ev.ClientId}, 이름: {ev.ClientName}");
                        OnClientConnected?.Invoke(ev.ClientId, ev.ClientName);
                        _server?.SendToClient(ev.ClientId, $"Welcome to the server, {ev.ClientName}!");
                        BroadcastUserList();
                        _connectedClientNames = _clientMap.Values.ToArray();
                        break;

                    case ServerEventType.ClientDisconnected:
                        if (_clientMap.ContainsKey(ev.ClientId))
                            _clientMap.Remove(ev.ClientId);
                        _activeLogger?.LogWarning(LogFilter.Connection, $"클라이언트 연결 해제됨. ID: {ev.ClientId}, 이름: {ev.ClientName}");
                        OnClientDisconnected?.Invoke(ev.ClientId, ev.ClientName);
                        BroadcastUserList();
                        _connectedClientNames = _clientMap.Values.ToArray();
                        break;

                    case ServerEventType.MessageReceived:
                        _activeLogger?.Log(LogFilter.Message, $"메시지 수신 (ID: {ev.ClientId}, 이름: {ev.ClientName}): {ev.Message}");
                        OnMessageReceived?.Invoke(ev.ClientId, ev.ClientName, ev.Message);
                        break;
                }
            }

            _isRunning = IsRunning;
            _connectedClientsCount = ConnectedClientsCount;
        }

        private void OnDestroy()
        {
            if (_server == null) return;
            _server.Dispose();
            _server = null;
        }

        #region Log Settings

        public void EnableAllLogs()
        {
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.All;
            logFilter = LogFilter.All;
            Log("모든 로그가 활성화되었습니다.");
        }

        public void DisableAllLogs()
        {
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.None;
            logFilter = LogFilter.None;
            Log("모든 로그가 비활성화되었습니다.");
        }

        public void EnableErrorLogsOnly()
        {
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.Error;
            logFilter = LogFilter.Error;
            Log("에러 로그만 활성화되었습니다.");
        }

        #endregion

        #region Public Methods

        public void StartServer()
        {
            _server?.Start();
        }

        public void StopServer()
        {
            _server?.Stop();
            _clientMap.Clear();
        }

        public void SendToClientById(int clientId, string message)
        {
            _server?.SendToClient(clientId, message);
        }

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

        public void Broadcast(string message) => _server?.Broadcast(message);

        public void SendBetweenClients(string senderName, string receiverName, string message)
        {
            SendToClientByName(receiverName, $"{TcpProtocol.CmdTo}:{senderName}:{message}");
        }

        public void SendBetweenClientsById(int senderId, int receiverId, string message)
        {
            if (_clientMap.TryGetValue(senderId, out var senderName))
            {
                _server?.SendToClient(receiverId, $"{TcpProtocol.CmdTo}:{senderName}:{message}");
            }
        }

        public void SendUserListToClient(string clientName)
        {
            var userListStr = string.Join(",", _clientMap.Values);
            SendToClientByName(clientName, $"{TcpProtocol.SystemUserList}:{userListStr}");
        }

        public void SendUserListToClientById(int clientId)
        {
            var userListStr = string.Join(",", _clientMap.Values);
            _server?.SendToClient(clientId, $"{TcpProtocol.SystemUserList}:{userListStr}");
        }

        public void BroadcastUserList()
        {
            var userListStr = string.Join(",", _clientMap.Values);
            _server?.Broadcast($"{TcpProtocol.SystemUserList}:{userListStr}");
        }

        public void GetConnectedUsers()
        {
            var users = _clientMap.Values.ToArray();
            Log(users.Length > 0 ? $"연결된 사용자: {string.Join(", ", users)}" : "연결된 사용자 없음");
        }

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
            _eventQueue.Enqueue(new ServerEventData
            {
                Type = ServerEventType.ClientConnected,
                ClientId = clientId,
                ClientName = clientName
            });
        }

        private void HandleClientDisconnected(int clientId, string clientName)
        {
            _eventQueue.Enqueue(new ServerEventData
            {
                Type = ServerEventType.ClientDisconnected,
                ClientId = clientId,
                ClientName = clientName
            });
        }

        private void HandleMessageReceived(int clientId, string clientName, string message)
        {
            _eventQueue.Enqueue(new ServerEventData
            {
                Type = ServerEventType.MessageReceived,
                ClientId = clientId,
                ClientName = clientName,
                Message = message
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

        #endregion

        #region Inspector Debug Info

        [Header("Debug Info")] 
        [SerializeField] private bool _isRunning;
        [SerializeField] private int _connectedClientsCount;
        [SerializeField] private string[] _connectedClientNames = Array.Empty<string>();

        #endregion
    }
}