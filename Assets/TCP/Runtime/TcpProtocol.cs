using System;
using System.Buffers;
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
        public const string CmdConnect = "CONNECT";
        public const string FromPrefix = "FROM";
        public const string SystemPrefix = "SYSTEM";
        public const string UserListPrefix = "USER_LIST";
        public const string UserNotFoundPrefix = "USER_NOT_FOUND";
        public const string NameTakenPrefix = "NAME_TAKEN";

        public const string SystemUserNotFound = "SYSTEM:USER_NOT_FOUND";
        public const string SystemUserList = "SYSTEM:USER_LIST";
        public const string SystemNameTaken = "SYSTEM:NAME_TAKEN";

        public const char CmdSeparator = ':';
        public const int MaxPacketSize = 10 * 1024 * 1024; 

        public static int EncodeInt32BE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
            return 4;
        }

        public static int DecodeInt32BE(byte[] buffer, int offset)
        {
            return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
        }

        public static string Pack(string command, params string[] args)
        {
            if (args == null || args.Length == 0) return command;
                
            var sb = new StringBuilder(command.Length + args.Length * 10);
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
            var bodyByteCount = Encoding.UTF8.GetByteCount(message);
            var packet = new byte[4 + bodyByteCount];
            
            EncodeInt32BE(packet, 0, bodyByteCount);
            Encoding.UTF8.GetBytes(message, 0, message.Length, packet, 4);
        
            return packet;
        }

        /// <summary>
        /// ArrayPool을 사용하여 패킷 버퍼를 대여하고 내용을 채웁니다.
        /// 사용 후 반드시 ArrayPool<byte>.Shared.Return()으로 반환해야 합니다.
        /// </summary>
        public static (byte[] buffer, int totalLength) RentPacket(string message)
        {
            var bodyByteCount = Encoding.UTF8.GetByteCount(message);
            var totalLength = 4 + bodyByteCount;
            var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
            
            EncodeInt32BE(buffer, 0, bodyByteCount);
            Encoding.UTF8.GetBytes(message, 0, message.Length, buffer, 4);
            
            return (buffer, totalLength);
        }

        public static (string command, string[] args) Unpack(string message)
        {
            if (string.IsNullOrEmpty(message)) return (string.Empty, Array.Empty<string>());
            
            var parts = message.Split(CmdSeparator);
            if (parts.Length == 1) return (parts[0], Array.Empty<string>());
            
            var args = new string[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);
            return (parts[0], args);
        }
    }
}