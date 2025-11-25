using UnityEngine;

namespace work.ctrl3d.Logger
{
    public class TcpLogger : ILogger
    {
        public LogFilter Filter { get; set; }
        
        private readonly string _prefix;
        private readonly Color _color;
        private readonly string _colorHex;

        public TcpLogger(string prefix, LogFilter filter, Color color)
        {
            _prefix = prefix;
            Filter = filter;
            _color = color;
            _colorHex = ColorUtility.ToHtmlStringRGB(color);
        }

        public void Log(LogFilter category, string message)
        {
            if ((Filter & category) == 0) return;
            Debug.Log($"<color=#{_colorHex}><b>{_prefix}</b> [{category}] {message}</color>");
        }

        public void LogWarning(LogFilter category, string message)
        {
            if ((Filter & category) == 0) return;
            Debug.LogWarning($"<color=#{_colorHex}><b>{_prefix}</b> [{category}] {message}</color>");
        }

        public void LogError(LogFilter category, string message)
        {
            if ((Filter & category) == 0) return;
            Debug.LogError($"<color=#{_colorHex}><b>{_prefix}</b> [{category}] {message}</color>");
        }
    }
}