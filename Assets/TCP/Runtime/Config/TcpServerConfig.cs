using System;

namespace work.ctrl3d.Config
{
    [Serializable]
    public class TcpServerConfig
    {
        public string address = "0.0.0.0";
        public ushort port = 7777;
        public string logColor = "#FFFF00";
    }
}