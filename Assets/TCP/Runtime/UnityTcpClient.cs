using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;
using work.ctrl3d.Config;
using work.ctrl3d.Logger;
#if USE_UNITASK
using Cysharp.Threading.Tasks;
#endif

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
        
        private enum ClientEventType
        {
            Connected,
            Disconnected,
            MessageReceived,
            SystemMessageReceived,
            NameRegistered,
            NameTaken,
            DirectMessageReceived,
            ClientListReceived,
            ConnectionFailed,
            Kicked
        }

        private struct ClientEventData
        {
            public ClientEventType Type;
            public string StringArg1;
            public string StringArg2;
            public string[] ArrayArg;
        }

        private readonly ConcurrentQueue<ClientEventData> _eventQueue = new();

        private bool _isReconnecting;
        private int _reconnectAttempts;
        private float _currentReconnectInterval;
        private float _reconnectTimer;
        private bool _wasConnectedBefore;
        private bool _shouldReconnect;
#if USE_UNITASK
        private CancellationTokenSource _reconnectCts;
#else
        private Coroutine _reconnectCoroutine;
#endif
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
            while (_eventQueue.TryDequeue(out var ev))
            {
                switch (ev.Type)
                {
                    case ClientEventType.Connected:
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
                        break;
                    case ClientEventType.Disconnected:
                        _activeLogger?.LogWarning(LogFilter.Connection, "서버와의 연결이 끊어졌습니다.");
                        OnDisconnected?.Invoke();
                        if (_wasConnectedBefore && _shouldReconnect && !_isReconnecting)
                            StartReconnection();
                        break;
                    case ClientEventType.MessageReceived:
                        _activeLogger?.Log(LogFilter.Message, $"메시지: {ev.StringArg1}");
                        OnMessageReceived?.Invoke(ev.StringArg1);
                        break;
                    case ClientEventType.SystemMessageReceived:
                        _activeLogger?.Log(LogFilter.System, $"시스템: {ev.StringArg1}");
                        OnSystemMessageReceived?.Invoke(ev.StringArg1);
                        break;
                    case ClientEventType.NameRegistered:
                        _activeLogger?.Log(LogFilter.System, $"이름 등록됨: {ev.StringArg1}");
                        OnNameRegistered?.Invoke(ev.StringArg1);
                        break;
                    case ClientEventType.NameTaken:
                        _activeLogger?.LogWarning(LogFilter.Error, "이름이 이미 사용 중입니다.");
                        OnNameTaken?.Invoke();
                        break;
                    case ClientEventType.DirectMessageReceived:
                        _activeLogger?.Log(LogFilter.Message, $"귓속말 ({ev.StringArg1}): {ev.StringArg2}");
                        OnDirectMessageReceived?.Invoke(ev.StringArg1, ev.StringArg2);
                        break;
                    case ClientEventType.ClientListReceived:
                        _activeLogger?.Log(LogFilter.System, $"사용자 목록: {string.Join(", ", ev.ArrayArg)}");
                        OnClientListReceived?.Invoke(ev.ArrayArg);
                        break;
                    case ClientEventType.ConnectionFailed:
                        _activeLogger?.LogError(LogFilter.Error, $"연결 실패: {ev.StringArg1}");
                        OnConnectionFailed?.Invoke(ev.StringArg1);
                        if (_shouldReconnect && !_isReconnecting) StartReconnection();
                        break;
                    case ClientEventType.Kicked:
                        _activeLogger?.LogWarning(LogFilter.System, $"강제 퇴장: {ev.StringArg1}");
                        OnKicked?.Invoke(ev.StringArg1);
                        break;
                }
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

#if USE_UNITASK
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            ReconnectionTaskAsync(_reconnectCts.Token).Forget();
#else
            if (_reconnectCoroutine != null) StopCoroutine(_reconnectCoroutine);
            _reconnectCoroutine = StartCoroutine(ReconnectionCoroutine());
#endif
        }

#if USE_UNITASK
        private async UniTaskVoid ReconnectionTaskAsync(CancellationToken token)
        {
            while (_isReconnecting && _shouldReconnect)
            {
                bool canceled = await UniTask.Delay(TimeSpan.FromSeconds(_currentReconnectInterval), cancellationToken: token).SuppressCancellationThrow();
                if (canceled || !_isReconnecting || !_shouldReconnect || IsConnected)
                    break;

                AttemptReconnect();
            }
        }
#else
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
#endif

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
#if USE_UNITASK
            _tcpClient?.ConnectToServerAsync().Forget();
#else
            _ = _tcpClient?.ConnectToServerAsync();
#endif

            _currentReconnectInterval = Mathf.Min(_currentReconnectInterval * reconnectBackoffMultiplier, maxReconnectInterval);
        }

        private void StopReconnection()
        {
            _isReconnecting = false;
            _reconnectAttempts = 0;
            _currentReconnectInterval = reconnectInterval;

#if USE_UNITASK
            if (_reconnectCts != null)
            {
                _reconnectCts.Cancel();
                _reconnectCts.Dispose();
                _reconnectCts = null;
            }
#else
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
#endif
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

        public void Connect()
        {
            _shouldReconnect = autoReconnect;
            StopReconnection();
#if USE_UNITASK
            _tcpClient?.ConnectToServerAsync().Forget();
#else
            _ = _tcpClient?.ConnectToServerAsync();
#endif
        }

        public void Disconnect()
        {
            _shouldReconnect = false;
            StopReconnection();
            _tcpClient?.Disconnect();
        }

        public void Send(string message) => _tcpClient?.Send(message);

        public void SendToClient(string targetName, string message) => _tcpClient?.SendToClient(targetName, message);

        public void Broadcast(string message) => _tcpClient?.Broadcast(message);

        public void RequestUserList() => _tcpClient?.RequestUserList();

        public void Ping() => _tcpClient?.Ping();

        #endregion

        #region Event Handlers (Thread-Safe)

        private void HandleConnected()
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.Connected });
        }

        private void HandleDisconnected()
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.Disconnected });
        }

        private void HandleMessageReceived(string message)
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.MessageReceived, StringArg1 = message });
        }

        private void HandleSystemMessageReceived(string msg)
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.SystemMessageReceived, StringArg1 = msg });
        }

        private void HandleNameRegistered(string name)
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.NameRegistered, StringArg1 = name });
        }

        private void HandleNameTaken()
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.NameTaken });
        }

        private void HandleDirectMessageReceived(string sender, string msg)
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.DirectMessageReceived, StringArg1 = sender, StringArg2 = msg });
        }

        private void HandleClientListReceived(string[] users)
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.ClientListReceived, ArrayArg = users });
        }

        private void HandleConnectionFailed(string reason)
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.ConnectionFailed, StringArg1 = reason });
        }

        private void HandleKicked(string reason)
        {
            _eventQueue.Enqueue(new ClientEventData { Type = ClientEventType.Kicked, StringArg1 = reason });
        }

        #endregion

        #region Logging Controls

        private void Log(string message)
        {
            _activeLogger?.Log(LogFilter.Connection, message);
        }

        public void EnableAllLogs() 
        {
            if (_activeLogger != null) _activeLogger.Filter = LogFilter.All;
            logFilter = LogFilter.All;
            Log("모든 로그 활성화"); 
        }

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