using System;

namespace work.ctrl3d.Config
{
    [Serializable]
    public class TcpClientConfig
    {
        // 네트워크 설정
        public string address = "127.0.0.1";
        public ushort port = 7777;
        public string clientName = "Client";
        
        // 로그 설정
        public LogSettings logSettings = new();
        
        // 연결 설정
        public ConnectionSettings connectionSettings = new();
    }
    
    [Serializable]
    public class ConnectionSettings
    {
        // 연결 설정
        public bool connectOnStart = true;
            
        // 재연결 설정
        public bool autoReconnect = true;
        public bool reconnectOnStart = true;
        public float reconnectInterval = 5.0f;
        public int maxReconnectAttempts = -1; // -1은 무제한
        public float reconnectBackoffMultiplier = 1.5f;
        public float maxReconnectInterval = 60.0f;
            
        // 하트비트 설정
        public bool enableHeartbeat = true;
        public float heartbeatInterval = 30.0f;
    }
}