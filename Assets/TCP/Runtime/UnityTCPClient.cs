using UnityEngine;
using UnityEngine.Events;
using System.Collections.Concurrent;
using System;
using System.IO;
using Alchemy.Inspector;
using work.ctrl3d.Config;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class UnityTCPClient : MonoBehaviour
    {
#if USE_JSONCONFIG
        [Header("JsonConfig Settings")] 
        [SerializeField] private string configFileName = "TcpClientConfig.json";
#endif

        [Header("Network Settings")] 
        [SerializeField] private string address = "127.0.0.1";

        [SerializeField] private ushort port = 7777;
        [SerializeField] private string clientName = "";
        [SerializeField] private Color logColor = Color.yellow;
        [SerializeField] private bool connectOnStart;

        [Header("Unity Events")] public UnityEvent onConnected;
        public UnityEvent<string> onMessageReceived;
        public UnityEvent<string> onSystemMessageReceived;
        public UnityEvent onDisconnected;
        public UnityEvent<string> onNameRegistered;
        public UnityEvent onNameTaken;

        private TCPClient _tcpClient;

        // 네트워크 스레드의 이벤트를 메인 스레드에서 처리하기 위한 큐
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();

        #region Public Properties

        public bool IsConnected => _tcpClient is { IsConnected: true };
        public bool IsNameRegistered => _tcpClient is { IsNameRegistered: true };
        public string RegisteredClientName => _tcpClient?.ClientName ?? string.Empty;

        #endregion

        #region Unity Lifecycle Methods

        private void Awake()
        {
#if USE_JSONCONFIG
            var tcpClientConfigPath = Path.Combine(Application.dataPath, configFileName);
            var tcpClientConfig = new JsonConfig<TcpClientConfig>(tcpClientConfigPath).GetConfig();

            address = tcpClientConfig.address;
            port = tcpClientConfig.port;
            clientName = tcpClientConfig.clientName;
            ColorUtility.TryParseHtmlString(tcpClientConfig.logColor, out logColor);
#endif
            
            _tcpClient = new TCPClient(address, port, clientName, new TcpClientLogger());

            _tcpClient.OnConnected += HandleConnected;
            _tcpClient.OnMessageReceived += HandleMessageReceived;
            _tcpClient.OnSystemMessageReceived += HandleSystemMessageReceived;
            _tcpClient.OnDisconnected += HandleDisconnected;
            _tcpClient.OnNameRegistered += HandleNameRegistered;
            _tcpClient.OnNameTaken += HandleNameTaken;
        }

        private void Start()
        {
            if (connectOnStart)
            {
                Connect();
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
            // 이벤트 해제
            if (_tcpClient == null) return;
            _tcpClient.OnConnected -= HandleConnected;
            _tcpClient.OnMessageReceived -= HandleMessageReceived;
            _tcpClient.OnSystemMessageReceived -= HandleSystemMessageReceived;
            _tcpClient.OnDisconnected -= HandleDisconnected;
            _tcpClient.OnNameRegistered -= HandleNameRegistered;
            _tcpClient.OnNameTaken -= HandleNameTaken;
                
            // 클라이언트 해제
            _tcpClient.Dispose();
            _tcpClient = null;
        }


        #endregion

        #region Public Methods

        [Button, HorizontalGroup("Network")]
        public void Connect() => _tcpClient?.ConnectToServer();

        [Button, HorizontalGroup("Network")]
        public void Disconnect() => _tcpClient?.Disconnect();

        [Button]
        public void Send(string message) => _tcpClient?.Send(message);

        public void SetClientName(string newClientName)
        {
            clientName = newClientName;
        }

        #endregion

        #region Private Event Handlers

        private void HandleConnected()
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"Connected to {address}:{port}");
                onConnected?.Invoke();
            });
        }

        private void HandleMessageReceived(string message)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"Message received: {message}");
                onMessageReceived?.Invoke(message);
            });
        }

        private void HandleSystemMessageReceived(string systemMessage)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"System message received: {systemMessage}");
                onSystemMessageReceived?.Invoke(systemMessage);
            });
        }

        private void HandleDisconnected()
        {
            _mainThreadActions.Enqueue(() =>
            {
                LogWarning("Disconnected from server");
                onDisconnected?.Invoke();
            });
        }

        private void HandleNameRegistered(string registeredName)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"Name registered successfully: {registeredName}");
                onNameRegistered?.Invoke(registeredName);
            });
        }

        private void HandleNameTaken()
        {
            _mainThreadActions.Enqueue(() =>
            {
                LogWarning($"Name '{clientName}' is already taken!");
                onNameTaken?.Invoke();
            });
        }
        
        private void Log(string message)
        {
            Debug.Log($"[{nameof(UnityTCPClient)}] {message}".WithColor(logColor));
        }

        private void LogWarning(string message)
        {
            Debug.LogWarning($"[{nameof(UnityTCPClient)}] {message}".WithColor(logColor));
        }

        private void LogError(string message)
        {
            Debug.LogError($"[{nameof(UnityTCPClient)}] {message}".WithColor(logColor));
        }

        #endregion

        #region Inspector Debug Info

        [Header("Debug Info")] [SerializeField, ReadOnly]
        private bool _isConnected;

        [SerializeField, ReadOnly] private bool _isNameRegistered;
        [SerializeField, ReadOnly] private string _registeredName;

        private void LateUpdate()
        {
            // Inspector에서 실시간 정보 업데이트
            _isConnected = IsConnected;
            _isNameRegistered = IsNameRegistered;
            _registeredName = RegisteredClientName;
        }

        #endregion
    }
}