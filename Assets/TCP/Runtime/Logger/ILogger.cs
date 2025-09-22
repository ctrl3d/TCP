namespace work.ctrl3d.Logger
{
    public enum LogLevel
    {
        None = 0,     // 로그 출력 안함
        Error = 1,    // 에러만
        Warning = 2,  // 경고 + 에러
        Info = 3,     // 정보 + 경고 + 에러
        Debug = 4     // 모든 로그
    }

    public interface ILogger
    {
        LogLevel LogLevel { get; set; }
        bool IsLogEnabled { get; set; }
        
        void Log(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogDebug(string message);
    }
}