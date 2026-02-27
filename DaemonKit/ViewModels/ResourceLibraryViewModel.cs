using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DaemonKit.Views;
using DynamicData;
using DynamicData.Binding;
using DNHper;
using ReactiveUI;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 资源库ViewModel — 聚合所有在线设备的共享文件
    /// </summary>
    public class ResourceLibraryViewModel : ReactiveObject, IDisposable
    {
        #region 依赖

        private readonly DaemonPanelViewModel _panelVM;
        private readonly CompositeDisposable _disposables = new();

        #endregion

        #region 属性

        private readonly SourceList<ResourceFileItem> _fileSource = new();
        private readonly ReadOnlyObservableCollection<ResourceFileItem> _filteredFiles;

        /// <summary>过滤后的文件列表</summary>
        public ReadOnlyObservableCollection<ResourceFileItem> Files => _filteredFiles;

        private bool _isLoading;

        /// <summary>是否正在加载</summary>
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        private string _statusText = "就绪";

        /// <summary>状态文本</summary>
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        private string _searchText = string.Empty;

        /// <summary>搜索文本</summary>
        public string SearchText
        {
            get => _searchText;
            set => this.RaiseAndSetIfChanged(ref _searchText, value);
        }

        private string _selectedCategory = "全部";

        /// <summary>选中的分类过滤</summary>
        public string SelectedCategory
        {
            get => _selectedCategory;
            set => this.RaiseAndSetIfChanged(ref _selectedCategory, value);
        }

        /// <summary>分类选项列表</summary>
        public string[] CategoryOptions { get; } =
            new[] { "全部", "进程包", "可执行", "压缩包", "库文件", "配置", "文档", "图片", "其他" };

        /// <summary>文件总数</summary>
        public int TotalCount => _fileSource.Count;

        /// <summary>在线设备数</summary>
        private int _onlineDeviceCount;
        public int OnlineDeviceCount
        {
            get => _onlineDeviceCount;
            set => this.RaiseAndSetIfChanged(ref _onlineDeviceCount, value);
        }

        /// <summary>已扫描设备数</summary>
        private int _scannedDeviceCount;
        public int ScannedDeviceCount
        {
            get => _scannedDeviceCount;
            set => this.RaiseAndSetIfChanged(ref _scannedDeviceCount, value);
        }

        private int _selectedCount;

        /// <summary>选中文件数</summary>
        public int SelectedCount
        {
            get => _selectedCount;
            set => this.RaiseAndSetIfChanged(ref _selectedCount, value);
        }

        /// <summary>是否有选中文件</summary>
        public bool HasSelection => SelectedCount > 0;

        private bool _isAllSelected;
        private bool _isUpdatingAllSelected;

        /// <summary>是否全部选中（用于表头全选CheckBox）</summary>
        public bool IsAllSelected
        {
            get => _isAllSelected;
            set
            {
                if (_isUpdatingAllSelected || _isAllSelected == value)
                    return;
                _isUpdatingAllSelected = true;
                this.RaiseAndSetIfChanged(ref _isAllSelected, value);
                foreach (var file in _filteredFiles)
                    file.IsSelected = value;
                UpdateSelectionCount();
                _isUpdatingAllSelected = false;
            }
        }

        #endregion

        #region 命令

        /// <summary>刷新（重新扫描所有设备）</summary>
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        /// <summary>下载选中文件</summary>
        public ReactiveCommand<Unit, Unit> DownloadSelectedCommand { get; }

        /// <summary>全选/取消全选</summary>
        public ReactiveCommand<Unit, Unit> ToggleSelectAllCommand { get; }

        /// <summary>下载单个文件</summary>
        public ReactiveCommand<ResourceFileItem, Unit> DownloadFileCommand { get; }

        /// <summary>暂停/恢复下载</summary>
        public ReactiveCommand<ResourceFileItem, Unit> PauseResumeCommand { get; }

        /// <summary>取消下载</summary>
        public ReactiveCommand<ResourceFileItem, Unit> CancelDownloadCommand { get; }

        /// <summary>已下载的文件打开文件夹</summary>
        public ReactiveCommand<ResourceFileItem, Unit> OpenFolderCommand { get; }

        /// <summary>部署已下载的进程包</summary>
        public ReactiveCommand<ResourceFileItem, Unit> DeployFileCommand { get; }

        /// <summary>应用已下载的节点更新包（NodeFull / NodePatch）</summary>
        public ReactiveCommand<ResourceFileItem, Unit> ApplyNodePackageCommand { get; }

        /// <summary>关闭窗口</summary>
        public Action? CloseAction { get; set; }

        #endregion

        #region 构造函数

        public ResourceLibraryViewModel(DaemonPanelViewModel panelVM)
        {
            _panelVM = panelVM;

            // 构建搜索+分类过滤管道
            var searchFilter = this.WhenAnyValue(x => x.SearchText)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Select(BuildSearchPredicate);

            var categoryFilter = this.WhenAnyValue(x => x.SelectedCategory)
                .Select(BuildCategoryPredicate);

            // 合并两个过滤器
            var combinedFilter = searchFilter.CombineLatest(
                categoryFilter,
                (search, category) =>
                    new Func<ResourceFileItem, bool>(item => search(item) && category(item))
            );

            _fileSource
                .Connect()
                .Filter(combinedFilter)
                .Sort(
                    SortExpressionComparer<ResourceFileItem>
                        .Ascending(f => f.SourceDeviceName)
                        .ThenByAscending(f => f.FileName)
                )
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _filteredFiles)
                .Subscribe()
                .DisposeWith(_disposables);

            // 刷新命令
            RefreshCommand = ReactiveCommand.CreateFromTask(ScanAllDevicesAsync);

            // 下载选中（异步批量下载，按设备分组，避免阻塞UI）
            var canDownload = this.WhenAnyValue(x => x.HasSelection);
            DownloadSelectedCommand = ReactiveCommand.Create(
                () =>
                {
                    var selectedFiles = _filteredFiles
                        .Where(f => f.IsSelected && f.CanDownload)
                        .ToList();
                    if (selectedFiles.Count == 0)
                        return;
                    StatusText = $"正在下载 {selectedFiles.Count} 个文件...";
                    _ = BatchDownloadAsync(selectedFiles);
                },
                canDownload
            );

            // 全选切换
            ToggleSelectAllCommand = ReactiveCommand.Create(() =>
            {
                var allSelected = _filteredFiles.All(f => f.IsSelected);
                foreach (var file in _filteredFiles)
                {
                    file.IsSelected = !allSelected;
                }
                UpdateSelectionCount();
            });

            // 下载单个文件（fire-and-forget，避免阻塞UI）
            DownloadFileCommand = ReactiveCommand.Create<ResourceFileItem>(file =>
            {
                _ = StartDownloadAsync(file);
            });

            // 暂停/恢复
            PauseResumeCommand = ReactiveCommand.Create<ResourceFileItem>(file =>
            {
                if (string.IsNullOrEmpty(file.TransferTaskId))
                    return;

                if (file.IsPaused)
                {
                    // 恢复：需要 FileTransferTask 对象
                    if (
                        _panelVM.TransferService.ActiveTasks.TryGetValue(
                            file.TransferTaskId,
                            out var transferTask
                        )
                    )
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _panelVM.TransferService.ResumeTaskAsync(transferTask);
                                file.DownloadState = ResourceDownloadState.Downloading;
                            }
                            catch (Exception ex)
                            {
                                DNHper.NLogger.Error("[P2P] 恢复下载失败: {ErrorMessage}", ex.Message);
                            }
                        });
                    }
                }
                else if (file.IsDownloading)
                {
                    _panelVM.TransferService.PauseTask(file.TransferTaskId);
                    file.DownloadState = ResourceDownloadState.Paused;
                }
            });

            // 取消下载
            CancelDownloadCommand = ReactiveCommand.Create<ResourceFileItem>(file =>
            {
                if (string.IsNullOrEmpty(file.TransferTaskId))
                    return;
                _panelVM.TransferService.CancelTask(file.TransferTaskId);
                file.DownloadState = ResourceDownloadState.None;
                file.DownloadProgress = 0;
                file.DownloadSpeed = string.Empty;
                file.TransferTaskId = string.Empty;
            });

            // 打开已下载文件所在文件夹
            OpenFolderCommand = ReactiveCommand.Create<ResourceFileItem>(file =>
            {
                if (!string.IsNullOrEmpty(file.LocalFilePath) && File.Exists(file.LocalFilePath))
                {
                    System.Diagnostics.Process.Start(
                        "explorer.exe",
                        $"/select,\"{file.LocalFilePath}\""
                    );
                }
                else
                {
                    var dir = AppPathes.ReceivedFilesDir;
                    if (Directory.Exists(dir))
                        System.Diagnostics.Process.Start("explorer.exe", dir);
                }
            });

            // 部署已下载的进程包
            DeployFileCommand = ReactiveCommand.Create<ResourceFileItem>(file =>
            {
                if (file.IsPackage && file.IsDownloaded)
                {
                    _ = DeployPackageAfterDownloadAsync(file);
                }
            });

            // 应用已下载的节点更新包
            ApplyNodePackageCommand = ReactiveCommand.Create<ResourceFileItem>(file =>
            {
                if (file.IsPatch && file.IsDownloaded)
                {
                    _ = ApplyNodePackageAfterDownloadAsync(file);
                }
            });

            // 订阅传输进度更新（500ms轮询刷新下载中的文件进度）
            Observable
                .Interval(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    UpdateSelectionCount();
                    UpdateDownloadProgress();
                })
                .DisposeWith(_disposables);

            // 订阅传输任务状态变化
            SubscribeToTransferEvents();
        }

        #endregion

        #region 传输事件订阅

        /// <summary>
        /// 订阅P2P传输服务的事件以更新文件下载状态
        /// </summary>
        private void SubscribeToTransferEvents()
        {
            // 监听任务状态变化
            _panelVM.TaskManager.TaskStateChanged
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(taskItem =>
                {
                    // 根据任务ID找到对应的资源文件
                    var file = FindFileByTaskId(taskItem.TaskId);

                    // 如果通过TaskId未找到，尝试通过文件名匹配（解决任务完成快于ID匹配的竞态问题）
                    if (file == null)
                    {
                        file = _fileSource.Items.FirstOrDefault(
                            f =>
                                !string.IsNullOrEmpty(f.FileName)
                                && f.FileName.Equals(
                                    taskItem.FileName,
                                    StringComparison.OrdinalIgnoreCase
                                )
                                && (
                                    f.DownloadState == ResourceDownloadState.Pending
                                    || f.DownloadState == ResourceDownloadState.Downloading
                                    || f.DownloadState == ResourceDownloadState.Paused
                                )
                        );
                        if (file != null)
                        {
                            file.TransferTaskId = taskItem.TaskId;
                        }
                    }

                    if (file == null)
                        return;

                    // 当实际任务替换占位符时，更新TransferTaskId映射
                    if (file.TransferTaskId != taskItem.TaskId)
                    {
                        file.TransferTaskId = taskItem.TaskId;
                    }

                    if (taskItem.IsFinished)
                    {
                        HandleTaskCompletion(file, taskItem);
                    }
                    else if (taskItem.State == TransferState.Transferring)
                    {
                        file.DownloadState = ResourceDownloadState.Downloading;
                    }
                    else if (taskItem.State == TransferState.Paused)
                    {
                        file.DownloadState = ResourceDownloadState.Paused;
                    }
                })
                .DisposeWith(_disposables);
        }

        /// <summary>
        /// 通过任务ID查找对应的ResourceFileItem
        /// </summary>
        private ResourceFileItem FindFileByTaskId(string taskId)
        {
            return _fileSource.Items.FirstOrDefault(f => f.TransferTaskId == taskId);
        }

        /// <summary>
        /// 处理传输任务完成（成功/失败/取消）
        /// </summary>
        private void HandleTaskCompletion(ResourceFileItem file, TransferTaskItem taskItem)
        {
            if (taskItem.State == TransferState.Completed)
            {
                file.DownloadState = ResourceDownloadState.Completed;
                file.DownloadProgress = 100;
                file.DownloadSpeed = string.Empty;
                file.LocalFilePath = Path.Combine(AppPathes.ReceivedFilesDir, file.FileName);
                StatusText = $"{file.FileName} 下载完成";

                if (file.IsPatch)
                {
                    _ = ApplyNodePackageAfterDownloadAsync(file);
                }
                else if (file.IsPackage)
                {
                    _ = DeployPackageAfterDownloadAsync(file);
                }
            }
            else if (taskItem.State == TransferState.Failed)
            {
                file.DownloadState = ResourceDownloadState.Failed;
                file.DownloadSpeed = string.Empty;
                StatusText = $"{file.FileName} 下载失败";
            }
            else if (taskItem.State == TransferState.Cancelled)
            {
                file.DownloadState = ResourceDownloadState.None;
                file.DownloadProgress = 0;
                file.DownloadSpeed = string.Empty;
                StatusText = $"{file.FileName} 已取消";
            }
        }

        /// <summary>
        /// 轮询更新下载进度
        /// </summary>
        private void UpdateDownloadProgress()
        {
            var downloadingFiles = _fileSource.Items
                .Where(
                    f =>
                        !string.IsNullOrEmpty(f.TransferTaskId)
                        && (
                            f.DownloadState == ResourceDownloadState.Downloading
                            || f.DownloadState == ResourceDownloadState.Pending
                            || f.DownloadState == ResourceDownloadState.Paused
                        )
                )
                .ToList();

            foreach (var file in downloadingFiles)
            {
                var taskItem = _panelVM.TaskManager.FindTask(file.TransferTaskId);
                if (taskItem == null)
                {
                    // 任务已被清理 — 检查文件是否已接收完成（补救任务完成后被移除的情况）
                    var receivedPath = Path.Combine(AppPathes.ReceivedFilesDir, file.FileName);
                    if (File.Exists(receivedPath))
                    {
                        file.DownloadState = ResourceDownloadState.Completed;
                        file.DownloadProgress = 100;
                        file.DownloadSpeed = string.Empty;
                        file.LocalFilePath = receivedPath;
                        StatusText = $"{file.FileName} 下载完成";

                        if (file.IsPackage)
                        {
                            _ = DeployPackageAfterDownloadAsync(file);
                        }
                    }
                    continue;
                }

                // 检查任务是否已完成（补救事件丢失的情况）
                if (taskItem.IsFinished)
                {
                    HandleTaskCompletion(file, taskItem);
                }
                else
                {
                    file.DownloadProgress = taskItem.Progress;
                    file.DownloadSpeed = taskItem.SpeedDisplay;

                    if (
                        taskItem.State == TransferState.Transferring
                        && file.DownloadState != ResourceDownloadState.Downloading
                    )
                    {
                        file.DownloadState = ResourceDownloadState.Downloading;
                    }
                    else if (
                        taskItem.State == TransferState.Paused
                        && file.DownloadState != ResourceDownloadState.Paused
                    )
                    {
                        file.DownloadState = ResourceDownloadState.Paused;
                    }
                }
            }
        }

        #endregion

        #region 核心方法

        /// <summary>
        /// 扫描所有在线设备的共享文件
        /// </summary>
        private async Task ScanAllDevicesAsync()
        {
            try
            {
                IsLoading = true;
                _fileSource.Clear();
                ScannedDeviceCount = 0;
                StatusText = "正在扫描设备...";

                // 获取所有在线的 MachineInfoExtended
                var onlineDevices = _panelVM.GetOnlineDevices();
                OnlineDeviceCount = onlineDevices.Count;

                if (onlineDevices.Count == 0)
                {
                    StatusText = "没有在线设备";
                    return;
                }

                StatusText = $"正在扫描 {onlineDevices.Count} 台设备...";

                // 高并发扫描（20路并发 + 3秒超时），确保数百设备时快速完成
                var semaphore = new System.Threading.SemaphoreSlim(20, 20);
                var tasks = onlineDevices.Select(async device =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var ip = device.IPs?.FirstOrDefault() ?? device.ID;
                        var localIP = _panelVM.GetLocalIPForRemote(ip);
                        var isLocal = ip == localIP || ip == "127.0.0.1" || ip == "localhost";
                        var files = await _panelVM.RequestRemoteFileListAsync(ip, timeoutMs: 3000);

                        if (files.Length > 0)
                        {
                            var deviceName = device.Name ?? device.ID;
                            var items = files
                                .Select(
                                    f =>
                                        ResourceFileItem.FromSharedFileInfo(
                                            f,
                                            deviceName,
                                            ip,
                                            device.ID,
                                            isLocal
                                        )
                                )
                                .ToList();

                            // 在UI线程添加到SourceList（文件立即可见，无需等待全部扫描完成）
                            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                _fileSource.AddRange(items);
                                this.RaisePropertyChanged(nameof(TotalCount));
                            });
                        }

                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            ScannedDeviceCount++;
                            StatusText =
                                $"已扫描 {ScannedDeviceCount}/{OnlineDeviceCount} 台设备，共 {TotalCount} 个文件";
                        });
                    }
                    catch (Exception ex)
                    {
                        NLogger.Warn(
                            "[资源库] 扫描设备 {DeviceName} 失败: {ErrorMessage}",
                            device.Name ?? device.ID,
                            ex.Message
                        );
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            ScannedDeviceCount++;
                        });
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);

                // 检查本地已下载文件，通过MD5/文件大小恢复下载状态
                await CheckLocalDownloadedFilesAsync();

                StatusText = $"共 {TotalCount} 个文件，来自 {OnlineDeviceCount} 台设备";
            }
            catch (Exception ex)
            {
                NLogger.Error("[资源库] 扫描失败: {ErrorMessage}", ex.Message);
                StatusText = "扫描失败，请重试";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 批量下载：按设备分组，每个设备发一次请求包含所有文件名
        /// </summary>
        private async Task BatchDownloadAsync(List<ResourceFileItem> files)
        {
            try
            {
                // 先将所有文件标记为等待中，并在传输列表中创建占位符任务
                foreach (var file in files)
                {
                    file.DownloadState = ResourceDownloadState.Pending;
                    file.DownloadProgress = 0;

                    // 在TaskManager中创建占位符任务，让传输列表立即显示"等待中"
                    var placeholderId = Guid.NewGuid().ToString();
                    var placeholder = _panelVM.TaskManager.CreatePlaceholderTask(
                        placeholderId,
                        file.FileName,
                        TransferDirection.Download,
                        TransferTaskSource.RemoteBrowseDownload,
                        file.SourceDeviceName,
                        file.SourceDeviceIP,
                        "等待中"
                    );
                    // 设置占位符为Pending状态（CreatePlaceholderTask默认设为Transferring）
                    placeholder.State = TransferState.Pending;
                    file.TransferTaskId = placeholderId;
                }

                // 按来源设备分组
                var deviceGroups = files.GroupBy(f => f.SourceDeviceIP).ToList();

                foreach (var group in deviceGroups)
                {
                    var deviceIP = group.Key;
                    var deviceFiles = group.ToList();
                    var fileNames = deviceFiles.Select(f => f.FileName).ToArray();

                    try
                    {
                        // 一次性发送该设备所有文件的下载请求
                        await DownloadFilesFromDevice(deviceIP, fileNames);
                        NLogger.Info(
                            "[资源库] 批量请求 {DeviceIP} 推送 {FileCount} 个文件",
                            deviceIP,
                            fileNames.Length
                        );
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error(
                            "[资源库] 批量请求失败 ({DeviceIP}): {ErrorMessage}",
                            deviceIP,
                            ex.Message
                        );
                        foreach (var file in deviceFiles)
                        {
                            // 清理占位符任务
                            if (!string.IsNullOrEmpty(file.TransferTaskId))
                                _panelVM.TaskManager.RemoveTask(file.TransferTaskId);
                            file.DownloadState = ResourceDownloadState.Failed;
                            file.TransferTaskId = string.Empty;
                        }
                        continue;
                    }

                    // 为每个文件启动任务匹配（限制并发，避免同时数十个轮询）
                    foreach (var file in deviceFiles)
                    {
                        _ = MatchTransferTaskAsync(file);
                        // 每个文件间隔100ms启动匹配，避免并发风暴
                        await Task.Delay(100);
                    }
                }

                StatusText = $"已添加 {files.Count} 个文件到下载队列";
            }
            catch (Exception ex)
            {
                NLogger.Error("[资源库] 批量下载失败: {ErrorMessage}", ex.Message);
                StatusText = "批量下载请求失败";
            }
        }

        /// <summary>
        /// 启动单个文件下载
        /// </summary>
        private async Task StartDownloadAsync(ResourceFileItem file)
        {
            if (!file.CanDownload)
                return;

            try
            {
                file.DownloadState = ResourceDownloadState.Pending;
                file.DownloadProgress = 0;
                StatusText = $"正在请求下载 {file.FileName} ...";

                // 在TaskManager中创建占位符任务，让传输列表立即显示
                var placeholderId = Guid.NewGuid().ToString();
                var placeholder = _panelVM.TaskManager.CreatePlaceholderTask(
                    placeholderId,
                    file.FileName,
                    TransferDirection.Download,
                    TransferTaskSource.RemoteBrowseDownload,
                    file.SourceDeviceName,
                    file.SourceDeviceIP,
                    "等待中"
                );
                placeholder.State = TransferState.Pending;
                file.TransferTaskId = placeholderId;

                await DownloadFilesFromDevice(file.SourceDeviceIP, new[] { file.FileName });

                // 尝试通过文件名匹配到传输任务ID（延迟匹配，因为任务创建有延迟）
                _ = MatchTransferTaskAsync(file);
            }
            catch (Exception ex)
            {
                // 清理占位符
                if (!string.IsNullOrEmpty(file.TransferTaskId))
                    _panelVM.TaskManager.RemoveTask(file.TransferTaskId);
                file.TransferTaskId = string.Empty;
                file.DownloadState = ResourceDownloadState.Failed;
                NLogger.Error(
                    "[资源库] 下载请求失败 ({FileName}): {ErrorMessage}",
                    file.FileName,
                    ex.Message
                );
                StatusText = $"下载请求失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 延迟匹配传输任务ID
        /// </summary>
        private async Task MatchTransferTaskAsync(ResourceFileItem file)
        {
            var placeholderTaskId = file.TransferTaskId; // 记住占位符ID

            // 最多等5分钟让任务出现在TaskManager中（远端信号量可能排队等待）
            for (var i = 0; i < 600; i++)
            {
                await Task.Delay(500);

                // 如果SubscribeToTransferEvents已经更新了TransferTaskId（占位符被替换），说明真实任务已匹配
                if (
                    !string.IsNullOrEmpty(file.TransferTaskId)
                    && file.TransferTaskId != placeholderTaskId
                )
                {
                    // 已由事件订阅自动匹配，检查任务状态
                    var matchedTask = _panelVM.TaskManager.FindTask(file.TransferTaskId);
                    if (matchedTask != null)
                    {
                        NLogger.Info(
                            "[资源库] 事件订阅已匹配传输任务: {FileName} -> {TransferTaskId}",
                            file.FileName,
                            file.TransferTaskId
                        );
                        if (matchedTask.IsFinished)
                        {
                            HandleTaskCompletion(file, matchedTask);
                        }
                        else if (
                            file.DownloadState != ResourceDownloadState.Downloading
                            && matchedTask.State == TransferState.Transferring
                        )
                        {
                            file.DownloadState = ResourceDownloadState.Downloading;
                        }
                    }
                    return;
                }

                // 主动查找非Pending的真实传输任务（跳过我们自己创建的占位符）
                var taskItem = _panelVM.TaskManager.FindTaskByFileName(file.FileName);
                if (
                    taskItem != null
                    && taskItem.TaskId != placeholderTaskId
                    && taskItem.State != TransferState.Pending
                )
                {
                    file.TransferTaskId = taskItem.TaskId;
                    NLogger.Info(
                        "[资源库] 已匹配传输任务: {FileName} -> {TaskId}",
                        file.FileName,
                        taskItem.TaskId
                    );

                    // 清理占位符
                    if (!string.IsNullOrEmpty(placeholderTaskId))
                    {
                        _panelVM.TaskManager.RemoveTask(placeholderTaskId);
                    }

                    // 检查任务是否已经完成（快速传输场景）
                    if (taskItem.IsFinished)
                    {
                        HandleTaskCompletion(file, taskItem);
                    }
                    else
                    {
                        file.DownloadState = ResourceDownloadState.Downloading;
                    }
                    return;
                }

                // 如果已取消或失败则退出
                if (
                    file.DownloadState == ResourceDownloadState.None
                    || file.DownloadState == ResourceDownloadState.Failed
                    || file.DownloadState == ResourceDownloadState.Completed
                )
                {
                    return;
                }

                // 每10秒检查一次文件是否已直接到达（避免过于频繁的文件系统访问）
                if (i > 0 && i % 20 == 0)
                {
                    var receivedPath = Path.Combine(AppPathes.ReceivedFilesDir, file.FileName);
                    if (File.Exists(receivedPath))
                    {
                        file.DownloadState = ResourceDownloadState.Completed;
                        file.DownloadProgress = 100;
                        file.LocalFilePath = receivedPath;
                        file.DownloadSpeed = string.Empty;
                        StatusText = $"{file.FileName} 下载完成";

                        if (file.IsPatch)
                        {
                            _ = ApplyNodePackageAfterDownloadAsync(file);
                        }
                        else if (file.IsPackage)
                        {
                            _ = DeployPackageAfterDownloadAsync(file);
                        }
                        return;
                    }
                }
            }

            // 超时未匹配到任务 — 检查文件是否已直接到达
            var finalPath = Path.Combine(AppPathes.ReceivedFilesDir, file.FileName);
            if (File.Exists(finalPath))
            {
                file.DownloadState = ResourceDownloadState.Completed;
                file.DownloadProgress = 100;
                file.LocalFilePath = finalPath;
                file.DownloadSpeed = string.Empty;
                StatusText = $"{file.FileName} 下载完成";

                if (file.IsPatch)
                {
                    _ = ApplyNodePackageAfterDownloadAsync(file);
                }
                else if (file.IsPackage)
                {
                    _ = DeployPackageAfterDownloadAsync(file);
                }
            }
            else
            {
                NLogger.Warn("[资源库] 传输任务匹配超时: {FileName}", file.FileName);
                // 保持Pending状态，让用户可以重新下载
                file.DownloadState = ResourceDownloadState.Failed;
                file.DownloadSpeed = string.Empty;
                StatusText = $"{file.FileName} 等待超时";
            }
        }

        /// <summary>
        /// 向指定设备发送文件下载请求（委托给 DaemonPanelViewModel 共享方法）
        /// </summary>
        private async Task DownloadFilesFromDevice(string remoteIP, string[] fileNames)
        {
            await _panelVM.RequestPushDownloadAsync(remoteIP, fileNames);
        }

        /// <summary>
        /// 进程包下载完成后自动弹出导入对话框
        /// </summary>
        private async Task DeployPackageAfterDownloadAsync(ResourceFileItem file)
        {
            try
            {
                var receivedPath = file.LocalFilePath;
                if (string.IsNullOrEmpty(receivedPath) || !File.Exists(receivedPath))
                {
                    receivedPath = Path.Combine(AppPathes.ReceivedFilesDir, file.FileName);
                }

                if (!File.Exists(receivedPath))
                {
                    NLogger.Warn("[资源库] 部署文件不存在: {ReceivedPath}", receivedPath);
                    return;
                }

                NLogger.Info("[资源库] 进程包已下载，自动打开导入对话框: {ReceivedPath}", receivedPath);

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var mainWindow = System.Windows.Application.Current.MainWindow;
                    var importDialog = new ImportDialog(receivedPath) { Owner = mainWindow };
                    importDialog.ShowDialog();

                    if (importDialog.DialogResult == true)
                    {
                        StatusText = $"已成功部署 {file.FileName}";
                        NLogger.Info("[资源库] 部署完成: {FileName}", file.FileName);

                        // 通知主窗口重新加载配置
                        ReactiveUI.MessageBus.Current.SendMessage("ReloadConfig");
                    }
                    else
                    {
                        StatusText = "部署已取消";
                    }
                });
            }
            catch (Exception ex)
            {
                NLogger.Error("[资源库] 部署失败 ({FileName}): {ErrorMessage}", file.FileName, ex.Message);
                StatusText = $"部署失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 节点更新包（NodeFull / NodePatch）下载完成后自动弹出应用对话框
        /// </summary>
        private async Task ApplyNodePackageAfterDownloadAsync(ResourceFileItem file)
        {
            try
            {
                var receivedPath = file.LocalFilePath;
                if (string.IsNullOrEmpty(receivedPath) || !File.Exists(receivedPath))
                {
                    receivedPath = Path.Combine(AppPathes.ReceivedFilesDir, file.FileName);
                }

                if (!File.Exists(receivedPath))
                {
                    NLogger.Warn("[资源库] 更新包文件不存在: {ReceivedPath}", receivedPath);
                    return;
                }

                NLogger.Info("[资源库] 节点更新包已下载，打开应用对话框: {ReceivedPath}", receivedPath);

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // 获取进程树节点列表
                    var mainWindow =
                        System.Windows.Application.Current.MainWindow as DaemonKit.MainWindow;
                    var allNodes = new System.Collections.Generic.List<ProcessItem>();
                    ProcessItem rootNode = null;
                    if (mainWindow?.ViewModel?.RootProcessNode != null)
                    {
                        allNodes = mainWindow.ViewModel.RootProcessNode.AllChildren();
                        rootNode = mainWindow.ViewModel.RootProcessNode;
                    }

                    var dialog = new Views.NodePackageDialog(receivedPath, allNodes, rootNode)
                    {
                        Owner = System.Windows.Application.Current.MainWindow
                    };
                    dialog.ShowDialog();

                    if (dialog.DialogResult == true)
                    {
                        StatusText = $"已成功应用更新 {file.FileName}";
                        NLogger.Info("[资源库] 更新完成: {FileName}", file.FileName);
                    }
                    else
                    {
                        StatusText = "更新已取消";
                    }
                });
            }
            catch (Exception ex)
            {
                NLogger.Error(
                    "[资源库] 应用更新失败 ({FileName}): {ErrorMessage}",
                    file.FileName,
                    ex.Message
                );
                StatusText = $"更新失败: {ex.Message}";
            }
        }

        /// <summary>更新选中计数</summary>
        private void UpdateSelectionCount()
        {
            var count = _filteredFiles.Count(f => f.IsSelected);
            if (count != _selectedCount)
            {
                SelectedCount = count;
                this.RaisePropertyChanged(nameof(HasSelection));
            }
            // 同步全选状态
            var allSelected = _filteredFiles.Count > 0 && count == _filteredFiles.Count;
            if (_isAllSelected != allSelected && !_isUpdatingAllSelected)
            {
                _isAllSelected = allSelected;
                this.RaisePropertyChanged(nameof(IsAllSelected));
            }
        }

        /// <summary>
        /// 检查本地已下载的文件，通过MD5校验恢复下载完成状态
        /// </summary>
        private async Task CheckLocalDownloadedFilesAsync()
        {
            var receivedDir = AppPathes.ReceivedFilesDir;
            if (!Directory.Exists(receivedDir))
                return;

            var itemsToCheck = _fileSource.Items.ToList();

            // 在后台线程进行MD5计算
            var matchResults = await Task.Run(() =>
            {
                var results = new List<(ResourceFileItem file, string localPath)>();
                foreach (var file in itemsToCheck)
                {
                    // 跳过已有下载状态的文件
                    if (file.DownloadState != ResourceDownloadState.None)
                        continue;

                    var localPath = Path.Combine(receivedDir, file.FileName);
                    if (!File.Exists(localPath))
                        continue;

                    var localInfo = new FileInfo(localPath);

                    // 优先使用MD5校验
                    if (!string.IsNullOrEmpty(file.RemoteMD5))
                    {
                        var localMD5 = ComputeFileMD5(localPath);
                        if (localMD5.Equals(file.RemoteMD5, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add((file, localPath));
                        }
                        else
                        {
                            NLogger.Info("[资源库] 本地文件MD5不匹配，可能已过期: {FileName}", file.FileName);
                        }
                    }
                    else if (localInfo.Length == file.FileSize)
                    {
                        // 无MD5时使用文件大小比对
                        results.Add((file, localPath));
                    }
                }
                return results;
            });

            // 在UI线程更新状态
            foreach (var (file, localPath) in matchResults)
            {
                file.DownloadState = ResourceDownloadState.Completed;
                file.DownloadProgress = 100;
                file.LocalFilePath = localPath;
            }

            if (matchResults.Count > 0)
            {
                NLogger.Info("[资源库] 通过本地校验恢复 {MatchCount} 个文件的下载状态", matchResults.Count);
            }
        }

        /// <summary>
        /// 计算文件MD5哈希值
        /// </summary>
        private static string ComputeFileMD5(string filePath)
        {
            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(filePath);
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                NLogger.Warn(
                    "[资源库] 计算文件MD5失败 ({FileName}): {ErrorMessage}",
                    Path.GetFileName(filePath),
                    ex.Message
                );
                return string.Empty;
            }
        }

        #endregion

        #region 过滤器

        private static Func<ResourceFileItem, bool> BuildSearchPredicate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return _ => true;

            var lower = text.Trim().ToLowerInvariant();
            return item =>
                (item.FileName?.ToLowerInvariant().Contains(lower) ?? false)
                || (item.SourceDeviceName?.ToLowerInvariant().Contains(lower) ?? false);
        }

        private static Func<ResourceFileItem, bool> BuildCategoryPredicate(string? category)
        {
            if (string.IsNullOrWhiteSpace(category) || category == "全部")
                return _ => true;

            return item => item.CategoryText == category;
        }

        #endregion

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
