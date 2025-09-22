using UnityEngine;

namespace work.ctrl3d.Logger
{
    /// <summary>
    /// TCP 클라이언트를 위한 로거 구현
    /// </summary>
    public class TcpClientLogger : ILogger
    {
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
        public bool IsLogEnabled { get; set; } = true;
        public string Prefix { get; set; } = "[TCPClient]";
        public Color LogColor { get; set; } = Color.cyan;

        public void Log(string message)
        {
            if (!IsLogEnabled || LogLevel < LogLevel.Info) return;
            Debug.Log($"{Prefix} {message}".WithColor(LogColor));
        }

        public void LogWarning(string message)
        {
            if (!IsLogEnabled || LogLevel < LogLevel.Warning) return;
            Debug.LogWarning($"{Prefix} {message}".WithColor(LogColor));
        }

        public void LogError(string message)
        {
            if (!IsLogEnabled || LogLevel < LogLevel.Error) return;
            Debug.LogError($"{Prefix} {message}".WithColor(LogColor));
        }

        public void LogDebug(string message)
        {
            if (!IsLogEnabled || LogLevel < LogLevel.Debug) return;
            Debug.Log($"{Prefix} DEBUG: {message}".WithColor(LogColor));
        }
        
        /// <summary>
        /// 설정 정보를 문자열로 반환
        /// </summary>
        public override string ToString()
        {
            return $"TcpClientLogger[Enabled={IsLogEnabled}, Level={LogLevel}, Prefix=\"{Prefix}\"]";
        }
    }
}