using System;
using System.IO;
using System.Linq;
using ReactiveUI;

namespace DaemonKit.Models
{
    /// <summary>
    /// 传输任务项 - 用于UI展示的增强版传输任务模型
    /// 具有响应式的速度、进度、ETA属性，支持滑动窗口瞬时速度计算
    /// </summary>
    public class TransferTaskItem : ReactiveObject
    {
        #region 基本属性

        /// <summary>任务唯一标识</summary>
        public string TaskId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>文件名</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>本地文件路径</summary>
        public string LocalPath { get; set; } = string.Empty;

        /// <summary>远程设备名称</summary>
        public string RemoteDeviceName { get; set; } = string.Empty;

        /// <summary>远程设备IP</summary>
        public string RemoteDeviceIP { get; set; } = string.Empty;

        /// <summary>文件总字节数</summary>
        public long TotalBytes { get; set; }

        /// <summary>传输方向</summary>
        public TransferDirection Direction { get; set; }

        /// <summary>传输开始时间</summary>
        public DateTime StartTime { get; set; } = DateTime.Now;

        /// <summary>传输结束时间</summary>
        public DateTime? EndTime { get; set; }

        /// <summary>任务来源类型（区分普通传输/进程包下载/远程浏览下载等）</summary>
        public TransferTaskSource Source { get; set; } = TransferTaskSource.ManualSend;

        #endregion

        #region 响应式属性

        private long _transferredBytes;

        /// <summary>已传输字节数</summary>
        public long TransferredBytes
        {
            get => _transferredBytes;
            set => this.RaiseAndSetIfChanged(ref _transferredBytes, value);
        }

        private TransferState _state = TransferState.Pending;

        /// <summary>传输状态</summary>
        public TransferState State
        {
            get => _state;
            set
            {
                var oldState = _state;
                this.RaiseAndSetIfChanged(ref _state, value);
                if (oldState != value)
                {
                    this.RaisePropertyChanged(nameof(CanPause));
                    this.RaisePropertyChanged(nameof(CanResume));
                    this.RaisePropertyChanged(nameof(CanCancel));
                    this.RaisePropertyChanged(nameof(IsActive));
                    this.RaisePropertyChanged(nameof(IsFinished));
                    this.RaisePropertyChanged(nameof(StateText));
                }
            }
        }

        private double _speedBytesPerSecond;

        /// <summary>瞬时传输速度（字节/秒）- 由TransferTaskManager滑动窗口计算</summary>
        public double SpeedBytesPerSecond
        {
            get => _speedBytesPerSecond;
            set => this.RaiseAndSetIfChanged(ref _speedBytesPerSecond, value);
        }

        private TimeSpan _estimatedRemaining = TimeSpan.MaxValue;

        /// <summary>预估剩余时间</summary>
        public TimeSpan EstimatedRemaining
        {
            get => _estimatedRemaining;
            set => this.RaiseAndSetIfChanged(ref _estimatedRemaining, value);
        }

        private string _errorMessage = string.Empty;

        /// <summary>错误信息</summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }

        private string _statusMessageOverride;

        /// <summary>状态文本覆盖（设置后StateText将显示此文本而非默认值）</summary>
        public string StatusMessageOverride
        {
            get => _statusMessageOverride;
            set
            {
                this.RaiseAndSetIfChanged(ref _statusMessageOverride, value);
                this.RaisePropertyChanged(nameof(StateText));
            }
        }

        #endregion

        #region 计算属性

        /// <summary>传输进度 (0-100)</summary>
        public double Progress => TotalBytes > 0 ? (double)TransferredBytes / TotalBytes * 100 : 0;

        /// <summary>已用时间</summary>
        public TimeSpan Elapsed => (EndTime ?? DateTime.Now) - StartTime;

        /// <summary>格式化的速度显示</summary>
        public string SpeedDisplay => Services.TransferTaskManager.FormatSpeed(SpeedBytesPerSecond);

        /// <summary>格式化的进度显示 (已传输/总量)</summary>
        public string ProgressDisplay =>
            $"{Services.TransferTaskManager.FormatBytes(TransferredBytes)} / {Services.TransferTaskManager.FormatBytes(TotalBytes)}";

        /// <summary>格式化的剩余时间</summary>
        public string ETADisplay => Services.TransferTaskManager.FormatETA(EstimatedRemaining);

        /// <summary>格式化的总大小</summary>
        public string TotalSizeDisplay => Services.TransferTaskManager.FormatBytes(TotalBytes);

        /// <summary>状态文本（优先显示StatusMessageOverride）</summary>
        public string StateText =>
            !string.IsNullOrEmpty(StatusMessageOverride)
                ? StatusMessageOverride
                : State switch
                {
                    TransferState.Pending => "等待中",
                    TransferState.Transferring => "传输中",
                    TransferState.Paused => "已暂停",
                    TransferState.Completed => "已完成",
                    TransferState.Failed => "失败",
                    TransferState.Cancelled => "已取消",
                    _ => "未知"
                };

        /// <summary>是否可以暂停</summary>
        public bool CanPause => State == TransferState.Transferring;

        /// <summary>是否可以恢复</summary>
        public bool CanResume => State == TransferState.Paused;

        /// <summary>是否可以取消</summary>
        public bool CanCancel =>
            State == TransferState.Transferring
            || State == TransferState.Pending
            || State == TransferState.Paused;

        /// <summary>是否是活跃任务（正在传输/等待/暂停）</summary>
        public bool IsActive =>
            State == TransferState.Transferring
            || State == TransferState.Pending
            || State == TransferState.Paused;

        /// <summary>是否已结束</summary>
        public bool IsFinished =>
            State == TransferState.Completed
            || State == TransferState.Failed
            || State == TransferState.Cancelled;

        /// <summary>方向图标名</summary>
        public string DirectionIcon =>
            Direction == TransferDirection.Upload ? "Upload" : "Download";

        /// <summary>来源类型文本</summary>
        public string SourceText =>
            Source switch
            {
                TransferTaskSource.ManualSend => "手动发送",
                TransferTaskSource.ManualReceive => "手动接收",
                TransferTaskSource.RemoteBrowseDownload => "远程下载",
                TransferTaskSource.PackageDownload => "进程包下载",
                TransferTaskSource.PackageExport => "进程包导出",
                TransferTaskSource.LocalCopy => "本地复制",
                _ => "其他"
            };

        /// <summary>文件扩展名</summary>
        public string FileExtension =>
            Path.GetExtension(FileName)?.ToLowerInvariant() ?? string.Empty;

        /// <summary>是否为补丁包文件（.dkp-patch.zip）</summary>
        public bool IsPatch =>
            FileName.EndsWith(".dkp-patch.zip", StringComparison.OrdinalIgnoreCase);

        /// <summary>是否为进程包文件（.dkp.zip，排除补丁包）</summary>
        public bool IsPackage =>
            !IsPatch && FileName.EndsWith(".dkp.zip", StringComparison.OrdinalIgnoreCase);

        /// <summary>是否为已完成的下载任务且文件存在</summary>
        public bool CanDeploy =>
            State == TransferState.Completed
            && Direction == TransferDirection.Download
            && (IsPackage || IsPatch)
            && !string.IsNullOrEmpty(LocalPath)
            && File.Exists(LocalPath);

        #endregion

        #region 方法

        /// <summary>
        /// 通知UI刷新所有动态计算属性（由TransferTaskManager定时器调用）
        /// </summary>
        public void RaiseProgressChanged()
        {
            this.RaisePropertyChanged(nameof(Progress));
            this.RaisePropertyChanged(nameof(ProgressDisplay));
            this.RaisePropertyChanged(nameof(SpeedDisplay));
            this.RaisePropertyChanged(nameof(ETADisplay));
            this.RaisePropertyChanged(nameof(Elapsed));
        }

        /// <summary>
        /// 从P2PFileTransferService的FileTransferTask创建
        /// </summary>
        public static TransferTaskItem FromServiceTask(FileTransferTask serviceTask)
        {
            return new TransferTaskItem
            {
                TaskId = serviceTask.TaskId,
                FileName = serviceTask.FileName,
                LocalPath = serviceTask.LocalPath,
                TotalBytes = serviceTask.TotalBytes,
                TransferredBytes = serviceTask.TransferredBytes,
                Direction = serviceTask.Direction,
                State = serviceTask.State,
                StartTime = serviceTask.StartTime,
                EndTime = serviceTask.EndTime,
                ErrorMessage = serviceTask.ErrorMessage,
                Source = serviceTask.Source,
                RemoteDeviceName = serviceTask.RemoteMachine?.Name ?? "",
                RemoteDeviceIP =
                    serviceTask.RemoteMachine?.IPs?.FirstOrDefault()
                    ?? serviceTask.RemoteMachine?.ID
                    ?? "",
            };
        }

        /// <summary>
        /// 从服务层任务更新状态（增量更新，不重建对象）
        /// </summary>
        public void UpdateFromServiceTask(FileTransferTask serviceTask)
        {
            TransferredBytes = serviceTask.TransferredBytes;
            if (serviceTask.State != State)
                State = serviceTask.State;
            if (serviceTask.ErrorMessage != ErrorMessage)
                ErrorMessage = serviceTask.ErrorMessage;
            if (serviceTask.EndTime.HasValue)
            {
                EndTime = serviceTask.EndTime;
            }
        }

        #endregion
    }

    /// <summary>
    /// 传输任务来源类型
    /// </summary>
    public enum TransferTaskSource
    {
        /// <summary>手动发送文件</summary>
        ManualSend,

        /// <summary>手动接收文件（被动接收）</summary>
        ManualReceive,

        /// <summary>远程浏览下载</summary>
        RemoteBrowseDownload,

        /// <summary>进程包下载</summary>
        PackageDownload,

        /// <summary>进程包导出</summary>
        PackageExport,

        /// <summary>本地文件复制（同机P2P）</summary>
        LocalCopy,
    }
}
