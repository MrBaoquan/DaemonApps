namespace DaemonKit.Models
{
    /// <summary>
    /// 软件包操作进度信息
    /// </summary>
    public class PackageProgressInfo
    {
        /// <summary>
        /// 操作类型
        /// </summary>
        public PackageOperationType OperationType { get; set; }

        /// <summary>
        /// 是否正在执行
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// 进度百分比 (0-100)
        /// </summary>
        public double ProgressPercentage { get; set; }

        /// <summary>
        /// 状态消息
        /// </summary>
        public string StatusMessage { get; set; }

        /// <summary>
        /// 当前文件
        /// </summary>
        public string CurrentFile { get; set; }

        /// <summary>
        /// 对话框引用（用于唤起）
        /// </summary>
        public object DialogInstance { get; set; }
    }

    /// <summary>
    /// 软件包操作类型
    /// </summary>
    public enum PackageOperationType
    {
        /// <summary>
        /// 打包（导出）
        /// </summary>
        Export,

        /// <summary>
        /// 安装（导入）
        /// </summary>
        Import
    }
}
