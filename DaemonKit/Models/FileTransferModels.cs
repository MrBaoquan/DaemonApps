using System;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;
using ReactiveUI;

namespace DaemonKit.Models
{
    /// <summary>
    /// 传输任务状态
    /// </summary>
    public enum TransferState
    {
        /// <summary>等待中</summary>
        Pending,

        /// <summary>传输中</summary>
        Transferring,

        /// <summary>已暂停</summary>
        Paused,

        /// <summary>已完成</summary>
        Completed,

        /// <summary>传输失败</summary>
        Failed,

        /// <summary>已取消</summary>
        Cancelled
    }

    /// <summary>
    /// 传输方向
    /// </summary>
    public enum TransferDirection
    {
        /// <summary>上传（发送文件）</summary>
        Upload,

        /// <summary>下载（接收文件）</summary>
        Download
    }

    /// <summary>
    /// 设备状态
    /// </summary>
    public enum MachineStatus
    {
        /// <summary>在线</summary>
        Online,

        /// <summary>离线</summary>
        Offline,

        /// <summary>忙碌（正在传输）</summary>
        Busy,

        /// <summary>连接中</summary>
        Connecting
    }

    /// <summary>
    /// 单个文件传输任务
    /// </summary>
    public class FileTransferTask : ReactiveObject
    {
        /// <summary>任务唯一标识</summary>
        public string TaskId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>文件名</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>本地文件路径</summary>
        public string LocalPath { get; set; } = string.Empty;

        /// <summary>远程文件路径</summary>
        public string RemotePath { get; set; } = string.Empty;

        /// <summary>文件总字节数</summary>
        public long TotalBytes { get; set; }

        /// <summary>原子累加字段，供I/O线程无锁更新（避免在I/O线程触发PropertyChanged）</summary>
        internal long _transferredBytesRaw;

        private long _transferredBytes;

        /// <summary>已传输字节数（UI绑定用，仅在需要通知UI时通过属性赋值）</summary>
        public long TransferredBytes
        {
            get =>
                System.Threading.Interlocked.Read(ref _transferredBytesRaw) > 0
                    ? System.Threading.Interlocked.Read(ref _transferredBytesRaw)
                    : _transferredBytes;
            set
            {
                _transferredBytesRaw = value;
                this.RaiseAndSetIfChanged(ref _transferredBytes, value);
            }
        }

        private TransferState _state = TransferState.Pending;

        /// <summary>当前传输状态</summary>
        public TransferState State
        {
            get => _state;
            set => this.RaiseAndSetIfChanged(ref _state, value);
        }

        /// <summary>传输方向</summary>
        public TransferDirection Direction { get; set; }

        /// <summary>任务来源类型（区分手动发送/接收/远程浏览下载/进程包下载等）</summary>
        public TransferTaskSource Source { get; set; } = TransferTaskSource.ManualSend;

        /// <summary>远程设备信息</summary>
        public MachineInfo RemoteMachine { get; set; }

        /// <summary>传输开始时间</summary>
        public DateTime StartTime { get; set; }

        /// <summary>传输结束时间</summary>
        public DateTime? EndTime { get; set; }

        /// <summary>断点续传偏移量</summary>
        public long ResumeOffset { get; set; } = 0;

        /// <summary>文件MD5哈希（用于验证完整性）</summary>
        public string FileHash { get; set; } = string.Empty;

        private string _errorMessage = string.Empty;

        /// <summary>错误信息</summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }

        #region 计算属性

        /// <summary>传输进度百分比 (0-100)</summary>
        public double Progress => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100 : 0;

        /// <summary>已用时间</summary>
        public TimeSpan Elapsed => (EndTime ?? DateTime.Now) - StartTime;

        /// <summary>传输速度（字节/秒）</summary>
        public double SpeedBytesPerSecond =>
            Elapsed.TotalSeconds > 0 ? TransferredBytes / Elapsed.TotalSeconds : 0;

        /// <summary>预计剩余时间</summary>
        public TimeSpan EstimatedRemaining =>
            SpeedBytesPerSecond > 0
                ? TimeSpan.FromSeconds((TotalBytes - TransferredBytes) / SpeedBytesPerSecond)
                : TimeSpan.MaxValue;

        /// <summary>格式化的传输速度显示</summary>
        public string SpeedDisplay => FormatBytes(SpeedBytesPerSecond) + "/s";

        /// <summary>格式化的进度显示</summary>
        public string ProgressDisplay =>
            $"{FormatBytes(TransferredBytes)} / {FormatBytes(TotalBytes)}";

        #endregion

        /// <summary>
        /// 格式化字节数为可读字符串
        /// </summary>
        private static string FormatBytes(double bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            while (bytes >= 1024 && order < sizes.Length - 1)
            {
                order++;
                bytes /= 1024;
            }
            return $"{bytes:0.##} {sizes[order]}";
        }
    }

    /// <summary>
    /// 批量文件传输任务
    /// </summary>
    public class FileTransferBatch : ReactiveObject
    {
        /// <summary>批次唯一标识</summary>
        public string BatchId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>批次内的所有任务</summary>
        public ObservableCollection<FileTransferTask> Tasks { get; set; } = new();

        /// <summary>目标设备</summary>
        public MachineInfo TargetMachine { get; set; }

        /// <summary>总文件数</summary>
        public int TotalFiles => Tasks.Count;

        /// <summary>已完成文件数</summary>
        public int CompletedFiles => Tasks.Count(t => t.State == TransferState.Completed);

        /// <summary>总字节数</summary>
        public long TotalBytes => Tasks.Sum(t => t.TotalBytes);

        /// <summary>已传输字节数</summary>
        public long TransferredBytes => Tasks.Sum(t => t.TransferredBytes);

        /// <summary>批次整体进度</summary>
        public double Progress => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100 : 0;

        /// <summary>进度显示文本</summary>
        public string ProgressText => $"{CompletedFiles}/{TotalFiles} 文件完成";
    }

    #region 传输协议消息

    /// <summary>
    /// 传输元数据（发送端→接收端）
    /// </summary>
    public class TransferMetadata
    {
        /// <summary>任务ID</summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>文件名</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>文件总大小</summary>
        public long TotalBytes { get; set; }

        /// <summary>请求从此偏移量开始续传</summary>
        public long ResumeOffset { get; set; }

        /// <summary>文件MD5哈希</summary>
        public string FileHash { get; set; } = string.Empty;

        /// <summary>发送方设备名称</summary>
        public string SenderName { get; set; } = string.Empty;

        /// <summary>发送方IP地址</summary>
        public string SenderIP { get; set; } = string.Empty;

        /// <summary>任务来源类型（便于接收端区分：手动发送/进程包下载响应等）</summary>
        public string SourceHint { get; set; } = string.Empty;

        /// <summary>消息类型标识</summary>
        public string MessageType { get; set; } = "METADATA";
    }

    /// <summary>
    /// 续传响应（接收端→发送端）
    /// </summary>
    public class ResumeResponse
    {
        /// <summary>任务ID</summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>接收端已有字节数（实际续传位置）</summary>
        public long ActualOffset { get; set; }

        /// <summary>是否接受传输</summary>
        public bool Accepted { get; set; }

        /// <summary>拒绝原因</summary>
        public string Error { get; set; } = string.Empty;

        /// <summary>消息类型标识</summary>
        public string MessageType { get; set; } = "RESUME_RESPONSE";
    }

    /// <summary>
    /// 传输完成确认（接收端→发送端）
    /// </summary>
    public class TransferComplete
    {
        /// <summary>任务ID</summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>接收端计算的MD5</summary>
        public string ReceivedHash { get; set; } = string.Empty;

        /// <summary>哈希是否匹配</summary>
        public bool HashMatch { get; set; }

        /// <summary>消息类型标识</summary>
        public string MessageType { get; set; } = "TRANSFER_COMPLETE";
    }

    /// <summary>
    /// 数据块消息
    /// </summary>
    public class DataChunk
    {
        /// <summary>任务ID</summary>
        public string TaskId { get; set; } = string.Empty;

        /// <summary>块序号</summary>
        public int ChunkIndex { get; set; }

        /// <summary>数据内容（不参与JSON序列化，通过二进制帧传输）</summary>
        [JsonIgnore]
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>是否为最后一块</summary>
        public bool IsLastChunk { get; set; }

        /// <summary>文件MD5哈希（仅最后一个chunk携带，用于接收端校验）</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string FileHash { get; set; }

        /// <summary>消息类型标识</summary>
        public string MessageType { get; set; } = "DATA_CHUNK";
    }

    #endregion

    #region 扩展的MachineInfo

    /// <summary>
    /// 扩展的设备信息（继承自原有MachineInfo，添加P2P相关字段）
    /// </summary>
    public class MachineInfoExtended : MachineInfo, System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private MachineStatus _status = MachineStatus.Online;

        /// <summary>设备状态</summary>
        public MachineStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    PropertyChanged?.Invoke(
                        this,
                        new System.ComponentModel.PropertyChangedEventArgs(nameof(Status))
                    );
                }
            }
        }

        /// <summary>最后一次心跳时间</summary>
        public DateTime LastSeen { get; set; } = DateTime.Now;

        /// <summary>是否支持P2P文件传输</summary>
        public bool SupportsP2P { get; set; } = true;

        /// <summary>文件传输端口</summary>
        public int FileTransferPort { get; set; } = 7009;

        /// <summary>DaemonKit版本号</summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>当前活跃传输数</summary>
        public int ActiveTransfers { get; set; }

        /// <summary>累计传输字节数</summary>
        public long TotalBytesTransferred { get; set; }

        /// <summary>是否为手动添加的设备（用于跨路由器发现）</summary>
        public bool IsManuallyAdded { get; set; }

        /// <summary>是否被选中（用于批量操作，不参与JSON持久化）</summary>
        [Newtonsoft.Json.JsonIgnore]
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(
                        this,
                        new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected))
                    );
                }
            }
        }

        /// <summary>
        /// 检查设备是否在线（15秒超时）
        /// </summary>
        public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 15;

        /// <summary>
        /// 从基础MachineInfo创建扩展版本
        /// </summary>
        public static MachineInfoExtended FromMachineInfo(MachineInfo source)
        {
            return new MachineInfoExtended
            {
                ID = source.ID,
                Name = source.Name,
                GPUs = source.GPUs,
                CPUs = source.CPUs,
                IPs = source.IPs,
                Memories = source.Memories,
                LastSeen = DateTime.Now,
                Status = MachineStatus.Online
            };
        }

        /// <summary>
        /// 获取硬件信息摘要（CPU/GPU/内存合并显示）
        /// </summary>
        public string HardwareInfo
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();

                if (CPUs != null && CPUs.Count > 0)
                    parts.Add($"CPU: {string.Join(", ", CPUs)}");

                if (GPUs != null && GPUs.Count > 0)
                    parts.Add($"GPU: {string.Join(", ", GPUs)}");

                if (Memories != null && Memories.Count > 0)
                    parts.Add($"内存: {string.Join(", ", Memories)}");

                return parts.Count > 0 ? string.Join(" | ", parts) : "未知";
            }
        }

        /// <summary>
        /// 获取硬件信息简要摘要（用于列表展示）
        /// </summary>
        public string HardwareInfoSummary
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();

                if (CPUs != null && CPUs.Count > 0)
                {
                    var cpuText = CPUs[0];
                    if (CPUs.Count > 1)
                        cpuText += $" +{CPUs.Count - 1}";
                    parts.Add($"CPU: {cpuText}");
                }

                if (GPUs != null && GPUs.Count > 0)
                {
                    var gpuText = GPUs[0];
                    if (GPUs.Count > 1)
                        gpuText += $" +{GPUs.Count - 1}";
                    parts.Add($"GPU: {gpuText}");
                }

                if (Memories != null && Memories.Count > 0)
                {
                    var memText = Memories[0];
                    if (Memories.Count > 1)
                        memText += $" +{Memories.Count - 1}";
                    parts.Add($"内存: {memText}");
                }

                return parts.Count > 0 ? string.Join(" | ", parts) : "未知";
            }
        }
    }

    #endregion

    #region 共享文件模型

    /// <summary>
    /// 共享文件信息
    /// </summary>
    public class SharedFileInfo
    {
        /// <summary>文件名</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>相对路径（相对于共享目录）</summary>
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>完整路径</summary>
        public string FullPath { get; set; } = string.Empty;

        /// <summary>文件大小（字节）</summary>
        public long FileSize { get; set; }

        /// <summary>最后修改时间</summary>
        public DateTime LastModified { get; set; }

        /// <summary>文件MD5哈希值（用于校验本地是否已下载）</summary>
        public string FileMD5 { get; set; } = string.Empty;

        /// <summary>是否被选中（用于多选）</summary>
        public bool IsSelected { get; set; }

        /// <summary>格式化的文件大小</summary>
        public string FileSizeFormatted
        {
            get
            {
                if (FileSize < 1024)
                    return $"{FileSize} B";
                if (FileSize < 1024 * 1024)
                    return $"{FileSize / 1024.0:F1} KB";
                if (FileSize < 1024 * 1024 * 1024)
                    return $"{FileSize / 1024.0 / 1024.0:F1} MB";
                return $"{FileSize / 1024.0 / 1024.0 / 1024.0:F2} GB";
            }
        }
    }

    /// <summary>
    /// 请求远程文件列表
    /// </summary>
    public class ListFilesRequest
    {
        public string MessageType { get; set; } = "LIST_FILES_REQUEST";
        public string RequestId { get; set; } = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// 远程文件列表响应
    /// </summary>
    public class ListFilesResponse
    {
        public string MessageType { get; set; } = "LIST_FILES_RESPONSE";
        public string RequestId { get; set; } = string.Empty;
        public SharedFileInfo[] Files { get; set; } = Array.Empty<SharedFileInfo>();
    }

    /// <summary>
    /// 请求下载远程文件
    /// </summary>
    public class DownloadFileRequest
    {
        public string MessageType { get; set; } = "DOWNLOAD_FILE_REQUEST";
        public string RequestId { get; set; } = Guid.NewGuid().ToString();
        public string[] FileNames { get; set; } = Array.Empty<string>();

        /// <summary>请求方的IP地址（服务端需要知道发送到哪里）</summary>
        public string RequesterIP { get; set; } = string.Empty;

        /// <summary>请求方的传输端口</summary>
        public int RequesterPort { get; set; } = 7009;
    }

    #endregion
}
