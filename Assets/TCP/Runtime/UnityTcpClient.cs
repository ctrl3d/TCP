
using UnityEngine;
using System.Collections.Concurrent;
using System;
using System.IO;
using System.Collections;
using Alchemy.Inspector;
using work.ctrl3d.Config;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class UnityTcpClient : MonoBehaviour
    {
#if USE_JSONCONFIG
        [Header("JsonConfig Settings")] 
        [SerializeField] private bool enableJsonConfig = true;
        [SerializeField] private string configFileName = "TcpClientConfig.json";
#endif

        [Header("Network Settings")] 
        [SerializeField] private string address = "127.0.0.1";
        [SerializeField] private int port = 7777; 
        [SerializeField] private string clientName = "";
        [SerializeField] private bool connectOnStart = true;

        [Header("Log Settings")]
        [SerializeField] private bool enableLogging = true;
        [SerializeField] private Color logColor = Color.yellow;
    
        [Header("Detail Log Settings")]
        [SerializeField] private LogFilter logFilter = LogFilter.All;

        private TcpLogger _activeLogger;

        [Header("Reconnection Settings")]
        [SerializeField] private bool autoReconnect = true;
        [SerializeField] private bool reconnectOnStart = true; 
        [SerializeField] private float reconnectInterval = 5f;
        [SerializeField] private int maxReconnectAttempts = -1; 
        [SerializeField] private float reconnectBackoffMultiplier = 1.5f; 
        [SerializeField] private float maxReconnectInterval = 60f; 

        [Header("Connection Health")]
        [SerializeField] private bool enableHeartbeat = true;
        [SerializeField] private float heartbeatInterval = 30f; 

        // [Review Fix] UnityEvent 제거, 순수 C# 이벤트 사용
        public event Action OnConnected;
        public event Action<string> OnMessageReceived;
        public event Action<string> OnSystemMessageReceived;
        public event Action OnDisconnected;
        public event Action<string> OnNameRegistered;
        public event Action OnNameTaken;
        public event Action<string, string> OnDirectMessageReceived; 
        public event Action<string[]> OnClientListReceived; 
        public event Action<int> OnReconnectAttempt; 
        public event Action OnReconnectSuccess; 
        public event Action OnReconnectFailed; 
        public event Action<string> OnConnectionFailed; 
        public event Action<string> OnKicked; 

        private TcpClient _tcpClient;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();

        private bool _isReconnecting;
        private int _reconnectAttempts;
        private float _currentReconnectInterval;
        private float _reconnectTimer;
        private bool _wasConnectedBefore;
        private bool _shouldReconnect;
        private Coroutine _reconnectCoroutine;
        private float _lastHeartbeatTime;

        public bool IsConnected => _tcpClient is { IsConnected: true };
        public bool IsNameRegistered => _tcpClient is { IsNameRegistered: true };
        public string RegisteredClientName => _tcpClient?.ClientName ?? string.Empty;
        public bool IsReconnecting => _isReconnecting;
        public int ReconnectAttempts => _reconnectAttempts;
        public float TimeToNextReconnect => _isReconnecting ? _reconnectTimer : 0f;
        public bool IsConnecting => _tcpClient?.IsConnecting ?? false;

        private void Awake()
        {
#if USE_JSONCONFIG
            if (enableJsonConfig)
            {
                var tcpClientConfigPath = Path.Combine(Application.dataPath, configFileName);
                var tcpClientConfig = new JsonConfig<TcpClientConfig>(tcpClientConfigPath).GetConfig();

                address = tcpClientConfig.address;
                port = tcpClientConfig.port;
                clientName = tcpClientConfig.clientName;

                ColorUtility.TryParseHtmlString(tcpClientConfig.logSettings.logColor, out logColor);
                enableLogging = tcpClientConfig.logSettings.enableLogging;
            
                connectOnStart = tcpClientConfig.connectionSettings.connectOnStart;
                autoReconnect = tcpClientConfig.connectionSettings.autoReconnect;
            }
#endif
            InitializeClient();
        }

        private void InitializeClient()
        {
            LogFilter initialFilter = enableLogging ? logFilter : LogFilter.None;
            _activeLogger = new TcpLogger($"[{nameof(UnityTcpClient)}]", initialFilter, logColor);

            _tcpClient = new TcpClient(address, port, clientName, _activeLogger);

            _tcpClient.OnConnected += HandleConnected;
            _tcpClient.OnMessageReceived += HandleMessageReceived;
            _tcpClient.OnSystemMessageReceived += HandleSystemMessageReceived;
            _tcpClient.OnDisconnected += HandleDisconnected;
            _tcpClient.OnNameRegistered += HandleNameRegistered;
            _tcpClient.OnNameTaken += HandleNameTaken;
            _tcpClient.OnDirectMessageReceived += HandleDirectMessageReceived;
            _tcpClient.OnClientListReceived += HandleClientListReceived;
            _tcpClient.OnConnectionFailed += HandleConnectionFailed;
            _tcpClient.OnKicked += HandleKicked;
        }

        private void Start()
        {
            _currentReconnectInterval = reconnectInterval;
            _shouldReconnect = autoReconnect;

            if (connectOnStart)
            {
                Connect();
            }
            else if (reconnectOnStart && autoReconnect)
            {
                StartReconnection();
            }
        }

        private void Update()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                action.Invoke();
            }

            HandleReconnection();
            HandleHeartbeat();
        }

        private void OnDestroy()
        {
            StopAllCoroutines();
            CleanupClient();
        }

        private void RecreateClient()
        {
            CleanupClient(disposeOnly: true);
            InitializeClient();
        }

        private void CleanupClient(bool disposeOnly = false)
        {
            if (!disposeOnly)
            {
                _shouldReconnect = false;
                _isReconnecting = false;
            }

            if (_tcpClient == null) return;

            _tcpClient.OnConnected -= HandleConnected;
            _tcpClient.OnMessageReceived -= HandleMessageReceived;
            _tcpClient.OnSystemMessageReceived -= HandleSystemMessageReceived;
            _tcpClient.OnDisconnected -= HandleDisconnected;
            _tcpClient.OnNameRegistered -= HandleNameRegistered;
            _tcpClient.OnNameTaken -= HandleNameTaken;
            _tcpClient.OnDirectMessageReceived -= HandleDirectMessageReceived;
            _tcpClient.OnClientListReceived -= HandleClientListReceived;
            _tcpClient.OnConnectionFailed -= HandleConnectionFailed;
            _tcpClient.OnKicked -= HandleKicked;
    
            _tcpClient.Dispose();
            _tcpClient = null;
        }

        #region Reconnection Logic

        private void HandleReconnection()
        {
            if (!_isReconnecting || !_shouldReconnect) return;
            _reconnectTimer -= Time.deltaTime;
        }

        private void StartReconnection()
        {
            if (_isReconnecting) return;

            _activeLogger?.Log(LogFilter.Connection, $"시작 재연결 프로세스. 다음 간격으로 시도합니다: {_currentReconnectInterval}초");
    
            _isReconnecting = true;
            _reconnectTimer = 0.1f;

            if (_reconnectCoroutine != null) StopCoroutine(_reconnectCoroutine);
            _reconnectCoroutine = StartCoroutine(ReconnectionCoroutine());
        }

        private IEnumerator ReconnectionCoroutine()
        {
            while (_isReconnecting && _shouldReconnect)
            {
                yield return new WaitForSeconds(_currentReconnectInterval);

                if (!_isReconnecting || !_shouldReconnect || IsConnected)
                    break;

                AttemptReconnect();
            }
        }

        private void AttemptReconnect()
        {
            if (maxReconnectAttempts > 0 && _reconnectAttempts >= maxReconnectAttempts)
            {
                _activeLogger?.Log(LogFilter.Connection, "최대 재연결 시도 횟수에 도달했습니다.");
                StopReconnection();
                OnReconnectFailed?.Invoke();
                return;
            }

            _reconnectAttempts++;
            _activeLogger?.Log(LogFilter.Connection, $"재연결 시도 #{_reconnectAttempts}...");
    
            OnReconnectAttempt?.Invoke(_reconnectAttempts);

            RecreateClient();
            // [Review Fix] ConnectToServerAsync() 호출 (async Task지만 예외는 내부에서 처리됨)
            _ = _tcpClient?.ConnectToServerAsync(); 

            _currentReconnectInterval = Mathf.Min(_currentReconnectInterval * reconnectBackoffMultiplier, maxReconnectInterval);
        }

        private void StopReconnection()
        {
            _isReconnecting = false;
            _reconnectAttempts = 0;
            _currentReconnectInterval = reconnectInterval;

            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
        }

        #endregion

        #region Heartbeat Logic

        private void HandleHeartbeat()
        {
            if (!enableHeartbeat || !IsConnected) return;

            if (Time.time - _lastHeartbeatTime >= heartbeatInterval)
            {
                _tcpClient?.Ping();
                _lastHeartbeatTime = Time.time;
            }
        }

        #endregion

        #region Public Methods (Control)

        [Button, HorizontalGroup("Network")]
        public void Connect()
        {
            _shouldReconnect = autoReconnect;
            StopReconnection();
            // [Review Fix] Async 호출 대응
            _ = _tcpClient?.ConnectToServerAsync();
        }

        [Button, HorizontalGroup("Network")]
        public void Disconnect()
        {
            _shouldReconnect = false;
            StopReconnection();
            _tcpClient?.Disconnect();
        }

        [Button, Group("Messaging")]
        public void Send(string message) => _tcpClient?.Send(message);

        [Button, Group("Messaging")]
        public void SendToClient(string targetName, string message) => _tcpClient?.SendToClient(targetName, message);

        [Button, Group("Messaging")]
        public void Broadcast(string message) => _tcpClient?.Broadcast(message);

        [Button, Group("Information")]
        public void RequestUserList() => _tcpClient?.RequestUserList();

        [Button, Group("Connection Test")]
        public void Ping() => _tcpClient?.Ping();

        #endregion

        #region Event Handlers (Thread-Safe)

        private void HandleConnected()
        {
            _mainThreadActions.Enqueue(() =>
            {
                _activeLogger?.Log(LogFilter.Connection, $"{address}:{port}에 연결되었습니다.");
                if (_isReconnecting)
                {
                    _activeLogger?.Log(LogFilter.Connection, "재연결 성공!");
                    StopReconnection();
                    OnReconnectSuccess?.Invoke();
                }
                _wasConnectedBefore = true;
                _lastHeartbeatTime = Time.time;
                OnConnected?.Invoke();
            });
        }

        private void HandleDisconnected()
        {
            _mainThreadActions.Enqueue(() =>
            {
                _activeLogger?.LogWarning(LogFilter.Connection, "서버와의 연결이 끊어졌습니다.");
                OnDisconnected?.Invoke();
                if (_wasConnectedBefore && _shouldReconnect && !_isReconnecting)
                {
                    StartReconnection();
                }
            });
        }

        private void HandleMessageReceived(string message)
        {
            _mainThreadActions.Enqueue(() => {
                _activeLogger?.Log(LogFilter.Message, $"메시지: {message}");
                OnMessageReceived?.Invoke(message);
            });
        }

        private void HandleSystemMessageReceived(string msg)
        {
            _mainThreadActions.Enqueue(() => {
                _activeLogger?.Log(LogFilter.System, $"시스템: {msg}");
                OnSystemMessageReceived?.Invoke(msg);
            });
        }

        private void HandleNameRegistered(string name)
        {
            _mainThreadActions.Enqueue(() => {
                _activeLogger?.Log(LogFilter.System, $"이름 등록됨: {name}");
                OnNameRegistered?.Invoke(name);
            });
        }

        private void HandleNameTaken()
        {
            _mainThreadActions.Enqueue(() => {
                _activeLogger?.LogWarning(LogFilter.Error, "이름이 이미 사용 중입니다.");
                OnNameTaken?.Invoke();
            });
        }

        private void HandleDirectMessageReceived(string sender, string msg)
        {
            _mainThreadActions.Enqueue(() => {
                _activeLogger?.Log(LogFilter.Message, $"귓속말 ({sender}): {msg}");
                OnDirectMessageReceived?.Invoke(sender, msg);
            });
        }

        private void HandleClientListReceived(string[] users)
        {
            _mainThreadActions.Enqueue(() => {
                _activeLogger?.Log(LogFilter.System, $"사용자 목록: {string.Join(", ", users)}");
                OnClientListReceived?.Invoke(users);
            });
        }

        private void HandleConnectionFailed(string reason)
        {
            _mainThreadActions.Enqueue(() => {
                _activeLogger?.LogError(LogFilter.Error, $"연결 실패: {reason}");
                OnConnectionFailed?.Invoke(reason);
                if (_shouldReconnect && !_isReconnecting) StartReconnection();
            });
        }

        private void HandleKicked(string reason)
        {
            _mainThreadActions.Enqueue(() => {
                _activeLogger?.LogWarning(LogFilter.System, $"강제 퇴장: {reason}");
                OnKicked?.Invoke(reason);
            });
        }

        #endregion

        #region Logging Controls

        private void Log(string message)
        {
            _activeLogger?.Log(LogFilter.Connection, message);
        }

        [Button, Group("Logging Controls")]
        public void EnableAllLogs() 
        {
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.All;
            logFilter = LogFilter.All;
            Log("모든 로그 활성화"); 
        }

        [Button, Group("Logging Controls")]
        public void DisableAllLogs() 
        { 
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.None;
            logFilter = LogFilter.None;
        }

        public string GetLogSettings()
        {
            return $"Enabled: {enableLogging}, Filter: {logFilter}";
        }

        #endregion
    }
}