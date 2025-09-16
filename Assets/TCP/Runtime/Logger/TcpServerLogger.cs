using UnityEngine;

namespace work.ctrl3d.Logger
{
    public class TcpServerLogger : ILogger
    {
        public void Log(string message)
        {
            Debug.Log($"[TCP Server] {message}");
        }

        public void LogWarning(string message)
        {
            Debug.LogWarning($"[TCP Server] {message}");
        }

        public void LogError(string message)
        {
            Debug.LogError($"[TCP Server] {message}");
        }
    }
}