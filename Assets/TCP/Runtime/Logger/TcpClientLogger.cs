using UnityEngine;

namespace work.ctrl3d.Logger
{
    public class TcpClientLogger : ILogger
    {
        public void Log(string message)
        {
            Debug.Log($"[TCP Client] {message}");
        }

        public void LogWarning(string message)
        {
            Debug.LogWarning($"[TCP Client] {message}");
        }

        public void LogError(string message)
        {
            Debug.LogError($"[TCP Client] {message}");
        }
    }
}