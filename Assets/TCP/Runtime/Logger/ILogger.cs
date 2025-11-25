namespace work.ctrl3d.Logger
{
    public interface ILogger
    {
        void Log(LogFilter category, string message);
        void LogWarning(LogFilter category, string message);
        void LogError(LogFilter category, string message);
    }
}