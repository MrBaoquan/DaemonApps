using ReactiveUI;
using System;

namespace DaemonKit.Models
{
    /// <summary>
    /// 远程包任务状态
    /// </summary>
    public enum RemotePackageState
    {
        /// <summary>等待中</summary>
        Pending,

        /// <summary>正在请求导出</summary>
        RequestingExport,

        /// <summary>远程正在导出</summary>
        Exporting,

        /// <summary>导出完成</summary>
        ExportCompleted,

        /// <summary>正在下载</summary>
        Downloading,

        /// <summary>完成</summary>
        Completed,

        /// <summary>失败</summary>
        Failed,

        /// <summary>已取消</summary>
        Cancelled
    }

    /// <summary>
    /// 远程进程包下载任务
    /// </summary>
    public class RemotePackageTask : ReactiveObject
    {
        /// <summary>任务ID</summary>
        public string TaskId { get; }

        /// <summary>远程设备名称</summary>
        public string MachineName { get; }

        /// <summary>远程设备IP</summary>
        public string MachineIP { get; }

        /// <summary>创建时间</summary>
        public DateTime CreatedTime { get; }

        private RemotePackageState _state = RemotePackageState.Pending;

        /// <summary>任务状态</summary>
        public RemotePackageState State
        {
            get => _state;
            set
            {
                var oldState = _state;
                this.RaiseAndSetIfChanged(ref _state, value);
                if (oldState != value)
                {
                    this.RaisePropertyChanged(nameof(StateText));
                    this.RaisePropertyChanged(nameof(IsCompleted));
                    this.RaisePropertyChanged(nameof(IsFailed));
                    this.RaisePropertyChanged(nameof(IsInProgress));
                }
            }
        }

        private double _progress;

        /// <summary>进度 (0-100)</summary>
        public double Progress
        {
            get => _progress;
            set => this.RaiseAndSetIfChanged(ref _progress, value);
        }

        private string _statusText = "等待中";

        /// <summary>状态描述文本</summary>
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        private string? _packageFileName;

        /// <summary>包文件名</summary>
        public string? PackageFileName
        {
            get => _packageFileName;
            set => this.RaiseAndSetIfChanged(ref _packageFileName, value);
        }

        private string? _errorMessage;

        /// <summary>错误信息</summary>
        public string? ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }

        private string? _localFilePath;

        /// <summary>下载后的本地文件路径</summary>
        public string? LocalFilePath
        {
            get => _localFilePath;
            set => this.RaiseAndSetIfChanged(ref _localFilePath, value);
        }

        /// <summary>状态显示文本</summary>
        public string StateText =>
            State switch
            {
                RemotePackageState.Pending => "等待中",
                RemotePackageState.RequestingExport => "请求导出...",
                RemotePackageState.Exporting => "远程导出中...",
                RemotePackageState.ExportCompleted => "导出完成",
                RemotePackageState.Downloading => "下载中...",
                RemotePackageState.Completed => "完成",
                RemotePackageState.Failed => "失败",
                RemotePackageState.Cancelled => "已取消",
                _ => "未知"
            };

        /// <summary>是否已完成</summary>
        public bool IsCompleted => State == RemotePackageState.Completed;

        /// <summary>是否失败</summary>
        public bool IsFailed => State == RemotePackageState.Failed;

        /// <summary>是否进行中</summary>
        public bool IsInProgress =>
            State != RemotePackageState.Completed
            && State != RemotePackageState.Failed
            && State != RemotePackageState.Cancelled;

        public RemotePackageTask(string machineName, string machineIP)
        {
            TaskId = Guid.NewGuid().ToString("N");
            MachineName = machineName;
            MachineIP = machineIP;
            CreatedTime = DateTime.Now;
        }

        /// <summary>
        /// 更新任务状态
        /// </summary>
        public void UpdateState(RemotePackageState state, string statusText, double progress = -1)
        {
            State = state;
            StatusText = statusText;
            if (progress >= 0)
            {
                Progress = progress;
            }
        }

        /// <summary>
        /// 设置失败状态
        /// </summary>
        public void SetFailed(string errorMessage)
        {
            State = RemotePackageState.Failed;
            ErrorMessage = errorMessage;
            StatusText = $"失败: {errorMessage}";
        }

        /// <summary>
        /// 设置完成状态
        /// </summary>
        public void SetCompleted(string localFilePath)
        {
            State = RemotePackageState.Completed;
            LocalFilePath = localFilePath;
            StatusText = "下载完成";
            Progress = 100;
        }
    }
}
