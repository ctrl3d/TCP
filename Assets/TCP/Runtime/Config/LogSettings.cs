using System;
using work.ctrl3d.Logger;

namespace work.ctrl3d.Config
{
    /// <summary>
    /// TCP 클라이언트와 서버에서 사용하는 로그 설정 클래스입니다.
    /// JSON 구성 파일에서 로드될 수 있으며, 다양한 로그 유형을 제어합니다.
    /// </summary>
    [Serializable]
    public class LogSettings
    {
        // 기본 로그 설정
        public bool enableLogging = true;
        public string logColor = "#00FFFF";
        
        // 로그 유형 설정
        public bool logConnections = false;   // 연결 관련 로그
        public bool logMessages = false;      // 메시지 전송/수신 로그
        public bool logSystem = false;        // 시스템 메시지 로그
        public bool logErrors = false;        // 오류 및 경고 로그
        public bool logHeartbeat = false;    // 하트비트(PING/PONG) 로그
        public bool logClientState = false;   // 클라이언트 상태 변경 로그
        public bool logReconnection = false;  // 재연결 시도 로그 (클라이언트 전용)
        
        
        /// <summary>
        /// 로그 설정 정보를 문자열로 반환합니다.
        /// </summary>
        public override string ToString()
        {
            return $"Connection:{logConnections}, Message:{logMessages}, " +
                   $"System:{logSystem}, Error:{logErrors}, Heartbeat:{logHeartbeat}, " +
                   $"ClientState:{logClientState}, Reconnection:{logReconnection}]";
        }
    }
}