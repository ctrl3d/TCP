using System;
using System.Net.Sockets;
using System.Text;
using work.ctrl3d.Logger;

namespace work.ctrl3d
{
    public class ClientConnection
    {
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _networkStream;
        private readonly byte[] _receiveBuffer;
        private readonly TCPServer _server;
        private readonly ILogger _logger;
        private bool _isConnected;

        public int ClientId { get; }
        public string ClientName { get; private set; } = string.Empty;
        public bool IsNameRegistered { get; private set; }

        public bool IsAlive
        {
            get
            {
                if (!_isConnected || _tcpClient is not { Connected: true })
                {
                    return false;
                }

                try
                {
                    return !(_tcpClient.Client.Poll(1, SelectMode.SelectRead) && _tcpClient.Client.Available == 0);
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }

        public ClientConnection(TcpClient tcpClient, TCPServer server, int clientId, ILogger logger = null)
        {
            _tcpClient = tcpClient;
            _server = server;
            ClientId = clientId;
            _logger = logger ?? new ConsoleLogger();
            _networkStream = _tcpClient.GetStream();
            _receiveBuffer = new byte[_tcpClient.ReceiveBufferSize];
            _isConnected = true;
            IsNameRegistered = false;
        }

        public void StartReceiving()
        {
            try
            {
                // 클라이언트에게 이름 등록 요청
                SendSystemMessage("REGISTER_NAME");
                _networkStream.BeginRead(_receiveBuffer, 0, _receiveBuffer.Length, ReceiveData, null);
            }
            catch (Exception e)
            {
                _logger.LogError($"Error starting to receive data: {e.Message}");
                Disconnect();
            }
        }

        private void ReceiveData(IAsyncResult result)
        {
            try
            {
                if (!_isConnected) return;

                var bytesRead = _networkStream.EndRead(result);
                if (bytesRead <= 0)
                {
                    _logger.Log($"Client {ClientId} ({ClientName}) gracefully disconnected: {GetClientEndPoint()}");
                    Disconnect();
                    return;
                }

                var receivedBytes = new byte[bytesRead];
                Array.Copy(_receiveBuffer, receivedBytes, bytesRead);

                var receivedMessage = Encoding.UTF8.GetString(receivedBytes);
                _logger.Log($"Received from client {ClientId} ({ClientName}) ({_tcpClient.Client.RemoteEndPoint}): {receivedMessage}");

                ProcessReceivedData(receivedMessage);

                if (_isConnected)
                {
                    _networkStream.BeginRead(_receiveBuffer, 0, _receiveBuffer.Length, ReceiveData, null);
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Error receiving data from client {ClientId} ({ClientName}): {e.Message}");
                Disconnect();
            }
        }

        private void ProcessReceivedData(string data)
        {
            switch (IsNameRegistered)
            {
                // 이름 등록 처리
                case false when data.StartsWith("NAME:"):
                {
                    var proposedName = data[5..].Trim();
                    if (_server.RegisterClientName(this, proposedName))
                    {
                        ClientName = proposedName;
                        IsNameRegistered = true;
                        SendSystemMessage("NAME_REGISTERED");
                        _logger.Log($"Client {ClientId} registered with name: {ClientName}");
                    }
                    else
                    {
                        SendSystemMessage("NAME_TAKEN");
                    }
                    return;
                }
                // 이름이 등록되지 않은 경우 메시지 무시
                case false:
                    SendSystemMessage("REGISTER_NAME_FIRST");
                    return;
                default:
                    // 일반 메시지 처리
                    _server.OnMessageReceivedFromClient(ClientId, ClientName, data);
                    break;
            }
        }

        private void SendSystemMessage(string message)
        {
            try
            {
                var systemMessage = $"SYSTEM:{message}";
                var sendBytes = Encoding.UTF8.GetBytes(systemMessage);
                _networkStream.Write(sendBytes, 0, sendBytes.Length);
                _networkStream.Flush();
            }
            catch (Exception e)
            {
                _logger.LogError($"Error sending system message to client {ClientId}: {e.Message}");
            }
        }

        public bool SendData(string message)
        {
            if (!IsAlive)
            {
                Disconnect();
                return false;
            }

            try
            {
                var sendBytes = Encoding.UTF8.GetBytes(message);
                _networkStream.Write(sendBytes, 0, sendBytes.Length);
                _networkStream.Flush();
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Could not send data to client {ClientId} ({ClientName}) ({GetClientEndPoint()}): {e.Message}. Client will be disconnected.");
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            if (!_isConnected) return;
            _isConnected = false;

            _networkStream?.Close();
            _tcpClient?.Close();

            _server.ClientDisconnected(this);
        }

        public string GetClientEndPoint()
        {
            try
            {
                return _tcpClient?.Client?.RemoteEndPoint?.ToString() ?? "Unknown";
            }
            catch
            {
                return "Disconnected";
            }
        }
    }
}