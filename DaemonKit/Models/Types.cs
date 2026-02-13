using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DaemonKit.Models
{
    public static class CommonVars
    {
        /// <summary>广播meta信息的UDP端口</summary>
        public const int MetaPort = 7007;

        /// <summary>接收控制指令的UDP端口</summary>
        public const int ControlPort = 7008;

        /// <summary>接收心跳数据的UDP端口</summary>
        public const int HeartbeatPort = 7777;
    }

    public class Command
    {
        public const int SHUTDOWN = 1001;
        public const int RESTART = 1002;
        public const int BOOT = 1003;
        public const int RESTART_NODE_TREE = 1004;
        public const int STOP = 1005;
        public const int EXPORT_PACKAGE = 1006; // 导出进程包到共享文件夹
        public const int EXPORT_PACKAGE_COMPLETED = 1007; // 导出进程包完成通知
        public const int EXPORT_PACKAGE_PROGRESS = 1008; // 导出进程包进度通知
        public const int PUSH_PACKAGE_TO_REQUESTER = 1009; // 请求远程主机主动推送文件
        public const int LIST_SHARED_FILES = 1010; // UDP请求远程文件列表
        public const int LIST_SHARED_FILES_RESPONSE = 1011; // UDP返回远程文件列表
        public const int PUSH_DOWNLOAD_FILES = 1012; // UDP请求远程推送下载文件

        public const int HEARTBEAT = 1221;

        [JsonProperty("evt")]
        public int EventID = 0;

        [JsonProperty("data")]
        public JObject Data;
    }
}
