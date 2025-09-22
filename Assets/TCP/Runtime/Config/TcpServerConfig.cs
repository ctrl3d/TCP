using System;

namespace work.ctrl3d.Config
{
    [Serializable]
    public class TcpServerConfig
    {
        // 네트워크 설정
        public string address = "0.0.0.0";
        public ushort port = 7777;
        public string serverName = "Server";
        
        // 로그 설정
        public LogSettings logSettings = new();
        
        // 서버 설정
        public ServerSettings serverSettings = new();
    }
    
    public class ServerSettings
    {
        public bool listenOnStart = true;
        public int maxClients = 100;
        public int connectionTimeout = 30; // 연결 타임아웃(초)
        public int cleanupInterval = 5; // 끊어진 연결 정리 간격(초)
        public bool kickDuplicateNames = true; // 중복 이름 연결 시 킥 여부
    }
    
}