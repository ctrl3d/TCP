
using UnityEngine;
using UnityEngine.Events;
using System.Collections.Concurrent;
using System;
using System.IO;
using Alchemy.Inspector;
using work.ctrl3d.Config;
using work.ctrl3d.Logger;
using System.Collections;

namespace work.ctrl3d
{
    public class UnityTCPClient : MonoBehaviour
    {
#if USE_JSONCONFIG
        [Header("JsonConfig Settings")] 
        [SerializeField] private bool enableJsonConfig = true;
        [SerializeField] private string configFileName = "TcpClientConfig.json";
#endif

        [Header("Network Settings")] 
        [SerializeField] private string address = "127.0.0.1";

        [SerializeField] private ushort port = 7777;
        [SerializeField] private string clientName = "";
        [SerializeField] private bool connectOnStart = true;

        [Header("Log Settings")]
        [SerializeField] private bool enableLogging = true;
        [SerializeField] private Color logColor = Color.yellow;
        [SerializeField] private LogLevel logLevel = LogLevel.Info;
        
        [Header("Detail Log Settings")]
        [SerializeField] private bool logConnections = true;
        [SerializeField] private bool logMessages = true;
        [SerializeField] private bool logSystem = true;
        [SerializeField] private bool logErrors = true;
        [SerializeField] private bool logHeartbeat = false;
        [SerializeField] private bool logReconnection = true;

        private TcpClientLogger _clientLogger;
        
        [Header("Reconnection Settings")]
        [SerializeField] private bool autoReconnect = true;
        [SerializeField] private bool reconnectOnStart = true; // 시작 시 재접속 시도
        [SerializeField] private float reconnectInterval = 5f; // 재접속 시도 간격 (초)
        [SerializeField] private int maxReconnectAttempts = -1; // -1은 무제한 재시도
        [SerializeField] private float reconnectBackoffMultiplier = 1.5f; // 재시도 간격 증가 배수
        [SerializeField] private float maxReconnectInterval = 60f; // 최대 재시도 간격 (초)

        [Header("Connection Health")]
        [SerializeField] private bool enableHeartbeat = true;
        [SerializeField] private float heartbeatInterval = 30f; // 하트비트 간격 (초)

        [Header("Unity Events")] 
        public UnityEvent onConnected;
        public UnityEvent<string> onMessageReceived;
        public UnityEvent<string> onSystemMessageReceived;
        public UnityEvent onDisconnected;
        public UnityEvent<string> onNameRegistered;
        public UnityEvent onNameTaken;
        public UnityEvent<string, string> onRelayMessageReceived; // sender, message
        public UnityEvent<string[]> onClientListReceived; // client names
        public UnityEvent<int> onReconnectAttempt; // 재접속 시도 횟수
        public UnityEvent onReconnectSuccess; // 재접속 성공
        public UnityEvent onReconnectFailed; // 재접속 최종 실패
        public UnityEvent<string> onConnectionFailed; // 연결 실패

        private TCPClient _tcpClient;

        // 네트워크 스레드의 이벤트를 메인 스레드에서 처리하기 위한 큐
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();

        // 재접속 관련 변수
        private bool _isReconnecting;
        private int _reconnectAttempts;
        private float _currentReconnectInterval;
        private float _reconnectTimer;
        private bool _wasConnectedBefore;
        private bool _shouldReconnect;
        private Coroutine _reconnectCoroutine;

        // 하트비트 관련 변수
        private Coroutine _heartbeatCoroutine;
        private float _lastHeartbeatTime;

        #region Public Properties

        public bool IsConnected => _tcpClient is { IsConnected: true };
        public bool IsNameRegistered => _tcpClient is { IsNameRegistered: true };
        public string RegisteredClientName => _tcpClient?.ClientName ?? string.Empty;
        public bool IsReconnecting => _isReconnecting;
        public int ReconnectAttempts => _reconnectAttempts;
        public float TimeToNextReconnect => _isReconnecting ? _reconnectTimer : 0f;
        public bool IsConnecting => _tcpClient?.IsConnecting ?? false;

        #endregion

        #region Unity Lifecycle Methods

        private void Awake()
        {
            _clientLogger = new TcpClientLogger
            {
                LogColor = logColor,
                Prefix = $"[{nameof(UnityTCPClient)}]"
            };
            
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
                logLevel = tcpClientConfig.logSettings.logLevel;
                logConnections = tcpClientConfig.logSettings.logConnections;
                logMessages = tcpClientConfig.logSettings.logMessages;
                logSystem = tcpClientConfig.logSettings.logSystem;
                logErrors = tcpClientConfig.logSettings.logErrors;
                logHeartbeat = tcpClientConfig.logSettings.logHeartbeat;
                logReconnection = tcpClientConfig.logSettings.logReconnection;
            
                connectOnStart = tcpClientConfig.connectionSettings.connectOnStart;
                autoReconnect = tcpClientConfig.connectionSettings.autoReconnect;
                reconnectOnStart = tcpClientConfig.connectionSettings.reconnectOnStart;
                reconnectInterval = tcpClientConfig.connectionSettings.reconnectInterval;
                maxReconnectAttempts = tcpClientConfig.connectionSettings.maxReconnectAttempts;
                reconnectBackoffMultiplier = tcpClientConfig.connectionSettings.reconnectBackoffMultiplier;
                maxReconnectInterval = tcpClientConfig.connectionSettings.maxReconnectInterval;
                enableHeartbeat = tcpClientConfig.connectionSettings.enableHeartbeat;
                heartbeatInterval = tcpClientConfig.connectionSettings.heartbeatInterval;
            }
#endif
            
            // 로거 설정 적용
            _clientLogger.IsLogEnabled = enableLogging;
            _clientLogger.LogColor = logColor;
            _clientLogger.LogLevel = logLevel;
            
            InitializeClient();
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

            // 재접속 로직 처리
            HandleReconnection();

            // 하트비트 체크
            HandleHeartbeat();
        }

        private void OnDestroy()
        {
            StopAllCoroutines();
            CleanupClient();
        }

        #endregion

        #region Client Management

        private void InitializeClient()
        {
            _tcpClient = new TCPClient(address, port, clientName, _clientLogger);
            
            // 로그 설정 적용
            ApplyLogSettings();
            
            // 이벤트 연결
            _tcpClient.OnConnected += HandleConnected;
            _tcpClient.OnMessageReceived += HandleMessageReceived;
            _tcpClient.OnSystemMessageReceived += HandleSystemMessageReceived;
            _tcpClient.OnDisconnected += HandleDisconnected;
            _tcpClient.OnNameRegistered += HandleNameRegistered;
            _tcpClient.OnNameTaken += HandleNameTaken;
            _tcpClient.OnRelayMessageReceived += HandleRelayMessageReceived;
            _tcpClient.OnClientListReceived += HandleClientListReceived;
            _tcpClient.OnConnectionFailed += HandleConnectionFailed;
        }

        private void ApplyLogSettings()
        {
            if (_tcpClient == null) return;
            
            _tcpClient.EnableConnectionLogs = logConnections;
            _tcpClient.EnableMessageLogs = logMessages;
            _tcpClient.EnableSystemLogs = logSystem;
            _tcpClient.EnableErrorLogs = logErrors;
            _tcpClient.EnableHeartbeatLogs = logHeartbeat;
        }

        private void RecreateClient()
        {
            if (_tcpClient != null)
            {
                // 이벤트 해제
                _tcpClient.OnConnected -= HandleConnected;
                _tcpClient.OnMessageReceived -= HandleMessageReceived;
                _tcpClient.OnSystemMessageReceived -= HandleSystemMessageReceived;
                _tcpClient.OnDisconnected -= HandleDisconnected;
                _tcpClient.OnNameRegistered -= HandleNameRegistered;
                _tcpClient.OnNameTaken -= HandleNameTaken;
                _tcpClient.OnRelayMessageReceived -= HandleRelayMessageReceived;
                _tcpClient.OnClientListReceived -= HandleClientListReceived;
                _tcpClient.OnConnectionFailed -= HandleConnectionFailed;
                
                _tcpClient.Dispose();
            }

            // 새로운 클라이언트 생성 및 이벤트 재등록
            InitializeClient();
        }

        private void CleanupClient()
        {
            _shouldReconnect = false;
            _isReconnecting = false;

            if (_tcpClient == null) return;
            
            _tcpClient.OnConnected -= HandleConnected;
            _tcpClient.OnMessageReceived -= HandleMessageReceived;
            _tcpClient.OnSystemMessageReceived -= HandleSystemMessageReceived;
            _tcpClient.OnDisconnected -= HandleDisconnected;
            _tcpClient.OnNameRegistered -= HandleNameRegistered;
            _tcpClient.OnNameTaken -= HandleNameTaken;
            _tcpClient.OnRelayMessageReceived -= HandleRelayMessageReceived;
            _tcpClient.OnClientListReceived -= HandleClientListReceived;
            _tcpClient.OnConnectionFailed -= HandleConnectionFailed;
                
            _tcpClient.Dispose();
            _tcpClient = null;
        }

        #endregion

        #region Reconnection Logic

        private void HandleReconnection()
        {
            if (!_isReconnecting || !_shouldReconnect) return;

            _reconnectTimer -= Time.deltaTime;
        }

        private void StartReconnection()
        {
            if (_isReconnecting) return;

            if (logReconnection)
                Log($"시작 재연결 프로세스. 다음 간격으로 시도합니다: {_currentReconnectInterval}초");
                
            _isReconnecting = true;
            _reconnectTimer = 0.1f; // 거의 즉시 첫 번째 시도
            
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
            }
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
                if (logReconnection)
                    Log("최대 재연결 시도 횟수에 도달했습니다. 재연결을 중지합니다.");
                    
                StopReconnection();
                onReconnectFailed?.Invoke();
                return;
            }

            _reconnectAttempts++;
            if (logReconnection)
                Log($"재연결 시도 #{_reconnectAttempts}...");
                
            onReconnectAttempt?.Invoke(_reconnectAttempts);

            // TCP 클라이언트 재생성
            RecreateClient();
            _tcpClient?.ConnectToServer();

            // 다음 재시도를 위한 간격 증가 (백오프 전략)
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
            if (!enableHeartbeat || !IsConnected || !IsNameRegistered) return;

            if (Time.time - _lastHeartbeatTime >= heartbeatInterval)
            {
                SendHeartbeat();
                _lastHeartbeatTime = Time.time;
            }
        }

        private void SendHeartbeat()
        {
            if (!IsConnected || !IsNameRegistered) return;
            
            try
            {
                _tcpClient?.Ping();
            }
            catch (Exception e)
            {
                LogError($"하트비트 전송 실패: {e.Message}");
            }
        }

        private void ResetHeartbeat()
        {
            _lastHeartbeatTime = Time.time;
        }

        #endregion

        #region Log Settings

        /// <summary>
        /// 모든 로그를 활성화합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void EnableAllLogs()
        {
            enableLogging = true;
            logLevel = LogLevel.Debug;
            logConnections = true;
            logMessages = true;
            logSystem = true;
            logErrors = true;
            logHeartbeat = true;
            logReconnection = true;
            
            _clientLogger.IsLogEnabled = true;
            _clientLogger.LogLevel = LogLevel.Debug;
            
            if (_tcpClient != null)
                _tcpClient.EnableAllLogs();
            
            Log("모든 로그가 활성화되었습니다.");
        }

        /// <summary>
        /// 모든 로그를 비활성화합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void DisableAllLogs()
        {
            enableLogging = false;
            logConnections = false;
            logMessages = false;
            logSystem = false;
            logErrors = false;
            logHeartbeat = false;
            logReconnection = false;
            
            _clientLogger.IsLogEnabled = false;
            
            if (_tcpClient != null)
                _tcpClient.DisableAllLogs();
            
            Debug.Log($"[{nameof(UnityTCPClient)}] 모든 로그가 비활성화되었습니다.");
        }

        /// <summary>
        /// 에러 로그만 활성화합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void EnableErrorLogsOnly()
        {
            enableLogging = true;
            logLevel = LogLevel.Error;
            logConnections = false;
            logMessages = false;
            logSystem = false;
            logErrors = true;
            logHeartbeat = false;
            logReconnection = false;
            
            _clientLogger.IsLogEnabled = true;
            _clientLogger.LogLevel = LogLevel.Error;
            
            if (_tcpClient != null)
                _tcpClient.EnableErrorLogsOnly();
            
            Log("에러 로그만 활성화되었습니다.");
        }

        /// <summary>
        /// 하트비트 로그 상태를 토글합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void ToggleHeartbeatLogs()
        {
            logHeartbeat = !logHeartbeat;
            if (_tcpClient != null)
            {
                _tcpClient.EnableHeartbeatLogs = logHeartbeat;
            }
            
            Log($"하트비트 로그: {(logHeartbeat ? "활성화됨" : "비활성화됨")}");
        }

        /// <summary>
        /// 현재 로그 설정을 표시합니다.
        /// </summary>
        [Button, Group("Logging Controls")]
        public void ShowLogSettings()
        {
            var clientLogSettings = _tcpClient?.GetLogSettings() ?? "클라이언트가 초기화되지 않았습니다.";
            Log($"로그 설정: 활성화({enableLogging}), 레벨({logLevel}), 연결({logConnections}), " +
                $"메시지({logMessages}), 시스템({logSystem}), 에러({logErrors}), 하트비트({logHeartbeat}), 재연결({logReconnection})");
            Log($"클라이언트 로그 설정: {clientLogSettings}");
        }
        
        #endregion

        #region Public Methods

        [Button, HorizontalGroup("Network")]
        public void Connect()
        {
            _shouldReconnect = autoReconnect;
            StopReconnection();
            
            _tcpClient?.ConnectToServer();
        }

        [Button, HorizontalGroup("Network")]
        public void Disconnect()
        {
            _shouldReconnect = false;
            StopReconnection();
            
            if (_heartbeatCoroutine != null)
            {
                StopCoroutine(_heartbeatCoroutine);
                _heartbeatCoroutine = null;
            }
            
            _tcpClient?.Disconnect();
        }

        [Button, HorizontalGroup("Reconnection")]
        public void StartReconnect()
        {
            if (IsConnected)
            {
                LogWarning("이미 서버에 연결되어 있습니다.");
                return;
            }

            _shouldReconnect = true;
            StartReconnection();
        }

        [Button, HorizontalGroup("Reconnection")]
        public void StopReconnect()
        {
            _shouldReconnect = false;
            StopReconnection();
            Log("재연결이 중지되었습니다.");
        }

        [Button, HorizontalGroup("Reconnection")]
        public void ResetReconnectSettings()
        {
            _currentReconnectInterval = reconnectInterval;
            _reconnectAttempts = 0;
            _reconnectTimer = 0f;
            Log("재연결 설정이 초기화되었습니다.");
        }

        [Button, Group("Messaging")]
        public void Send(string message) => _tcpClient?.Send(message);

        [Button, Group("Messaging")]
        public void SendToClient(string targetClientName, string message) => 
            _tcpClient?.SendToClient(targetClientName, message);

        [Button, Group("Messaging")]
        public void Broadcast(string message) => _tcpClient?.Broadcast(message);

        [Button, Group("Information")]
        public void RequestClientList() => _tcpClient?.RequestClientList();

        [Button, Group("Information")]
        public void RequestUserList() => _tcpClient?.RequestUserList();

        [Button, Group("Connection Test")]
        public void Ping() => _tcpClient?.Ping();

        [Button, Group("Connection Test")]
        public void TestConnection()
        {
            var result = _tcpClient?.TestConnection() ?? false;
            Log($"연결 테스트 결과: {(result ? "성공" : "실패")}");
        }

        [Button, Group("Information")]
        public void ShowConnectionInfo()
        {
            var info = _tcpClient?.GetConnectionInfo() ?? "연결 없음";
            Log($"연결 정보: {info}");
        }

        [Button, Group("Information")]
        public void ShowStatus()
        {
            var status = _tcpClient?.GetStatusInfo() ?? "클라이언트가 없음";
            Log($"클라이언트 상태: {status}");
        }

        public void SetClientName(string newClientName)
        {
            clientName = newClientName;
        }

        public void SetAutoReconnect(bool enabled)
        {
            autoReconnect = enabled;
            _shouldReconnect = enabled;
            
            if (!enabled && _isReconnecting)
            {
                StopReconnect();
            }
        }

        public void SetReconnectInterval(float interval)
        {
            reconnectInterval = Mathf.Max(0.1f, interval);
            if (!_isReconnecting)
            {
                _currentReconnectInterval = reconnectInterval;
            }
        }

        public void SetHeartbeatEnabled(bool enabled)
        {
            enableHeartbeat = enabled;
            if (enabled)
            {
                ResetHeartbeat();
            }
        }

        public void SetHeartbeatInterval(float interval)
        {
            heartbeatInterval = Mathf.Max(1f, interval);
        }

        #endregion

        #region Event Handlers

        private void HandleConnected()
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"{address}:{port}에 연결되었습니다.");
                
                // 재접속 관련 상태 리셋
                if (_isReconnecting)
                {
                    if (logReconnection)
                        Log("재연결에 성공했습니다!");
                    StopReconnection();
                    onReconnectSuccess?.Invoke();
                }
                
                _wasConnectedBefore = true;
                ResetHeartbeat();
                onConnected?.Invoke();
            });
        }

        private void HandleMessageReceived(string message)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"메시지 수신: {message}");
                onMessageReceived?.Invoke(message);
            });
        }

        private void HandleSystemMessageReceived(string systemMessage)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"시스템 메시지 수신: {systemMessage}");
                onSystemMessageReceived?.Invoke(systemMessage);
            });
        }

        private void HandleDisconnected()
        {
            _mainThreadActions.Enqueue(() =>
            {
                LogWarning("서버와의 연결이 끊어졌습니다.");
                onDisconnected?.Invoke();
                
                // 자동 재접속 시작 (이전에 연결된 적이 있고, 자동 재접속이 활성화된 경우)
                if (_wasConnectedBefore && _shouldReconnect && !_isReconnecting)
                {
                    StartReconnection();
                }
            });
        }

        private void HandleNameRegistered(string registeredName)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"이름 등록 성공: {registeredName}");
                ResetHeartbeat();
                onNameRegistered?.Invoke(registeredName);
            });
        }

        private void HandleNameTaken()
        {
            _mainThreadActions.Enqueue(() =>
            {
                LogWarning($"이름 '{clientName}'은(는) 이미 사용 중입니다!");
                onNameTaken?.Invoke();
            });
        }
        
        private void HandleRelayMessageReceived(string senderName, string message)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"{senderName}에게서 개인 메시지: {message}");
                onRelayMessageReceived?.Invoke(senderName, message);
            });
        }

        private void HandleClientListReceived(string[] clientList)
        {
            _mainThreadActions.Enqueue(() =>
            {
                Log($"클라이언트 목록 수신: {string.Join(", ", clientList)}");
                onClientListReceived?.Invoke(clientList);
            });
        }

        private void HandleConnectionFailed(string reason)
        {
            _mainThreadActions.Enqueue(() =>
            {
                LogError($"연결 실패: {reason}");
                onConnectionFailed?.Invoke(reason);
                
                // 자동 재접속이 활성화된 경우 재접속 시작
                if (_shouldReconnect && !_isReconnecting)
                {
                    StartReconnection();
                }
            });
        }

        #endregion

        #region Logging

        private void Log(string message)
        {
            if (!enableLogging  || logLevel < LogLevel.Info) return;
            Debug.Log($"[{clientName}] {message}".WithColor(logColor));
        }

        private void LogWarning(string message)
        {
            if (!enableLogging || logLevel < LogLevel.Warning) return;
            Debug.LogWarning($"[{clientName}] {message}".WithColor(logColor));
        }

        private void LogError(string message)
        {
            if (!enableLogging || logLevel < LogLevel.Error) return;
            Debug.LogError($"[{clientName}] {message}".WithColor(logColor));
        }

        #endregion

        #region Inspector Debug Info

        [Header("Debug Info")] [SerializeField, ReadOnly]
        private bool _isConnected;

        [SerializeField, ReadOnly] private bool _isNameRegistered;
        [SerializeField, ReadOnly] private string _registeredName;
        [SerializeField, ReadOnly] private bool _reconnecting;
        [SerializeField, ReadOnly] private int _attemptCount;
        [SerializeField, ReadOnly] private float _nextReconnectIn;
        [SerializeField, ReadOnly] private bool _connecting;
        [SerializeField, ReadOnly] private float _lastHeartbeat;

        private void LateUpdate()
        {
            // Inspector에서 실시간 정보 업데이트
            _isConnected = IsConnected;
            _isNameRegistered = IsNameRegistered;
            _registeredName = RegisteredClientName;
            _reconnecting = _isReconnecting;
            _attemptCount = _reconnectAttempts;
            _nextReconnectIn = _isReconnecting ? _reconnectTimer : 0f;
            _connecting = IsConnecting;
            _lastHeartbeat = _lastHeartbeatTime;
        }

        #endregion

        #region Public API for External Scripts

        /// <summary>
        /// 외부 스크립트에서 사용할 수 있는 연결 상태 확인
        /// </summary>
        public bool CheckConnectionStatus()
        {
            return IsConnected && IsNameRegistered;
        }

        /// <summary>
        /// 외부 스크립트에서 사용할 수 있는 메시지 전송 (안전한 방식)
        /// </summary>
        public bool TrySendMessage(string message)
        {
            if (!CheckConnectionStatus())
            {
                LogWarning("메시지를 보낼 수 없습니다: 연결되지 않았거나 이름이 등록되지 않았습니다.");
                return false;
            }

            try
            {
                Send(message);
                return true;
            }
            catch (Exception e)
            {
                LogError($"메시지 전송 실패: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 외부 스크립트에서 사용할 수 있는 타겟팅된 메시지 전송
        /// </summary>
        public bool TrySendToClient(string targetClientName, string message)
        {
            if (!CheckConnectionStatus())
            {
                LogWarning("메시지를 보낼 수 없습니다: 연결되지 않았거나 이름이 등록되지 않았습니다.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetClientName))
            {
                LogWarning("대상 클라이언트 이름은 비워둘 수 없습니다.");
                return false;
            }

            try
            {
                SendToClient(targetClientName, message);
                return true;
            }
            catch (Exception e)
            {
                LogError($"{targetClientName}에게 메시지 전송 실패: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 현재 재접속 상태 정보를 반환
        /// </summary>
        public (bool isReconnecting, int attempts, float nextAttemptIn) GetReconnectionStatus()
        {
            return (_isReconnecting, _reconnectAttempts, _reconnectTimer);
        }

        /// <summary>
        /// 로그 설정 정보를 반환합니다.
        /// </summary>
        public string GetLogSettingsInfo()
        {
            return $"LogSettings[Enabled:{enableLogging}, Level:{logLevel}, " +
                   $"Connection:{logConnections}, Message:{logMessages}, System:{logSystem}, " +
                   $"Error:{logErrors}, Heartbeat:{logHeartbeat}, Reconnection:{logReconnection}]";
        }

        #endregion
    }
}