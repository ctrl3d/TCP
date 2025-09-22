using System;

namespace work.ctrl3d.Logger
{
    public class ConsoleLogger : ILogger
    {
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
        public bool IsLogEnabled { get; set; }

        public void Log(string message)
        {
            if (!IsLogEnabled || LogLevel < LogLevel.Info) return;
            Console.WriteLine($"[INFO] {DateTime.Now:HH:mm:ss} - {message}");
        }

        public void LogWarning(string message)
        {
            if (!IsLogEnabled || LogLevel < LogLevel.Warning) return;
            Console.WriteLine($"[WARN] {DateTime.Now:HH:mm:ss} - {message}");
        }

        public void LogError(string message)
        {
            if (!IsLogEnabled || LogLevel < LogLevel.Error) return;
            Console.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} - {message}");
        }

        public void LogDebug(string message)
        {
            if (!IsLogEnabled || LogLevel < LogLevel.Debug) return;
            Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss} - {message}");
        }
    }
}