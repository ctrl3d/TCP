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

        public Action OnConnected;
        public Action<string> OnMessageReceived;
        public Action<string> OnSystemMessageReceived;
        public Action OnDisconnected;
        public Action<string> OnNameRegistered;
        public Action OnNameTaken;

        public bool IsConnected => _tcpClient is { Connected: true };
        public bool IsNameRegistered => _isNameRegistered;
        public string ClientName { get; }

        public TCPClient(string address, ushort port, string clientName = null, ILogger logger = null)
        {
            _address = address;
            _port = port;
            _logger = logger ?? new ConsoleLogger();
            ClientName = clientName ?? string.Empty;
        }

        #region Public Methods

        public void ConnectToServer()
        {
            if (IsConnected)
            {
                _logger.LogWarning("Already connected to the server.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.BeginConnect(_address, _port, ConnectCallback, null);
                _logger.Log($"Attempting to connect to {_address}:{_port}...");
            }
            catch (Exception e)
            {
                _logger.LogError($"Error connecting to server: {e.Message}");
                Disconnect();
            }
        }

        public void Send(string message)
        {
            if (!IsConnected)
            {
                _logger.LogError("Not connected to the server. Cannot send message.");
                return;
            }

            if (!_isNameRegistered)
            {
                _logger.LogError("Name not registered yet. Cannot send regular messages.");
                return;
            }

            if (_disposed) throw new ObjectDisposedException(nameof(TCPClient));

            SendRawMessage(message);
        }

        private void SendRawMessage(string message)
        {
            try
            {
                var sendBytes = Encoding.UTF8.GetBytes(message);
                _networkStream.WriteAsync(sendBytes, 0, sendBytes.Length);
                _logger.Log($"Sent: {message}");
            }
            catch (Exception e)
            {
                _logger.LogError($"Error sending data: {e.Message}");
                Disconnect();
            }
        }

        public void Disconnect()
        {
            if (!IsConnected && _tcpClient == null) return;

            _logger.LogWarning("Disconnecting from the server.");

            _networkStream?.Close();
            _tcpClient?.Close();

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

        #region Private Callbacks

        private void ConnectCallback(IAsyncResult result)
        {
            try
            {
                _tcpClient.EndConnect(result);
                if (!_tcpClient.Connected)
                {
                    _logger.LogError("Failed to connect to the server.");
                    Disconnect();
                    return;
                }

                _networkStream = _tcpClient.GetStream();
                _receiveBuffer = new byte[_tcpClient.ReceiveBufferSize];
                _networkStream.BeginRead(_receiveBuffer, 0, _receiveBuffer.Length, ReceiveCallback, null);

                _logger.Log("Successfully connected to the server!");
                OnConnected?.Invoke();
            }
            catch (Exception e)
            {
                _logger.LogError($"Error in connection callback: {e.Message}");
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
                    _logger.LogWarning("Server has closed the connection.");
                    Disconnect();
                    return;
                }

                var receivedMessage = Encoding.UTF8.GetString(_receiveBuffer, 0, bytesRead);
                _logger.Log($"Received: {receivedMessage}");

                ProcessMessage(receivedMessage);

                if (IsConnected && _networkStream.CanRead)
                {
                    _networkStream.BeginRead(_receiveBuffer, 0, _receiveBuffer.Length, ReceiveCallback, null);
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Error receiving data: {e.Message}");
                Disconnect();
            }
        }

        private void ProcessMessage(string message)
        {
            // 시스템 메시지 처리
            if (message.StartsWith("SYSTEM:"))
            {
                var systemMessage = message.Substring(7);
                _logger.Log($"System message received: {systemMessage}");
                OnSystemMessageReceived?.Invoke(systemMessage);

                switch (systemMessage)
                {
                    case "REGISTER_NAME":
                        // 서버가 이름 등록을 요청하면 자동으로 등록
                        if (!string.IsNullOrWhiteSpace(ClientName))
                        {
                            SendRawMessage($"NAME:{ClientName}");
                            _logger.Log($"Auto-registering name: {ClientName}");
                        }
                        else
                        {
                            _logger.LogWarning("No client name provided for registration.");
                        }

                        break;
                    case "NAME_REGISTERED":
                        _isNameRegistered = true;
                        _logger.Log($"Name '{ClientName}' registered successfully!");
                        OnNameRegistered?.Invoke(ClientName);
                        break;
                    case "NAME_TAKEN":
                        _logger.LogWarning($"Name '{ClientName}' is already taken!");
                        OnNameTaken?.Invoke();
                        break;
                    case "REGISTER_NAME_FIRST":
                        _logger.LogWarning("Must register name before sending messages.");
                        break;
                }

                return;
            }

            // 일반 메시지 처리
            OnMessageReceived?.Invoke(message);
        }

        private void ClearAllEvents()
        {
            OnConnected = null;
            OnMessageReceived = null;
            OnSystemMessageReceived = null;
            OnDisconnected = null;
            OnNameRegistered = null;
            OnNameTaken = null;
        }

        ~TCPClient()
        {
            Dispose();
        }

        #endregion
    }
}