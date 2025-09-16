using System;

namespace work.ctrl3d.Config
{
    [Serializable]
    public class TcpClientConfig
    {
        public string address = "127.0.0.1";
        public ushort port = 7777;
        public string clientName;
        public string logColor = "#00FFFF";
    }
}