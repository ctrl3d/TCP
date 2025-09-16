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
        [SerializeField] private string configFileName = "TcpServerConfig.json";
#endif
        
        [Header("Network Settings")] 
        [SerializeField] private string address = "0.0.0.0";
        
        [SerializeField] private ushort port = 7777;
        [SerializeField] private Color logColor = Color.cyan;
        [SerializeField] private bool listenOnStart = true;

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
#if USE_JSONCONFIG
            var tcpServerConfigPath = Path.Combine(Application.dataPath, configFileName);
            var tcpServerConfig = new JsonConfig<TcpServerConfig>(tcpServerConfigPath).GetConfig();
            
            address = tcpServerConfig.address;
            port = tcpServerConfig.port;
            ColorUtility.TryParseHtmlString(tcpServerConfig.logColor, out logColor);
#endif

            _server = new TCPServer(address, port, new TcpServerLogger());

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

        #region Public Methods

        [Button, HorizontalGroup("Server Controls")]
        public void StartServer() => _server?.Start();

        [Button, HorizontalGroup("Server Controls")]
        public void StopServer() => _server?.Stop();

        [Button, Group("Message Controls")]
        public void SendToClientById(int clientId, string message) => _server?.SendMessageToClient(clientId, message);

        [Button, Group("Message Controls")]
        public void SendToClientByName(string clientName, string message) =>
            _server?.SendMessageToClient(clientName, message);

        [Button, Group("Message Controls")]
        public void Broadcast(string message) => _server?.BroadcastMessage(message);

        [Button, HorizontalGroup("Maintenance")]
        public void CleanupConnections() => _server?.CleanUpConnections();

        #endregion

        #region Private Event Handlers (Thread-Safe)

        private void HandleClientConnected(int clientId, string clientName)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"Client connected. ID: {clientId}, Name: {clientName} | Total: {ConnectedClientsCount}");
                onClientConnected?.Invoke(clientId, clientName);
            });
        }

        private void HandleClientDisconnected(int clientId, string clientName)
        {
            _mainThreadActions.Enqueue(() =>
            {
                LogWarning(
                    $"Client disconnected. ID: {clientId}, Name: {clientName} | Total: {ConnectedClientsCount}");
                onClientDisconnected?.Invoke(clientId, clientName);
            });
        }

        private void HandleMessageReceived(int clientId, string clientName, string message)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"Message from client {clientId} ({clientName}): {message}");
                onMessageReceived?.Invoke(clientId, clientName, message);
            });
        }
        
        private void Log(string message)
        {
            Debug.Log($"[{nameof(UnityTCPServer)}] {message}".WithColor(logColor));
        }

        private void LogWarning(string message)
        {
            Debug.LogWarning($"[{nameof(UnityTCPServer)}] {message}".WithColor(logColor));
        }

        private void LogError(string message)
        {
            Debug.LogError($"[{nameof(UnityTCPServer)}] {message}".WithColor(logColor));
        }

        #endregion

        #region Inspector Debug Info

        [Header("Debug Info")] [SerializeField, ReadOnly]
        private bool _isRunning;

        [SerializeField, ReadOnly] private int _connectedClients;

        private void LateUpdate()
        {
            // Inspector에서 실시간 정보 업데이트
            _isRunning = IsRunning;
            _connectedClients = ConnectedClientsCount;
        }
        
        #endregion
    }
}