namespace work.ctrl3d.Constants
{
    public static class SystemMessages
    {
        #region 연결 관련 메시지
        public const string RegisterName = "REGISTER_NAME";
        public const string NameRegistered = "NAME_REGISTERED";
        public const string NameTaken = "NAME_TAKEN";
        public const string RegisterNameFirst = "REGISTER_NAME_FIRST";
        #endregion

        #region 통신 관련 메시지
        public const string Ping = "PING";
        public const string Pong = "PONG";
        public const string GetClients = "GET_CLIENTS";
        public const string GetUsers = "GET_USERS";
        public const string Who = "WHO";
        public const string Status = "STATUS";
        public const string Help = "HELP";
        #endregion

        #region 메시지 전송 관련

        public const string ToPrefix = "TO:";
        public const string BroadcastPrefix = "BROADCAST:";
        public const string FromPrefix = "FROM:";
        public const string BroadcastFromPrefix = "BROADCAST FROM ";
        public const string NamePrefix = "NAME:";
        public const string SystemPrefix = "SYSTEM:";

        #endregion

        #region 응답 메시지

        public const string ClientListPrefix = "CLIENT_LIST:";
        public const string UserListPrefix = "USER_LIST:";
        public const string UserNotFoundPrefix = "USER_NOT_FOUND:";
        public const string YouArePrefix = "YOU_ARE:";
        public const string ServerStatusPrefix = "SERVER_STATUS:";
        public const string HelpTextPrefix = "HELP_TEXT:";
        public const string KickedPrefix = "KICKED:";
        public const string Kicked = "KICKED";

        #endregion

        #region 에러 메시지

        public const string InvalidTargetUser = "INVALID_TARGET_USER";
        public const string EmptyMessage = "EMPTY_MESSAGE";
        public const string InvalidToFormat = "INVALID_TO_FORMAT";
        public const string EmptyBroadcastMessage = "EMPTY_BROADCAST_MESSAGE";

        #endregion

        #region 서버 상태 메시지

        public const string ClientsPrefix = "CLIENTS_";
        public const string Running = "RUNNING";
        public const string IDPrefix = "ID_";

        #endregion

        #region 도움말 메시지

        public const string HelpMessage = @"사용 가능한 명령어:
TO:<사용자명>:<메시지> - 특정 사용자에게 메시지 전송
BROADCAST:<메시지> - 모든 사용자에게 메시지 전송
GET_USERS - 온라인 사용자 목록 가져오기
GET_CLIENTS - 온라인 클라이언트 목록 가져오기 (레거시)
PING - 연결 테스트
WHO - 자신의 사용자 정보 보기
STATUS - 서버 상태 확인
HELP - 이 도움말 메시지 표시";

        #endregion

        #region 메시지 포맷팅 헬퍼 메서드

        public static string FormatSystemMessage(string message)
        {
            return $"{SystemPrefix}{message}";
        }

        public static string FormatToMessage(string targetUser, string message)
        {
            return $"{ToPrefix}{targetUser}:{message}";
        }

        public static string FormatFromMessage(string sender, string message)
        {
            return $"{FromPrefix}{sender}:{message}";
        }

        public static string FormatBroadcastMessage(string message)
        {
            return $"{BroadcastPrefix}{message}";
        }

        public static string FormatBroadcastFromMessage(string sender, string message)
        {
            return $"{BroadcastFromPrefix}{sender}: {message}";
        }

        public static string FormatNameMessage(string name)
        {
            return $"{NamePrefix}{name}";
        }

        public static string FormatClientListMessage(string clientList)
        {
            return $"{ClientListPrefix}{clientList}";
        }

        public static string FormatUserListMessage(string userList)
        {
            return $"{UserListPrefix}{userList}";
        }

        public static string FormatUserNotFoundMessage(string userName)
        {
            return $"{UserNotFoundPrefix}{userName}";
        }

        public static string FormatYouAreMessage(string clientName, int clientId)
        {
            return $"{YouArePrefix}{clientName}:{IDPrefix}{clientId}";
        }

        public static string FormatServerStatusMessage(int connectedCount)
        {
            return $"{ServerStatusPrefix}{ClientsPrefix}{connectedCount}:{Running}";
        }

        public static string FormatHelpTextMessage()
        {
            return $"{HelpTextPrefix}{HelpMessage}";
        }

        public static string FormatKickedMessage(string reason)
        {
            return $"{KickedPrefix}{reason}";
        }

        #endregion
    }
}