using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DaemonKit.Models
{
    public static class CommonVars
    {
        /// <summary>广播meta信息的UDP端口（默认值）</summary>
        public const int DefaultMetaPort = 7007;

        /// <summary>接收控制指令及心跳的UDP端口（默认值）</summary>
        public const int DefaultControlPort = 7008;

        /// <summary>P2P文件传输的TCP端口（默认值）</summary>
        public const int DefaultFileTransferPort = 7009;

        // 端口覆盖值（0 或负值表示使用默认值）
        private static int _customMetaPort = 0;
        private static int _customControlPort = 0;
        private static int _customFileTransferPort = 0;

        /// <summary>广播meta信息的UDP端口（可通过 AppSettings 覆盖）</summary>
        public static int MetaPort => _customMetaPort > 0 ? _customMetaPort : DefaultMetaPort;

        /// <summary>接收控制指令及心跳的UDP端口（可通过 AppSettings 覆盖）</summary>
        public static int ControlPort =>
            _customControlPort > 0 ? _customControlPort : DefaultControlPort;

        /// <summary>P2P文件传输及设备探测的TCP端口（可通过 AppSettings 覆盖）</summary>
        public static int FileTransferPort =>
            _customFileTransferPort > 0 ? _customFileTransferPort : DefaultFileTransferPort;

        /// <summary>
        /// 控制命令认证令牌（空字符串表示不启用认证）
        /// </summary>
        public static string AuthToken { get; private set; } = string.Empty;

        /// <summary>
        /// 是否启用了命令认证
        /// </summary>
        public static bool IsAuthEnabled => !string.IsNullOrEmpty(AuthToken);

        /// <summary>
        /// 从 AppSettings 加载端口覆盖设置
        /// </summary>
        public static void ApplyPortOverrides(
            int metaPort,
            int controlPort,
            int fileTransferPort,
            string authToken = ""
        )
        {
            _customMetaPort = metaPort;
            _customControlPort = controlPort;
            _customFileTransferPort = fileTransferPort;
            AuthToken = authToken ?? string.Empty;
        }
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
        public const int LIST_SHARED_FILES = 1010; // 请求远程文件列表（已迁移到TCP通道）
        public const int LIST_SHARED_FILES_RESPONSE = 1011; // 返回远程文件列表（已迁移到TCP通道）
        public const int PUSH_DOWNLOAD_FILES = 1012; // 请求远程推送下载文件

        // ── 音频控制 ──────────────────────────────────────────────────────
        public const int SET_VOLUME = 1013; // 设置音量 data: {"volume": 0-100}
        public const int MUTE = 1014; // 静音
        public const int UNMUTE = 1015; // 取消静音
        public const int TOGGLE_MUTE = 1016; // 切换静音状态
        public const int VOLUME_UP = 1017; // 系统步进增量
        public const int VOLUME_DOWN = 1018; // 系统步进减量

        // ── 节能模式 ──────────────────────────────────────────────────────
        public const int ENTER_POWER_SAVING = 1020; // 开启节能模式
        public const int EXIT_POWER_SAVING = 1021; // 退出节能模式

        // ── 显示器控制 ────────────────────────────────────────────────────
        public const int MONITOR_OFF = 1025; // 关闭显示器
        public const int MONITOR_ON = 1026; // 唤醒显示器

        // ── 系统功能 ──────────────────────────────────────────────────────
        public const int TAKE_SCREENSHOT = 1030; // 远程触发截图
        public const int DISABLE_DESKTOP = 1031; // 关闭桌面进程(explorer.exe)
        public const int ENABLE_DESKTOP = 1032; // 启用桌面进程(explorer.exe)
        public const int TOGGLE_TOUCH = 1033; // 切换触摸屏启用/禁用

        public const int HEARTBEAT = 1221;

        /// <summary>命令确认应答（data 中包含原始 evt 和执行结果）</summary>
        public const int ACK = 1300;

        [JsonProperty("evt")]
        public int EventID = 0;

        [JsonProperty("data")]
        public JObject Data;

        /// <summary>认证令牌（为空时表示不使用认证）</summary>
        [JsonProperty("token", NullValueHandling = NullValueHandling.Ignore)]
        public string Token;
    }
}
