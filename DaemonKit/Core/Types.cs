using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DaemonKit
{
    public static class CommonVars
    {
        // �㲥meta��Ϣ��UDP�˿�
        public const int MetaPort = 7007;

        // ���տ���ָ���UDP�˿�
        public const int ControlPort = 7008;

        // ���������� UDP�˿�
        public const int HeartbeatPort = 7777;
    }

    public class Command
    {
        public const int SHUTDOWN = 1001;
        public const int RESTART = 1002;
        public const int BOOT = 1003;
        public const int RESTART_NODE_TREE = 1004;
        public const int STOP = 1005;

        public const int HEARTBEAT = 1221;

        [JsonProperty("evt")]
        public int EventID = 0;

        [JsonProperty("data")]
        public JObject Data;
    }
}
