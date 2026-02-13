using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DaemonKit.Models;
using DNHper;
using ReactiveUI;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 远程文件浏览器ViewModel
    /// </summary>
    public class RemoteFileBrowserViewModel : ReactiveObject
    {
        #region 属性

        private readonly MachineInfo _targetMachine;
        private readonly Func<MachineInfo, string[], Task> _downloadAction;
        private readonly Func<Task<SharedFileInfo[]>> _fileListProvider;

        /// <summary>缓存的原始文件列表（用于刷新时恢复）</summary>
        private SharedFileInfo[] _cachedFiles = Array.Empty<SharedFileInfo>();

        /// <summary>远程文件列表</summary>
        public ObservableCollection<SelectableFileInfo> Files { get; } = new();

        private bool _isLoading;

        /// <summary>是否正在加载</summary>
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        private string _statusText = "正在获取文件列表...";

        /// <summary>状态文本</summary>
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        private string _targetMachineName = string.Empty;

        /// <summary>目标机器名称</summary>
        public string TargetMachineName
        {
            get => _targetMachineName;
            set => this.RaiseAndSetIfChanged(ref _targetMachineName, value);
        }

        /// <summary>选中的文件数量</summary>
        public int SelectedCount => Files.Count(f => f.IsSelected);

        private bool _hasSelection;

        /// <summary>是否有选中的文件</summary>
        public bool HasSelection
        {
            get => _hasSelection;
            set => this.RaiseAndSetIfChanged(ref _hasSelection, value);
        }

        /// <summary>是否全部选中</summary>
        public bool AllSelected => Files.Count > 0 && Files.All(f => f.IsSelected);

        #endregion

        #region 命令

        /// <summary>刷新文件列表</summary>
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        /// <summary>全选/取消全选</summary>
        public ReactiveCommand<Unit, Unit> ToggleSelectAllCommand { get; }

        /// <summary>下载选中文件</summary>
        public ReactiveCommand<Unit, Unit> DownloadSelectedCommand { get; }

        /// <summary>在资源库中查看此设备</summary>
        public ReactiveCommand<Unit, Unit> OpenInResourceLibraryCommand { get; }

        /// <summary>关闭窗口（由外部设置）</summary>
        public Action CloseAction { get; set; }

        #endregion

        #region 构造函数

        public RemoteFileBrowserViewModel(
            MachineInfo targetMachine,
            Func<Task<SharedFileInfo[]>> fileListProvider,
            Func<MachineInfo, string[], Task> downloadAction
        )
        {
            _targetMachine = targetMachine;
            _fileListProvider = fileListProvider;
            _downloadAction = downloadAction;
            TargetMachineName = targetMachine?.Name ?? targetMachine?.ID ?? "未知设备";

            // 刷新命令
            RefreshCommand = ReactiveCommand.CreateFromTask(LoadFilesAsync);

            // 全选切换
            ToggleSelectAllCommand = ReactiveCommand.Create(() =>
            {
                var allSelected = Files.All(f => f.IsSelected);
                foreach (var file in Files)
                {
                    file.IsSelected = !allSelected;
                }
                UpdateSelectionStatus();
            });

            // 下载选中文件
            var canDownload = this.WhenAnyValue(x => x.HasSelection);
            DownloadSelectedCommand = ReactiveCommand.CreateFromTask(
                DownloadSelectedAsync,
                canDownload
            );

            // 在资源库中查看此设备
            OpenInResourceLibraryCommand = ReactiveCommand.Create(() =>
            {
                var deviceFilter = _targetMachine?.Name ?? _targetMachine?.ID ?? string.Empty;
                ReactiveUI.MessageBus.Current.SendMessage(deviceFilter, "OpenResourceLibrary");
                CloseAction?.Invoke();
            });
        }

        #endregion

        #region 方法

        /// <summary>
        /// 加载远程文件列表（通过UDP获取，避免TCP防火墙阻断）
        /// </summary>
        private async Task LoadFilesAsync()
        {
            try
            {
                IsLoading = true;
                StatusText = "正在获取远程文件列表...";

                if (_fileListProvider == null)
                {
                    StatusText = "文件列表提供者未配置";
                    return;
                }

                var ip = _targetMachine?.IPs?.FirstOrDefault() ?? _targetMachine?.ID;
                NLogger.Info($"[P2P] 正在从 {ip} 获取远程文件列表 (UDP)");

                // 通过UDP获取远程文件列表
                var remoteFiles = await _fileListProvider();

                // 缓存文件列表
                _cachedFiles = remoteFiles ?? Array.Empty<SharedFileInfo>();

                // 在UI线程填充列表
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    Files.Clear();
                    foreach (var file in _cachedFiles)
                    {
                        Files.Add(new SelectableFileInfo(file, UpdateSelectionStatus));
                    }
                });

                if (Files.Count > 0)
                {
                    StatusText = $"共 {Files.Count} 个远程文件";
                    NLogger.Info($"[P2P] 获取远程文件列表成功，共 {Files.Count} 个文件");
                }
                else
                {
                    StatusText = "远程设备暂无共享文件";
                    NLogger.Warn($"[P2P] 远程设备 {ip} 没有共享文件或请求超时");
                }
                UpdateSelectionStatus();
            }
            catch (Exception ex)
            {
                NLogger.Error($"获取远程文件列表失败: {ex.Message}");
                StatusText = "获取文件列表失败，请重试";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 下载选中的文件
        /// </summary>
        private async Task DownloadSelectedAsync()
        {
            var selectedFiles = Files.Where(f => f.IsSelected).Select(f => f.FileName).ToArray();
            if (selectedFiles.Length == 0)
                return;

            try
            {
                StatusText = $"正在下载 {selectedFiles.Length} 个文件...";
                await _downloadAction(_targetMachine, selectedFiles);
                StatusText = $"已添加 {selectedFiles.Length} 个文件到传输队列";

                // 关闭窗口
                CloseAction?.Invoke();
            }
            catch (Exception ex)
            {
                NLogger.Error($"下载文件失败: {ex.Message}");
                StatusText = "下载失败";
            }
        }

        /// <summary>
        /// 设置文件列表（从外部调用）
        /// </summary>
        public void SetFiles(SharedFileInfo[] files)
        {
            // 缓存原始文件列表
            _cachedFiles = files ?? Array.Empty<SharedFileInfo>();

            Files.Clear();
            foreach (var file in _cachedFiles)
            {
                Files.Add(new SelectableFileInfo(file, UpdateSelectionStatus));
            }
            StatusText = $"共 {Files.Count} 个文件";
            UpdateSelectionStatus();
        }

        /// <summary>
        /// 更新选中状态
        /// </summary>
        private void UpdateSelectionStatus()
        {
            HasSelection = Files.Any(f => f.IsSelected);
            this.RaisePropertyChanged(nameof(SelectedCount));
            this.RaisePropertyChanged(nameof(AllSelected));
        }

        #endregion
    }

    /// <summary>
    /// 可选择的文件信息（用于多选列表）
    /// </summary>
    public class SelectableFileInfo : ReactiveObject
    {
        private readonly Action _onSelectionChanged;

        /// <summary>原始文件信息引用</summary>
        public SharedFileInfo Source { get; }

        public string FileName { get; }
        public string RelativePath { get; }
        public long FileSize { get; }
        public DateTime LastModified { get; }
        public string FileSizeFormatted { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _isSelected, value);
                _onSelectionChanged?.Invoke();
            }
        }

        public SelectableFileInfo(SharedFileInfo source, Action onSelectionChanged = null)
        {
            _onSelectionChanged = onSelectionChanged;
            Source = source;
            FileName = source.FileName;
            RelativePath = source.RelativePath;
            FileSize = source.FileSize;
            LastModified = source.LastModified;
            FileSizeFormatted = source.FileSizeFormatted;
        }
    }
}
