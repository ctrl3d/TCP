using System;
using System.Text;

namespace work.ctrl3d
{
    [Flags]
    public enum LogFilter
    {
        None = 0,
        Connection = 1 << 0, // 1
        Message = 1 << 1,    // 2
        System = 1 << 2,     // 4
        Error = 1 << 3,      // 8
        Heartbeat = 1 << 4,  // 16
        All = ~0             // 모든 비트 1
    }

    public static partial class TcpProtocol
    {
        public const string CmdTo = "CMD_TO";
        public const string CmdBroadcast = "BROADCAST";
        public const string CmdGetUsers = "GET_USERS";
        public const string CmdPing = "PING";
        public const string CmdPong = "PONG";
        public const string SystemUserNotFound = "SYSTEM:USER_NOT_FOUND";
        public const string SystemUserList = "SYSTEM:USER_LIST";
        public const string SystemNameTaken = "SYSTEM:NAME_TAKEN";

        public const char CmdSeparator = ':';
        public const int MaxPacketSize = 10 * 1024 * 1024; 

        public static string Pack(string command, params string[] args)
        {
            if (args == null || args.Length == 0) return command;
                
            var sb = new StringBuilder(command.Length + args.Length * 10); // 대략적인 초기 용량
            sb.Append(command);
                
            for (var i = 0; i < args.Length; i++)
            {
                sb.Append(CmdSeparator);
                sb.Append(args[i]);
            }
                
            return sb.ToString();
        }
    
        public static byte[] CreatePacket(string message)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(message);
            var lengthBytes = BitConverter.GetBytes(bodyBytes.Length);
        
            var packet = new byte[4 + bodyBytes.Length];
            Array.Copy(lengthBytes, 0, packet, 0, 4);
            Array.Copy(bodyBytes, 0, packet, 4, bodyBytes.Length);
        
            return packet;
        }
    }
}