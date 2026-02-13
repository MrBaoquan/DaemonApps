using System;
using System.IO;
using ReactiveUI;

namespace DaemonKit.Models
{
    /// <summary>
    /// 资源库文件条目（聚合多设备共享文件）
    /// </summary>
    public class ResourceFileItem : ReactiveObject
    {
        /// <summary>文件名</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>相对路径</summary>
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>文件大小（字节）</summary>
        public long FileSize { get; set; }

        /// <summary>最后修改时间</summary>
        public DateTime LastModified { get; set; }

        /// <summary>来源设备名称</summary>
        public string SourceDeviceName { get; set; } = string.Empty;

        /// <summary>来源设备IP</summary>
        public string SourceDeviceIP { get; set; } = string.Empty;

        /// <summary>来源设备ID</summary>
        public string SourceDeviceID { get; set; } = string.Empty;

        /// <summary>是否为本机设备</summary>
        public bool IsLocalDevice { get; set; }

        /// <summary>远端文件MD5哈希值（用于校验本地文件是否一致）</summary>
        public string RemoteMD5 { get; set; } = string.Empty;

        /// <summary>来源设备显示文本（名称+IP）</summary>
        public string SourceDeviceDisplay =>
            IsLocalDevice
                ? $"{SourceDeviceName}（本机）"
                : string.IsNullOrEmpty(SourceDeviceIP)
                    ? SourceDeviceName
                    : $"{SourceDeviceName} ({SourceDeviceIP})";

        private bool _isSelected;

        /// <summary>是否被选中</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => this.RaiseAndSetIfChanged(ref _isSelected, value);
        }

        #region 下载状态

        private ResourceDownloadState _downloadState = ResourceDownloadState.None;

        /// <summary>下载状态</summary>
        public ResourceDownloadState DownloadState
        {
            get => _downloadState;
            set
            {
                this.RaiseAndSetIfChanged(ref _downloadState, value);
                this.RaisePropertyChanged(nameof(IsDownloading));
                this.RaisePropertyChanged(nameof(IsDownloaded));
                this.RaisePropertyChanged(nameof(IsPaused));
                this.RaisePropertyChanged(nameof(CanDownload));
                this.RaisePropertyChanged(nameof(CanPauseResume));
                this.RaisePropertyChanged(nameof(CanCancel));
            }
        }

        private double _downloadProgress;

        /// <summary>下载进度 0-100</summary>
        public double DownloadProgress
        {
            get => _downloadProgress;
            set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
        }

        private string _downloadSpeed = string.Empty;

        /// <summary>下载速度显示</summary>
        public string DownloadSpeed
        {
            get => _downloadSpeed;
            set => this.RaiseAndSetIfChanged(ref _downloadSpeed, value);
        }

        /// <summary>关联的传输任务ID</summary>
        public string TransferTaskId { get; set; } = string.Empty;

        /// <summary>下载后的本地文件路径</summary>
        public string LocalFilePath { get; set; } = string.Empty;

        /// <summary>是否正在下载</summary>
        public bool IsDownloading => DownloadState == ResourceDownloadState.Downloading;

        /// <summary>是否已下载完成</summary>
        public bool IsDownloaded => DownloadState == ResourceDownloadState.Completed;

        /// <summary>是否已暂停</summary>
        public bool IsPaused => DownloadState == ResourceDownloadState.Paused;

        /// <summary>是否可以开始下载</summary>
        public bool CanDownload =>
            DownloadState == ResourceDownloadState.None
            || DownloadState == ResourceDownloadState.Failed;

        /// <summary>是否可以暂停/恢复</summary>
        public bool CanPauseResume =>
            DownloadState == ResourceDownloadState.Downloading
            || DownloadState == ResourceDownloadState.Paused;

        /// <summary>是否可以取消</summary>
        public bool CanCancel =>
            DownloadState == ResourceDownloadState.Downloading
            || DownloadState == ResourceDownloadState.Paused
            || DownloadState == ResourceDownloadState.Pending;

        #endregion

        /// <summary>格式化的文件大小</summary>
        public string FileSizeFormatted
        {
            get
            {
                if (FileSize < 1024)
                    return $"{FileSize} B";
                if (FileSize < 1024 * 1024)
                    return $"{FileSize / 1024.0:F1} KB";
                if (FileSize < 1024L * 1024 * 1024)
                    return $"{FileSize / 1024.0 / 1024.0:F1} MB";
                return $"{FileSize / 1024.0 / 1024.0 / 1024.0:F2} GB";
            }
        }

        /// <summary>文件扩展名</summary>
        public string FileExtension =>
            Path.GetExtension(FileName)?.ToLowerInvariant() ?? string.Empty;

        /// <summary>是否为补丁包文件（.dkp-patch.zip）</summary>
        public bool IsPatch =>
            FileName.EndsWith(".dkp-patch.zip", StringComparison.OrdinalIgnoreCase);

        /// <summary>是否为进程包文件（.dkp.zip，排除补丁包）</summary>
        public bool IsPackage =>
            !IsPatch && FileName.EndsWith(".dkp.zip", StringComparison.OrdinalIgnoreCase);

        /// <summary>文件类型分类</summary>
        public ResourceFileCategory Category
        {
            get
            {
                // 优先检测补丁包（.dkp-patch.zip）
                if (IsPatch)
                    return ResourceFileCategory.Patch;

                // 进程包（.dkp.zip，已排除补丁包）
                if (IsPackage)
                    return ResourceFileCategory.Package;

                return FileExtension switch
                {
                    ".exe"
                    or ".msi"
                    or ".bat"
                    or ".cmd"
                    or ".ps1"
                        => ResourceFileCategory.Executable,
                    ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => ResourceFileCategory.Archive,
                    ".dll" or ".sys" or ".ocx" => ResourceFileCategory.Library,
                    ".json"
                    or ".xml"
                    or ".yaml"
                    or ".yml"
                    or ".ini"
                    or ".cfg"
                    or ".conf"
                    or ".toml"
                        => ResourceFileCategory.Config,
                    ".log" or ".txt" or ".md" or ".csv" => ResourceFileCategory.Document,
                    ".png"
                    or ".jpg"
                    or ".jpeg"
                    or ".bmp"
                    or ".gif"
                    or ".ico"
                    or ".svg"
                        => ResourceFileCategory.Image,
                    _ => ResourceFileCategory.Other
                };
            }
        }

        /// <summary>文件类型显示文本</summary>
        public string CategoryText =>
            Category switch
            {
                ResourceFileCategory.Patch => "补丁包",
                ResourceFileCategory.Package => "进程包",
                ResourceFileCategory.Executable => "可执行",
                ResourceFileCategory.Archive => "压缩包",
                ResourceFileCategory.Library => "库文件",
                ResourceFileCategory.Config => "配置",
                ResourceFileCategory.Document => "文档",
                ResourceFileCategory.Image => "图片",
                _ => "其他"
            };

        /// <summary>
        /// 从 SharedFileInfo 创建 ResourceFileItem（统一转换入口）
        /// </summary>
        public static ResourceFileItem FromSharedFileInfo(
            SharedFileInfo source,
            string deviceName,
            string deviceIP,
            string deviceID,
            bool isLocal
        )
        {
            return new ResourceFileItem
            {
                FileName = source.FileName,
                RelativePath = source.RelativePath,
                FileSize = source.FileSize,
                LastModified = source.LastModified,
                RemoteMD5 = source.FileMD5,
                SourceDeviceName = deviceName,
                SourceDeviceIP = deviceIP,
                SourceDeviceID = deviceID,
                IsLocalDevice = isLocal,
            };
        }
    }

    /// <summary>
    /// 资源文件分类
    /// </summary>
    public enum ResourceFileCategory
    {
        Patch,
        Package,
        Executable,
        Archive,
        Library,
        Config,
        Document,
        Image,
        Other
    }

    /// <summary>
    /// 资源文件下载状态
    /// </summary>
    public enum ResourceDownloadState
    {
        /// <summary>未下载</summary>
        None,

        /// <summary>等待中</summary>
        Pending,

        /// <summary>下载中</summary>
        Downloading,

        /// <summary>已暂停</summary>
        Paused,

        /// <summary>已完成</summary>
        Completed,

        /// <summary>失败</summary>
        Failed
    }
}
