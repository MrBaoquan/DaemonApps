using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Models;
using DaemonKit.Services;
using DynamicData;
using DynamicData.Binding;
using Microsoft.Win32;
using Newtonsoft.Json;
using ReactiveUI;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 联调面板ViewModel - 支持分页、搜索、文件传输
    /// </summary>
    public class DaemonPanelViewModel : ReactiveObject, IDisposable
    {
        #region 服务引用

        private readonly P2PFileTransferService _transferService;

        /// <summary>
        /// 对外暴露传输服务实例（供MainWindow的PUSH处理器使用）
        /// </summary>
        public P2PFileTransferService TransferService => _transferService;
        private readonly TransferTaskManager _taskManager;
        private readonly IDisposable _cleanUp;

        /// <summary>导出完成事件流（替代 TCS 字典，外部回调推送事件，内部 Rx 管道订阅）</summary>
        private readonly System.Reactive.Subjects.Subject<(
            string TaskId,
            bool Success,
            string PackageFileName
        )> _exportCompletedSubject = new();

        /// <summary>UDP文件列表响应事件流</summary>
        private readonly System.Reactive.Subjects.Subject<(
            string RequestId,
            SharedFileInfo[] Files
        )> _fileListResponseSubject = new();

        /// <summary>远程包任务取消令牌（以taskId为Key）</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            System.Threading.CancellationTokenSource
        > _packageTaskCancellations = new();

        /// <summary>批量下载并发控制（最多3个并发下载）</summary>
        private readonly System.Threading.SemaphoreSlim _batchDownloadSemaphore = new(3, 3);

        #endregion

        #region 设备列表与缓存

        /// <summary>设备数据源缓存</summary>
        private readonly SourceCache<MachineInfo, string> _machineCache;

        // 设备缓存持久化
        private readonly string _deviceCacheFilePath;
        private readonly Subject<Unit> _deviceCacheSaveSubject = new();

        // 传输任务管理由 TransferTaskManager 统一管理

        /// <summary>远程包任务缓存</summary>
        private readonly SourceCache<RemotePackageTask, string> _packageTaskCache;

        /// <summary>分页后的设备列表（UI绑定）</summary>
        private readonly ReadOnlyObservableCollection<MachineInfo> _pagedMachines;
        public ReadOnlyObservableCollection<MachineInfo> PagedMachines => _pagedMachines;

        /// <summary>活跃的传输任务（UI绑定，委托给TaskManager）</summary>
        public ReadOnlyObservableCollection<TransferTaskItem> ActiveTransfers =>
            _taskManager.ActiveTasks;

        /// <summary>已完成的传输任务（UI绑定）</summary>
        public ReadOnlyObservableCollection<TransferTaskItem> CompletedTransfers =>
            _taskManager.CompletedTasks;

        /// <summary>远程包任务列表（UI绑定）</summary>
        private readonly ReadOnlyObservableCollection<RemotePackageTask> _packageTasks;
        public ReadOnlyObservableCollection<RemotePackageTask> PackageTasks => _packageTasks;

        #endregion

        #region 分页属性

        private int _pageSize = 10;

        /// <summary>每页显示条数</summary>
        public int PageSize
        {
            get => _pageSize;
            set => this.RaiseAndSetIfChanged(ref _pageSize, value);
        }

        private int _currentPage = 1;

        /// <summary>当前页码</summary>
        public int CurrentPage
        {
            get => _currentPage;
            set => this.RaiseAndSetIfChanged(ref _currentPage, Math.Max(1, value));
        }

        private int _totalPages = 1;

        /// <summary>总页数</summary>
        public int TotalPages
        {
            get => _totalPages;
            set => this.RaiseAndSetIfChanged(ref _totalPages, value);
        }

        private int _totalDevices;

        /// <summary>设备总数</summary>
        public int TotalDevices
        {
            get => _totalDevices;
            set => this.RaiseAndSetIfChanged(ref _totalDevices, value);
        }

        private int _filteredCount;

        /// <summary>过滤后的设备数</summary>
        public int FilteredCount
        {
            get => _filteredCount;
            set => this.RaiseAndSetIfChanged(ref _filteredCount, value);
        }

        private int _selectedCount;

        /// <summary>选中的设备数</summary>
        public int SelectedCount
        {
            get => _selectedCount;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedCount, value);
                this.RaisePropertyChanged(nameof(HasSelection));
                // 同步全选复选框状态（避免递归：仅更新backing field）
                var total = _machineCache.Items.OfType<MachineInfoExtended>().Count();
                var allSelected = total > 0 && value == total;
                if (_isAllSelected != allSelected)
                {
                    _isAllSelected = allSelected;
                    this.RaisePropertyChanged(nameof(IsAllSelected));
                }
            }
        }

        /// <summary>是否有选中的设备</summary>
        public bool HasSelection => _selectedCount > 0;

        private bool _isAllSelected;

        /// <summary>表头全选复选框状态</summary>
        public bool IsAllSelected
        {
            get => _isAllSelected;
            set
            {
                if (_isAllSelected == value)
                    return;
                this.RaiseAndSetIfChanged(ref _isAllSelected, value);
                // 全选/取消全选当前页所有设备
                foreach (var machine in _machineCache.Items.OfType<MachineInfoExtended>())
                {
                    machine.IsSelected = value;
                }
                SelectedCount = value
                    ? _machineCache.Items.OfType<MachineInfoExtended>().Count()
                    : 0;
            }
        }

        #endregion

        #region 搜索与过滤

        private string _searchText = string.Empty;

        /// <summary>搜索文本</summary>
        public string SearchText
        {
            get => _searchText;
            set => this.RaiseAndSetIfChanged(ref _searchText, value);
        }

        private MachineStatus? _statusFilter = null;

        /// <summary>状态过滤器</summary>
        public MachineStatus? StatusFilter
        {
            get => _statusFilter;
            set => this.RaiseAndSetIfChanged(ref _statusFilter, value);
        }

        /// <summary>状态过滤选项列表</summary>
        public ObservableCollection<StatusFilterOption> StatusFilterOptions { get; } =
            new()
            {
                new StatusFilterOption { DisplayName = "全部", Value = null },
                new StatusFilterOption { DisplayName = "在线", Value = MachineStatus.Online },
                new StatusFilterOption { DisplayName = "离线", Value = MachineStatus.Offline },
                new StatusFilterOption { DisplayName = "忙碌", Value = MachineStatus.Busy }
            };

        private StatusFilterOption _selectedStatusOption;

        /// <summary>选中的状态过滤选项</summary>
        public StatusFilterOption SelectedStatusOption
        {
            get => _selectedStatusOption;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedStatusOption, value);
                StatusFilter = value?.Value;
            }
        }

        #endregion

        #region 选中项

        private MachineInfo _selectedMachine;

        /// <summary>当前选中的设备</summary>
        public MachineInfo SelectedMachine
        {
            get => _selectedMachine;
            set => this.RaiseAndSetIfChanged(ref _selectedMachine, value);
        }

        private string _newDeviceIp = string.Empty;

        /// <summary>手动添加设备IP</summary>
        public string NewDeviceIp
        {
            get => _newDeviceIp;
            set => this.RaiseAndSetIfChanged(ref _newDeviceIp, value);
        }

        #endregion

        #region Events

        /// <summary>
        /// 请求显示传输列表窗口（下载开始时自动触发，类似浏览器下载行为）
        /// 参数为Tab索引：0=上传, 1=下载
        /// </summary>
        public event Action<int> ShowTransferListRequested;

        #endregion

        #region 每页条数选项

        public int[] PageSizeOptions { get; } = { 10, 20, 50, 100 };

        #endregion

        #region Commands

        /// <summary>下一页</summary>
        public ReactiveCommand<Unit, Unit> NextPageCommand { get; }

        /// <summary>上一页</summary>
        public ReactiveCommand<Unit, Unit> PrevPageCommand { get; }

        /// <summary>跳转到指定页</summary>
        public ReactiveCommand<int, Unit> GoToPageCommand { get; }

        /// <summary>刷新设备列表</summary>
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        /// <summary>远程连接</summary>
        public ReactiveCommand<MachineInfo, Unit> ConnectCommand { get; }

        /// <summary>远程关机</summary>
        public ReactiveCommand<MachineInfo, Unit> ShutdownCommand { get; }

        /// <summary>远程重启</summary>
        public ReactiveCommand<MachineInfo, Unit> RestartCommand { get; }

        /// <summary>重启进程树</summary>
        public ReactiveCommand<MachineInfo, Unit> RestartNodeTreeCommand { get; }

        /// <summary>发送文件</summary>
        public ReactiveCommand<MachineInfo, Unit> SendFilesCommand { get; }

        /// <summary>浏览远程设备共享文件（暂未实现远程浏览，先打开本地共享目录）</summary>
        public ReactiveCommand<MachineInfo, Unit> BrowseFilesCommand { get; }

        /// <summary>打开本地共享文件夹</summary>
        public ReactiveCommand<Unit, Unit> OpenSharedFolderCommand { get; }

        /// <summary>添加设备（手动IP）</summary>
        public ReactiveCommand<Unit, Unit> AddDeviceByIpCommand { get; }

        /// <summary>移除设备</summary>
        public ReactiveCommand<MachineInfo, Unit> RemoveDeviceCommand { get; }

        /// <summary>清空设备缓存</summary>
        public ReactiveCommand<Unit, Unit> ClearDeviceCacheCommand { get; }

        /// <summary>暂停传输</summary>
        public ReactiveCommand<TransferTaskItem, Unit> PauseTransferCommand { get; }

        /// <summary>恢复传输</summary>
        public ReactiveCommand<TransferTaskItem, Unit> ResumeTransferCommand { get; }

        /// <summary>取消传输</summary>
        public ReactiveCommand<TransferTaskItem, Unit> CancelTransferCommand { get; }

        /// <summary>打开传输列表窗口</summary>
        public ReactiveCommand<Unit, Unit> OpenTransferListCommand { get; }

        /// <summary>清除已完成的传输任务</summary>
        public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

        /// <summary>下载远程进程包（非阻塞，添加到任务队列）</summary>
        public ReactiveCommand<MachineInfo, Unit> DownloadPackageCommand { get; }

        /// <summary>批量下载远程进程包</summary>
        public ReactiveCommand<Unit, Unit> BatchDownloadPackagesCommand { get; }

        /// <summary>全选在线设备</summary>
        public ReactiveCommand<Unit, Unit> SelectAllOnlineCommand { get; }

        /// <summary>取消全选</summary>
        public ReactiveCommand<Unit, Unit> DeselectAllCommand { get; }

        /// <summary>批量关机（选中设备）</summary>
        public ReactiveCommand<Unit, Unit> BatchShutdownCommand { get; }

        /// <summary>批量重启系统（选中设备）</summary>
        public ReactiveCommand<Unit, Unit> BatchRestartCommand { get; }

        /// <summary>批量重启软件（选中设备）</summary>
        public ReactiveCommand<Unit, Unit> BatchRestartSoftwareCommand { get; }

        /// <summary>取消远程包任务</summary>
        public ReactiveCommand<RemotePackageTask, Unit> CancelPackageTaskCommand { get; }

        /// <summary>重试远程包任务</summary>
        public ReactiveCommand<RemotePackageTask, Unit> RetryPackageTaskCommand { get; }

        /// <summary>清除已完成的远程包任务</summary>
        public ReactiveCommand<Unit, Unit> ClearCompletedPackageTasksCommand { get; }

        /// <summary>查看硬件详情</summary>
        public ReactiveCommand<MachineInfo, Unit> ShowHardwareInfoCommand { get; }

        /// <summary>是否没有传输任务</summary>
        public bool IsEmpty => ActiveTransfers.Count == 0;

        /// <summary>是否有远程包任务</summary>
        public bool HasPackageTasks => PackageTasks.Count > 0;

        /// <summary>总传输进度（委托给TaskManager）</summary>
        public double TotalProgress => _taskManager.TotalProgress;

        /// <summary>总速度显示</summary>
        public string TotalSpeedDisplay => _taskManager.TotalSpeedDisplay;

        /// <summary>活跃传输数</summary>
        public int ActiveTransferCount => _taskManager.ActiveCount;

        /// <summary>传输任务管理器（公开给子ViewModel使用）</summary>
        public TransferTaskManager TaskManager => _taskManager;

        #endregion

        #region 构造函数

        public DaemonPanelViewModel()
            : this(new P2PFileTransferService(), null) { }

        public DaemonPanelViewModel(
            P2PFileTransferService transferService,
            TransferTaskManager? taskManager = null
        )
        {
            _transferService = transferService;
            _taskManager = taskManager ?? new TransferTaskManager();
            _machineCache = new SourceCache<MachineInfo, string>(m => m.ID);
            _packageTaskCache = new SourceCache<RemotePackageTask, string>(t => t.TaskId);
            _selectedStatusOption = StatusFilterOptions[0];
            _deviceCacheFilePath = Utilities.AppPathes.DeviceCachePath;

            // 加载本地缓存的设备
            LoadDeviceCache();

            // 缓存保存节流（避免频繁写磁盘）
            _deviceCacheSaveSubject
                .Throttle(TimeSpan.FromSeconds(2))
                .ObserveOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
                .Subscribe(_ => SaveDeviceCacheInternal());

            // 1. 构建搜索过滤器（带防抖）
            var searchFilter = this.WhenAnyValue(x => x.SearchText)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Select(BuildSearchPredicate);

            // 2. 构建状态过滤器
            var statusFilter = this.WhenAnyValue(x => x.StatusFilter).Select(BuildStatusPredicate);

            // 3. 分页参数变化
            var pageChanged = this.WhenAnyValue(x => x.CurrentPage, x => x.PageSize)
                .Select(tuple => new PageRequest(tuple.Item1, tuple.Item2));

            // 4. 构建设备列表管道：过滤 → 排序 → 统计 → 分页
            var machineListSubscription = _machineCache
                .Connect()
                .AutoRefreshOnObservable(
                    m =>
                        m is MachineInfoExtended ext
                            ? Observable
                                .FromEventPattern<
                                    System.ComponentModel.PropertyChangedEventHandler,
                                    System.ComponentModel.PropertyChangedEventArgs
                                >(h => ext.PropertyChanged += h, h => ext.PropertyChanged -= h)
                                .Where(
                                    e =>
                                        e.EventArgs.PropertyName
                                        == nameof(MachineInfoExtended.Status)
                                )
                                .Select(_ => m)
                            : Observable.Empty<MachineInfo>()
                )
                .Filter(searchFilter)
                .Filter(statusFilter)
                .Sort(SortExpressionComparer<MachineInfo>.Ascending(m => m.Name ?? m.ID))
                .Do(changeSet =>
                {
                    // 更新统计信息（必须在UI线程执行，因为设置TotalPages等属性会触发
                    // ReactiveCommand.CanExecute → ButtonBase.UpdateCanExecute → WPF线程检查）
                    var totalFiltered = _machineCache.Items.Count(
                        m => MatchesSearch(m, SearchText) && MatchesStatus(m, StatusFilter)
                    );
                    var totalDevices = _machineCache.Count;

                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        FilteredCount = totalFiltered;
                        TotalDevices = totalDevices;
                        TotalPages = Math.Max(
                            1,
                            (int)Math.Ceiling((double)totalFiltered / PageSize)
                        );

                        // 确保当前页有效
                        if (CurrentPage > TotalPages)
                        {
                            CurrentPage = TotalPages;
                        }
                    });
                })
                .Page(pageChanged)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _pagedMachines)
                .Subscribe();

            // 5. 传输任务列表由 TransferTaskManager 统一管理

            // 5.1 构建远程包任务列表管道
            var packageTaskSubscription = _packageTaskCache
                .Connect()
                .Sort(SortExpressionComparer<RemotePackageTask>.Descending(t => t.CreatedTime))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _packageTasks)
                .Subscribe();

            // 6. 传输服务事件订阅由MainWindow统一处理（避免重复TrackTask）
            //    MainWindow订阅 TransferProgress/Completed/Failed → _taskManager.TrackTask/CompleteTask
            //    此处仅保留UI统计刷新

            // 6.1 定时刷新UI统计属性（仅在有活跃传输时刷新速度和进度）
            var statsRefreshSubscription = System.Reactive.Linq.Observable
                .Interval(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    if (_taskManager.ActiveCount > 0)
                    {
                        this.RaisePropertyChanged(nameof(TotalProgress));
                        this.RaisePropertyChanged(nameof(TotalSpeedDisplay));
                        this.RaisePropertyChanged(nameof(ActiveTransferCount));
                    }
                    this.RaisePropertyChanged(nameof(IsEmpty));
                    this.RaisePropertyChanged(nameof(HasPackageTasks));

                    // 更新选中设备计数（500ms轮询，比逐个监听PropertyChanged更高效）
                    // 注意：直接更新backing field并通知，避免触发setter中的IsAllSelected同步逻辑
                    var newSelectedCount = _machineCache.Items
                        .OfType<MachineInfoExtended>()
                        .Count(m => m.IsSelected);
                    if (newSelectedCount != _selectedCount)
                    {
                        _selectedCount = newSelectedCount;
                        this.RaisePropertyChanged(nameof(SelectedCount));
                        this.RaisePropertyChanged(nameof(HasSelection));
                    }
                });

            // 7. 初始化分页命令
            var canGoNext = this.WhenAnyValue(
                x => x.CurrentPage,
                x => x.TotalPages,
                (current, total) => current < total
            );
            var canGoPrev = this.WhenAnyValue(x => x.CurrentPage, current => current > 1);

            NextPageCommand = ReactiveCommand.Create(
                () =>
                {
                    CurrentPage++;
                },
                canGoNext
            );
            PrevPageCommand = ReactiveCommand.Create(
                () =>
                {
                    CurrentPage--;
                },
                canGoPrev
            );
            GoToPageCommand = ReactiveCommand.Create<int>(page =>
            {
                CurrentPage = Math.Clamp(page, 1, TotalPages);
            });

            RefreshCommand = ReactiveCommand.Create(() =>
            {
                // 触发UI刷新
                _machineCache.Refresh();
            });

            // 8. 初始化设备操作命令
            ConnectCommand = ReactiveCommand.Create<MachineInfo>(machine =>
            {
                var ip = machine.IPs?.FirstOrDefault() ?? machine.ID;
                // 使用VNC连接（vncviewer.exe需要在PATH中或指定完整路径）
                DNHper.WinAPI.OpenProcess("vncviewer.exe", ip);
            });

            ShutdownCommand = ReactiveCommand.Create<MachineInfo>(machine =>
            {
                SendCommandToMachine(machine, Models.Command.SHUTDOWN);
            });

            RestartCommand = ReactiveCommand.Create<MachineInfo>(machine =>
            {
                SendCommandToMachine(machine, Models.Command.RESTART);
            });

            RestartNodeTreeCommand = ReactiveCommand.Create<MachineInfo>(machine =>
            {
                SendCommandToMachine(machine, Models.Command.RESTART_NODE_TREE);
            });

            SendFilesCommand = ReactiveCommand.Create<MachineInfo>(machine =>
            {
                var dialog = new OpenFileDialog
                {
                    Multiselect = true,
                    Title = "选择要发送的文件",
                    Filter = "所有文件|*.*"
                };

                if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
                {
                    // 自动弹出传输列表并激活上传Tab
                    ShowTransferListRequested?.Invoke(0);

                    var files = dialog.FileNames;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _transferService.SendFilesAsync(machine, files);
                            DNHper.NLogger.Info($"[P2P] 文件发送完成");
                        }
                        catch (Exception ex)
                        {
                            DNHper.NLogger.Error($"[P2P] 文件发送失败: {ex.Message}");
                        }
                    });
                }
            });

            // 浏览远程设备共享文件（通过UDP获取文件列表，避免TCP防火墙阻断）
            BrowseFilesCommand = ReactiveCommand.CreateFromTask<MachineInfo>(async machine =>
            {
                try
                {
                    var ip = machine.IPs?.FirstOrDefault() ?? machine.ID;
                    DNHper.NLogger.Info($"[P2P] 正在打开远程文件浏览器: {machine.Name ?? machine.ID} ({ip})");

                    // 创建ViewModel（传入UDP文件列表提供者和UDP下载回调）
                    var vm = new RemoteFileBrowserViewModel(
                        machine,
                        async () => await RequestRemoteFileListAsync(ip),
                        async (m, fileNames) =>
                        {
                            await RequestPushDownloadAsync(ip, fileNames);
                        }
                    );

                    // 显示对话框
                    var window = new Views.RemoteFileBrowserWindow(vm);
                    window.Owner = System.Windows.Application.Current.MainWindow;

                    // 窗口加载后触发远程文件加载
                    window.Loaded += async (s, e) =>
                    {
                        await vm.RefreshCommand.Execute();
                    };

                    window.ShowDialog();
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Error($"[P2P] 浏览远程文件失败: {ex.Message}");
                }
            });

            // 打开本地共享文件夹
            OpenSharedFolderCommand = ReactiveCommand.Create(() =>
            {
                _transferService.OpenSharedFolder();
            });

            AddDeviceByIpCommand = ReactiveCommand.Create(() =>
            {
                var ipText = NewDeviceIp?.Trim();
                if (string.IsNullOrEmpty(ipText))
                    return;

                if (!IPAddress.TryParse(ipText, out _))
                {
                    MessageBox.Show(
                        "请输入有效的IP地址",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return;
                }

                var manualDevice = new MachineInfoExtended
                {
                    ID = ipText,
                    Name = ipText,
                    IPs = new ObservableCollection<string> { ipText },
                    Status = MachineStatus.Offline,
                    LastSeen = DateTime.MinValue,
                    IsManuallyAdded = true
                };

                AddOrUpdateMachine(manualDevice);
                NewDeviceIp = string.Empty;
            });

            RemoveDeviceCommand = ReactiveCommand.Create<MachineInfo>(machine =>
            {
                if (machine == null || string.IsNullOrEmpty(machine.ID))
                    return;
                RemoveMachine(machine.ID);
            });

            ClearDeviceCacheCommand = ReactiveCommand.Create(() =>
            {
                var result = MessageBox.Show(
                    "确定清空本地缓存设备列表吗？",
                    "确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );
                if (result != MessageBoxResult.Yes)
                    return;

                _machineCache.Clear();
                SaveDeviceCache();
            });

            // 9. 初始化传输控制命令
            PauseTransferCommand = ReactiveCommand.Create<TransferTaskItem>(task =>
            {
                _transferService.PauseTask(task.TaskId);
                _taskManager.PauseTask(task.TaskId);
            });

            ResumeTransferCommand = ReactiveCommand.CreateFromTask<TransferTaskItem>(async task =>
            {
                if (_transferService.ActiveTasks.TryGetValue(task.TaskId, out var serviceTask))
                {
                    try
                    {
                        await _transferService.ResumeTaskAsync(serviceTask);
                        _taskManager.ResumeTask(task.TaskId);
                    }
                    catch (Exception ex)
                    {
                        DNHper.NLogger.Error($"[P2P] 恢复任务失败: {ex.Message}");
                    }
                }
            });

            CancelTransferCommand = ReactiveCommand.Create<TransferTaskItem>(task =>
            {
                _transferService.CancelTask(task.TaskId);
                _taskManager.CancelTask(task.TaskId);
            });

            // 10. 传输列表窗口相关命令
            OpenTransferListCommand = ReactiveCommand.Create(() =>
            {
                var vm = new TransferListViewModel(_taskManager, _transferService);
                var window = new Views.TransferListWindow(vm);
                window.Owner = System.Windows.Application.Current.MainWindow;
                window.Show();
            });

            ClearCompletedCommand = ReactiveCommand.Create(() =>
            {
                _taskManager.ClearCompleted();
            });

            // 11. 下载远程进程包 - 非阻塞，添加到任务队列
            DownloadPackageCommand = ReactiveCommand.Create<MachineInfo>(machine =>
            {
                var ip = machine.IPs?.FirstOrDefault() ?? machine.ID;
                var machineName = machine.Name ?? machine.ID;

                // 创建远程包任务并加入队列
                var task = new RemotePackageTask(machineName, ip);
                _packageTaskCache.AddOrUpdate(task);

                DNHper.NLogger.Info($"[P2P] 创建远程包下载任务: {machineName} ({ip})");

                // 异步执行任务（不阻塞UI）
                _ = ExecuteRemotePackageTaskAsync(task);

                // 自动弹出传输列表并激活下载Tab（类似浏览器下载行为）
                ShowTransferListRequested?.Invoke(1);
            });

            // 12. 批量下载远程进程包（基于选中设备）
            BatchDownloadPackagesCommand = ReactiveCommand.Create(() =>
            {
                var selectedMachines = _machineCache.Items
                    .OfType<MachineInfoExtended>()
                    .Where(m => m.IsSelected)
                    .ToList();

                if (selectedMachines.Count == 0)
                {
                    MessageBox.Show(
                        "请先勾选要下载进程包的设备。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                // 自动过滤离线设备
                var onlineMachines = selectedMachines
                    .Where(m => m.Status == MachineStatus.Online)
                    .ToList();
                var skippedCount = selectedMachines.Count - onlineMachines.Count;

                if (onlineMachines.Count == 0)
                {
                    MessageBox.Show(
                        "选中的设备均为离线状态，无法执行下载。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                var msg = $"将从 {onlineMachines.Count} 台在线设备下载进程包";
                if (skippedCount > 0)
                    msg += $"（已自动跳过 {skippedCount} 台离线设备）";
                msg += "，是否继续？";

                var result = MessageBox.Show(
                    msg,
                    "批量下载进程包",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result != MessageBoxResult.Yes)
                    return;

                foreach (var machine in onlineMachines)
                {
                    var ip = machine.IPs?.FirstOrDefault() ?? machine.ID;
                    var machineName = machine.Name ?? machine.ID;

                    var task = new RemotePackageTask(machineName, ip);
                    _packageTaskCache.AddOrUpdate(task);

                    DNHper.NLogger.Info($"[P2P] 批量任务 - 创建远程包下载任务: {machineName} ({ip})");

                    _ = ThrottledExecuteRemotePackageTaskAsync(task);
                }

                // 自动弹出传输列表并激活下载Tab（类似浏览器下载行为）
                ShowTransferListRequested?.Invoke(1);
            });

            // 12.1 全选在线设备
            SelectAllOnlineCommand = ReactiveCommand.Create(() =>
            {
                foreach (var machine in _machineCache.Items.OfType<MachineInfoExtended>())
                {
                    machine.IsSelected = machine.Status == MachineStatus.Online;
                }
                // 立即更新计数
                SelectedCount = _machineCache.Items
                    .OfType<MachineInfoExtended>()
                    .Count(m => m.IsSelected);
            });

            // 12.2 取消全选
            DeselectAllCommand = ReactiveCommand.Create(() =>
            {
                foreach (var machine in _machineCache.Items.OfType<MachineInfoExtended>())
                {
                    machine.IsSelected = false;
                }
                SelectedCount = 0;
            });

            // 12.3 批量关机（选中设备，自动跳过离线）
            BatchShutdownCommand = ReactiveCommand.Create(() =>
            {
                var onlineSelected = _machineCache.Items
                    .OfType<MachineInfoExtended>()
                    .Where(m => m.IsSelected && m.Status == MachineStatus.Online)
                    .ToList();

                if (onlineSelected.Count == 0)
                {
                    MessageBox.Show(
                        "没有在线的选中设备可执行此操作。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                var result = MessageBox.Show(
                    $"确定要关闭 {onlineSelected.Count} 台在线设备吗？",
                    "批量关机",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );
                if (result != MessageBoxResult.Yes)
                    return;

                foreach (var machine in onlineSelected)
                {
                    SendCommandToMachine(machine, Models.Command.SHUTDOWN);
                }
                DNHper.NLogger.Info($"[P2P] 批量关机: {onlineSelected.Count} 台设备");
            });

            // 12.4 批量重启系统（选中设备，自动跳过离线）
            BatchRestartCommand = ReactiveCommand.Create(() =>
            {
                var onlineSelected = _machineCache.Items
                    .OfType<MachineInfoExtended>()
                    .Where(m => m.IsSelected && m.Status == MachineStatus.Online)
                    .ToList();

                if (onlineSelected.Count == 0)
                {
                    MessageBox.Show(
                        "没有在线的选中设备可执行此操作。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                var result = MessageBox.Show(
                    $"确定要重启 {onlineSelected.Count} 台在线设备的系统吗？",
                    "批量重启",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );
                if (result != MessageBoxResult.Yes)
                    return;

                foreach (var machine in onlineSelected)
                {
                    SendCommandToMachine(machine, Models.Command.RESTART);
                }
                DNHper.NLogger.Info($"[P2P] 批量重启系统: {onlineSelected.Count} 台设备");
            });

            // 12.5 批量重启软件（选中设备，自动跳过离线）
            BatchRestartSoftwareCommand = ReactiveCommand.Create(() =>
            {
                var onlineSelected = _machineCache.Items
                    .OfType<MachineInfoExtended>()
                    .Where(m => m.IsSelected && m.Status == MachineStatus.Online)
                    .ToList();

                if (onlineSelected.Count == 0)
                {
                    MessageBox.Show(
                        "没有在线的选中设备可执行此操作。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                foreach (var machine in onlineSelected)
                {
                    SendCommandToMachine(machine, Models.Command.RESTART_NODE_TREE);
                }
                DNHper.NLogger.Info($"[P2P] 批量重启软件: {onlineSelected.Count} 台设备");
            });

            // 13. 取消远程包任务
            CancelPackageTaskCommand = ReactiveCommand.Create<RemotePackageTask>(task =>
            {
                if (task.IsInProgress)
                {
                    task.UpdateState(RemotePackageState.Cancelled, "已取消");
                    // 通过取消令牌终止异步管道（替代旧的 TCS 字典移除）
                    if (_packageTaskCancellations.TryRemove(task.TaskId, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                    DNHper.NLogger.Info($"[P2P] 取消远程包任务: {task.MachineName}");
                }
            });

            // 14. 重试远程包任务
            RetryPackageTaskCommand = ReactiveCommand.Create<RemotePackageTask>(task =>
            {
                if (task.IsFailed || task.State == RemotePackageState.Cancelled)
                {
                    // 重置状态并重新执行
                    task.UpdateState(RemotePackageState.Pending, "等待中", 0);
                    task.ErrorMessage = null;
                    _ = ExecuteRemotePackageTaskAsync(task);
                    DNHper.NLogger.Info($"[P2P] 重试远程包任务: {task.MachineName}");
                }
            });

            // 15. 清除已完成的远程包任务
            ClearCompletedPackageTasksCommand = ReactiveCommand.Create(() =>
            {
                var completedTasks = PackageTasks.Where(t => !t.IsInProgress).ToList();
                foreach (var task in completedTasks)
                {
                    _packageTaskCache.Remove(task);
                }
            });

            ShowHardwareInfoCommand = ReactiveCommand.Create<MachineInfo>(machine =>
            {
                if (machine == null)
                    return;

                var title = machine.Name ?? machine.ID;
                var hardwareInfo = (machine as MachineInfoExtended)?.HardwareInfo ?? "未知";
                MessageBox.Show(
                    hardwareInfo,
                    $"硬件详情 - {title}",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            });

            // 组合所有订阅用于清理
            _cleanUp = new System.Reactive.Disposables.CompositeDisposable(
                machineListSubscription,
                packageTaskSubscription,
                statsRefreshSubscription,
                _deviceCacheSaveSubject
            );
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 处理远程导出进程包进度通知
        /// </summary>
        public void OnExportPackageProgress(string remoteIP, string message)
        {
            // 查找对应IP的包任务，更新进度文本
            var packageTask = _packageTaskCache.Items.FirstOrDefault(
                t => t.MachineIP == remoteIP && t.State == RemotePackageState.Exporting
            );

            if (packageTask != null)
            {
                packageTask.UpdateState(RemotePackageState.Exporting, $"远程: {message}", -1);
                _packageTaskCache.AddOrUpdate(packageTask);

                // 同步更新传输面板中的占位符任务
                var placeholderTaskId = $"pkg-prepare-{packageTask.TaskId}";
                _taskManager.UpdateTaskStatus(placeholderTaskId, $"远程备份: {message}");
            }
        }

        /// <summary>
        /// 处理远程导出进程包完成通知
        /// </summary>
        public void OnExportPackageCompleted(
            string remoteIP,
            bool success,
            string error = null,
            string packageFileName = null,
            string taskId = null
        )
        {
            // 解析 taskId（向后兼容：旧版远端可能不返回 taskId）
            var resolvedTaskId = taskId;
            if (string.IsNullOrEmpty(resolvedTaskId))
            {
                resolvedTaskId = _packageTaskCache.Items
                    .FirstOrDefault(t => t.MachineIP == remoteIP && t.IsInProgress)
                    ?.TaskId;
            }

            if (!string.IsNullOrEmpty(resolvedTaskId))
            {
                DNHper.NLogger.Info(
                    $"[P2P] 收到 {remoteIP} 的导出完成通知，成功: {success}, 文件: {packageFileName}"
                );
                if (!success)
                {
                    DNHper.NLogger.Warn($"[P2P] 远程导出失败: {error}");
                }
                // 推送到 Subject，Rx 管道中的 Where(TaskId).Take(1) 自动匹配
                _exportCompletedSubject.OnNext((resolvedTaskId, success, packageFileName));
            }
        }

        /// <summary>
        /// 获取与远程IP通信的本地IP地址
        /// </summary>
        public string GetLocalIPForRemote(string remoteIP)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect(remoteIP, 65530);
                    var endPoint = socket.LocalEndPoint as System.Net.IPEndPoint;
                    return endPoint?.Address.ToString() ?? "127.0.0.1";
                }
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        /// <summary>
        /// 添加或更新设备（已有设备就地更新属性，保留IsSelected等UI瞬态）
        /// </summary>
        public void AddOrUpdateMachine(MachineInfo machine)
        {
            if (machine == null || string.IsNullOrEmpty(machine.ID))
                return;

            var existing = _machineCache.Lookup(machine.ID);
            if (existing.HasValue && existing.Value is MachineInfoExtended existingExt)
            {
                // 就地更新属性（保留 IsSelected 等 UI 瞬态）
                existingExt.Name = machine.Name;
                existingExt.IPs = machine.IPs;
                existingExt.CPUs = machine.CPUs;
                existingExt.GPUs = machine.GPUs;
                existingExt.Memories = machine.Memories;
                existingExt.LastSeen = DateTime.Now;
                existingExt.Status = MachineStatus.Online;

                if (machine is MachineInfoExtended incoming)
                {
                    existingExt.SupportsP2P = incoming.SupportsP2P;
                    existingExt.FileTransferPort = incoming.FileTransferPort;
                    existingExt.Version = incoming.Version;
                }

                // 通知 DynamicData 管道重新评估此条目
                _machineCache.Refresh(existingExt);
            }
            else
            {
                // 新设备：添加到缓存
                if (machine is MachineInfoExtended extended)
                {
                    extended.LastSeen = DateTime.Now;
                    extended.Status = MachineStatus.Online;
                }
                _machineCache.AddOrUpdate(machine);
            }
            SaveDeviceCache();
        }

        /// <summary>
        /// 获取所有在线设备列表（供资源库等外部模块使用）
        /// </summary>
        public List<MachineInfoExtended> GetOnlineDevices()
        {
            return _machineCache.Items
                .OfType<MachineInfoExtended>()
                .Where(m => m.Status == MachineStatus.Online)
                .ToList();
        }

        /// <summary>
        /// 移除设备
        /// </summary>
        public void RemoveMachine(string machineId)
        {
            _machineCache.Remove(machineId);
            SaveDeviceCache();
        }

        /// <summary>
        /// 清空所有设备
        /// </summary>
        public void ClearMachines()
        {
            _machineCache.Clear();
            SaveDeviceCache();
        }

        /// <summary>
        /// 检查并更新设备在线状态
        /// </summary>
        public void UpdateDeviceStatus()
        {
            var now = DateTime.Now;
            var offlineThreshold = TimeSpan.FromSeconds(15);

            foreach (var machine in _machineCache.Items.ToList())
            {
                if (machine is MachineInfoExtended extended)
                {
                    var shouldBeOffline = now - extended.LastSeen > offlineThreshold;
                    if (shouldBeOffline && extended.Status != MachineStatus.Offline)
                    {
                        // Status setter 触发 PropertyChanged，AutoRefreshOnObservable 驱动管道刷新
                        extended.Status = MachineStatus.Offline;
                    }
                }
            }
        }

        /// <summary>
        /// 加载设备缓存
        /// </summary>
        private void LoadDeviceCache()
        {
            try
            {
                if (!File.Exists(_deviceCacheFilePath))
                    return;

                var json = File.ReadAllText(_deviceCacheFilePath);
                var devices = JsonConvert.DeserializeObject<List<MachineInfoExtended>>(json);
                if (devices == null || devices.Count == 0)
                    return;

                foreach (var device in devices)
                {
                    device.Status = MachineStatus.Offline;
                    if (device.LastSeen == default)
                        device.LastSeen = DateTime.MinValue;
                    _machineCache.AddOrUpdate(device);
                }
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Warn($"[DeviceCache] 加载缓存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 触发保存设备缓存（节流）
        /// </summary>
        private void SaveDeviceCache()
        {
            _deviceCacheSaveSubject.OnNext(Unit.Default);
        }

        /// <summary>
        /// 保存设备缓存
        /// </summary>
        private void SaveDeviceCacheInternal()
        {
            try
            {
                var snapshot = _machineCache.Items
                    .Select(m =>
                    {
                        if (m is MachineInfoExtended ext)
                        {
                            return new MachineInfoExtended
                            {
                                ID = ext.ID,
                                Name = ext.Name,
                                IPs = ext.IPs,
                                CPUs = ext.CPUs,
                                GPUs = ext.GPUs,
                                Memories = ext.Memories,
                                LastSeen = ext.LastSeen,
                                Status = ext.Status,
                                IsManuallyAdded = ext.IsManuallyAdded,
                                SupportsP2P = ext.SupportsP2P,
                                FileTransferPort = ext.FileTransferPort,
                                Version = ext.Version,
                                ActiveTransfers = ext.ActiveTransfers,
                                TotalBytesTransferred = ext.TotalBytesTransferred
                            };
                        }

                        return new MachineInfoExtended
                        {
                            ID = m.ID,
                            Name = m.Name,
                            IPs = m.IPs,
                            CPUs = m.CPUs,
                            GPUs = m.GPUs,
                            Memories = m.Memories,
                            LastSeen = DateTime.MinValue,
                            Status = MachineStatus.Offline
                        };
                    })
                    .ToList();

                var dir = Path.GetDirectoryName(_deviceCacheFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                File.WriteAllText(_deviceCacheFilePath, json);
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Warn($"[DeviceCache] 保存缓存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步执行远程包下载任务
        /// </summary>
        /// <summary>
        /// 异步执行远程包下载任务（使用 Rx 管道替代 TCS + Task.WhenAny 模式）
        /// 流程：请求导出 → 等待导出完成(Subject) → 请求推送下载 → 等待传输完成(Observable) → 完成
        /// </summary>
        private async System.Threading.Tasks.Task ExecuteRemotePackageTaskAsync(
            RemotePackageTask task
        )
        {
            var ip = task.MachineIP;
            var machineName = task.MachineName;
            var localIP = GetLocalIPForRemote(ip);

            // 为每个任务创建独立的取消令牌（供 CancelPackageTaskCommand 使用）
            using var cts = new System.Threading.CancellationTokenSource();
            _packageTaskCancellations[task.TaskId] = cts;
            var ct = cts.Token;

            // 创建传输面板中的占位符任务
            var placeholderTaskId = $"pkg-prepare-{task.TaskId}";
            _taskManager.CreatePlaceholderTask(
                placeholderTaskId,
                $"{machineName} 进程包",
                TransferDirection.Download,
                TransferTaskSource.PackageDownload,
                machineName,
                ip,
                "正在请求远程备份..."
            );

            try
            {
                // ── Phase 1: 请求导出 ──
                task.UpdateState(RemotePackageState.RequestingExport, "正在请求远程导出...", 10);
                _packageTaskCache.AddOrUpdate(task);
                _taskManager.UpdateTaskStatus(placeholderTaskId, "正在请求远程备份...");

                var exportCommand = new Models.Command
                {
                    EventID = Models.Command.EXPORT_PACKAGE,
                    Data = new Newtonsoft.Json.Linq.JObject
                    {
                        ["requesterIP"] = localIP,
                        ["taskId"] = task.TaskId
                    }
                };

                var sent = await SendUdpCommandWithRetryAsync(
                    ip,
                    CommonVars.ControlPort,
                    exportCommand
                );
                if (!sent)
                {
                    task.SetFailed("发送导出命令失败");
                    _packageTaskCache.AddOrUpdate(task);
                    _taskManager.UpdateTaskStatus(
                        placeholderTaskId,
                        "发送导出命令失败",
                        TransferState.Failed
                    );
                    return;
                }
                DNHper.NLogger.Info($"[P2P] 任务 {task.TaskId} 已发送导出命令到 {machineName}");

                // ── Phase 2: 等待导出完成（Rx: Subject → Where → Take(1) → Timeout → ToTask） ──
                task.UpdateState(RemotePackageState.Exporting, "远程正在导出...", 20);
                _packageTaskCache.AddOrUpdate(task);
                _taskManager.UpdateTaskStatus(placeholderTaskId, "远程正在备份打包...");

                var exportResult = await _exportCompletedSubject
                    .Where(e => e.TaskId == task.TaskId)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(120))
                    .ToTask(ct);

                if (!exportResult.Success)
                {
                    task.SetFailed("远程导出失败");
                    _packageTaskCache.AddOrUpdate(task);
                    _taskManager.UpdateTaskStatus(
                        placeholderTaskId,
                        "远程备份失败",
                        TransferState.Failed
                    );
                    return;
                }

                // ── Phase 3: 获取文件名 ──
                var latestPackage = exportResult.PackageFileName;
                if (string.IsNullOrEmpty(latestPackage))
                {
                    // 降级方案：远程未返回文件名，尝试TCP获取文件列表
                    DNHper.NLogger.Warn($"[P2P] 任务 {task.TaskId} 远程未返回文件名，尝试TCP获取文件列表");
                    task.UpdateState(RemotePackageState.ExportCompleted, "导出完成，获取文件列表...", 50);
                    _packageTaskCache.AddOrUpdate(task);
                    _taskManager.UpdateTaskStatus(placeholderTaskId, "备份完成，获取文件列表...");

                    await Observable.Timer(TimeSpan.FromMilliseconds(500));
                    var files = await _transferService.GetRemoteSharedFilesAsync(ip);
                    var packageFiles = files
                        .Where(f => f.EndsWith(".dkp.zip", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f)
                        .ToList();

                    if (packageFiles.Count == 0)
                    {
                        var errorMsg =
                            files.Length == 0
                                ? "获取文件列表失败（可能是网络超时或防火墙阻断TCP连接）"
                                : $"共享目录中有 {files.Length} 个文件但没有 .dkp.zip 包文件";
                        task.SetFailed(errorMsg);
                        _packageTaskCache.AddOrUpdate(task);
                        _taskManager.UpdateTaskStatus(
                            placeholderTaskId,
                            errorMsg,
                            TransferState.Failed
                        );
                        return;
                    }
                    latestPackage = packageFiles.First();
                }

                DNHper.NLogger.Info($"[P2P] 任务 {task.TaskId} 目标文件: {latestPackage}");

                // ── Phase 4: 下载文件 ──
                task.PackageFileName = latestPackage;
                task.UpdateState(RemotePackageState.Downloading, $"正在下载 {latestPackage}...", 60);
                _packageTaskCache.AddOrUpdate(task);
                _taskManager.RemoveTask(placeholderTaskId); // 移除占位符，实际传输会自动创建真实任务

                DNHper.NLogger.Info($"[P2P] 任务 {task.TaskId} 开始下载: {latestPackage}");

                // 发送推送命令
                var pushCommand = new Models.Command
                {
                    EventID = Models.Command.PUSH_PACKAGE_TO_REQUESTER,
                    Data = new Newtonsoft.Json.Linq.JObject
                    {
                        ["requesterIP"] = localIP,
                        ["requesterPort"] = _transferService.DefaultPort,
                        ["fileName"] = latestPackage
                    }
                };
                var pushSent = await SendUdpCommandWithRetryAsync(
                    ip,
                    CommonVars.ControlPort,
                    pushCommand
                );
                if (!pushSent)
                {
                    task.SetFailed("发送推送命令失败");
                    _packageTaskCache.AddOrUpdate(task);
                    return;
                }
                DNHper.NLogger.Info($"[P2P] 任务 {task.TaskId} 已通过UDP请求远程推送文件: {latestPackage}");

                // 进度订阅（节流500ms，在 finally 中释放）
                var progressSub = _transferService.TransferProgress
                    .Where(
                        t =>
                            t.FileName == latestPackage && t.Direction == TransferDirection.Download
                    )
                    .Sample(TimeSpan.FromMilliseconds(500))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(t =>
                    {
                        if (t.TotalBytes > 0)
                        {
                            var downloadProgress = (double)t.TransferredBytes / t.TotalBytes;
                            task.StatusText =
                                $"正在下载 {latestPackage}... ({Services.TransferTaskManager.FormatBytes(t.TransferredBytes)}/{Services.TransferTaskManager.FormatBytes(t.TotalBytes)})";
                            task.Progress = 60 + downloadProgress * 38;
                        }
                    });

                try
                {
                    // 等待传输完成/失败（Rx: Merge → Take(1) → Timeout → ToTask，替代 TCS + WhenAny）
                    var downloadSuccess = await _transferService.TransferCompleted
                        .Where(
                            t =>
                                t.FileName == latestPackage
                                && t.Direction == TransferDirection.Download
                        )
                        .Select(_ => true)
                        .Merge(
                            _transferService.TransferFailed
                                .Where(
                                    t =>
                                        t.FileName == latestPackage
                                        && t.Direction == TransferDirection.Download
                                )
                                .Select(_ => false)
                        )
                        .Take(1)
                        .Timeout(TimeSpan.FromMinutes(5))
                        .ToTask(ct);

                    if (!downloadSuccess)
                    {
                        task.SetFailed("文件下载失败");
                        _packageTaskCache.AddOrUpdate(task);
                        return;
                    }

                    // ── Phase 5: 完成 ──
                    var localFilePath = System.IO.Path.Combine(
                        Utilities.AppPathes.ReceivedFilesDir,
                        latestPackage
                    );
                    task.SetCompleted(localFilePath);
                    _packageTaskCache.AddOrUpdate(task);
                    DNHper.NLogger.Info($"[P2P] 任务 {task.TaskId} 完成: {localFilePath}");
                }
                finally
                {
                    progressSub?.Dispose();
                }
            }
            catch (TimeoutException)
            {
                task.SetFailed("操作超时");
                _packageTaskCache.AddOrUpdate(task);
                DNHper.NLogger.Warn($"[P2P] 任务 {task.TaskId} 超时");
            }
            catch (System.OperationCanceledException)
            {
                // 已通过 CancelPackageTaskCommand 标记取消，无需重复处理
                DNHper.NLogger.Info($"[P2P] 任务 {task.TaskId} 已取消");
            }
            catch (Exception ex)
            {
                task.SetFailed(ex.Message);
                _packageTaskCache.AddOrUpdate(task);
                DNHper.NLogger.Error($"[P2P] 任务 {task.TaskId} 失败: {ex.Message}");
            }
            finally
            {
                _packageTaskCancellations.TryRemove(task.TaskId, out _);
                // 确保占位符任务（如果还在）也标记为对应状态
                var placeholder = _taskManager.FindTask(placeholderTaskId);
                if (placeholder != null && placeholder.IsActive)
                {
                    var failState = task.IsFailed
                        ? TransferState.Failed
                        : task.State == RemotePackageState.Cancelled
                            ? TransferState.Cancelled
                            : TransferState.Failed;
                    _taskManager.UpdateTaskStatus(placeholderTaskId, task.StatusText, failState);
                }
            }
        }

        /// <summary>
        /// 添加远程包下载任务（供外部调用）
        /// </summary>
        public void AddRemotePackageTask(string machineName, string machineIP)
        {
            var task = new RemotePackageTask(machineName, machineIP);
            _packageTaskCache.AddOrUpdate(task);
            _ = ExecuteRemotePackageTaskAsync(task);
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 构建搜索谓词
        /// </summary>
        private Func<MachineInfo, bool> BuildSearchPredicate(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return _ => true;

            return machine => MatchesSearch(machine, searchText);
        }

        /// <summary>
        /// 检查设备是否匹配搜索条件
        /// </summary>
        private bool MatchesSearch(MachineInfo machine, string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            var tokens = searchText
                .Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            if (tokens.Count == 0)
                return true;

            foreach (var token in tokens)
            {
                var search = token.ToLowerInvariant();

                if (MatchesIpRange(machine, token) || MatchesCidr(machine, token))
                    return true;

                if (
                    (machine.Name?.ToLowerInvariant().Contains(search) == true)
                    || (machine.ID?.ToLowerInvariant().Contains(search) == true)
                    || (machine.IPs?.Any(ip => ip.Contains(search)) == true)
                    || (machine.CPUs?.Any(cpu => cpu.ToLowerInvariant().Contains(search)) == true)
                    || (machine.GPUs?.Any(gpu => gpu.ToLowerInvariant().Contains(search)) == true)
                )
                {
                    return true;
                }
            }

            return false;
        }

        private bool MatchesIpRange(MachineInfo machine, string token)
        {
            if (machine?.IPs == null || machine.IPs.Count == 0)
                return false;

            if (!token.Contains('-'))
                return false;

            var parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                return false;

            var start = parts[0].Trim();
            var end = parts[1].Trim();

            if (string.IsNullOrEmpty(start) || string.IsNullOrEmpty(end))
                return false;

            if (!TryParseIPv4(start, out var startValue))
                return false;

            // 支持 192.168.1.10-20 这种写法
            if (!end.Contains('.') && int.TryParse(end, out var lastOctet))
            {
                var startParts = start.Split('.');
                if (startParts.Length == 4)
                {
                    end = $"{startParts[0]}.{startParts[1]}.{startParts[2]}.{lastOctet}";
                }
            }

            if (!TryParseIPv4(end, out var endValue))
                return false;

            if (startValue > endValue)
            {
                (startValue, endValue) = (endValue, startValue);
            }

            return machine.IPs.Any(ip =>
            {
                if (!TryParseIPv4(ip, out var ipValue))
                    return false;
                return ipValue >= startValue && ipValue <= endValue;
            });
        }

        private bool MatchesCidr(MachineInfo machine, string token)
        {
            if (machine?.IPs == null || machine.IPs.Count == 0)
                return false;

            if (!token.Contains('/'))
                return false;

            var parts = token.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                return false;

            if (!TryParseIPv4(parts[0].Trim(), out var baseIp))
                return false;

            if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32)
                return false;

            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var network = baseIp & mask;

            return machine.IPs.Any(ip =>
            {
                if (!TryParseIPv4(ip, out var ipValue))
                    return false;
                return (ipValue & mask) == network;
            });
        }

        private bool TryParseIPv4(string ipText, out uint value)
        {
            value = 0;
            if (!IPAddress.TryParse(ipText, out var ip))
                return false;
            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
                return false;
            value =
                ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            return true;
        }

        /// <summary>
        /// 构建状态谓词
        /// </summary>
        private Func<MachineInfo, bool> BuildStatusPredicate(MachineStatus? status)
        {
            if (status == null)
                return _ => true;

            return machine => MatchesStatus(machine, status);
        }

        /// <summary>
        /// 检查设备是否匹配状态条件
        /// </summary>
        private bool MatchesStatus(MachineInfo machine, MachineStatus? status)
        {
            if (status == null)
                return true;

            if (machine is MachineInfoExtended extended)
            {
                return extended.Status == status;
            }

            // 默认视为在线
            return status == MachineStatus.Online;
        }

        /// <summary>
        /// 发送命令到远程设备
        /// </summary>
        private void SendCommandToMachine(MachineInfo machine, int commandId)
        {
            try
            {
                var targetIP = machine.IPs?.FirstOrDefault() ?? machine.ID;
                using var udpClient = new UdpClient();

                var command = new Models.Command { EventID = commandId };
                var json = JsonConvert.SerializeObject(command);
                var data = Encoding.UTF8.GetBytes(json);

                udpClient.Send(data, data.Length, targetIP, CommonVars.ControlPort);
                DNHper.NLogger.Info($"[P2P] 已发送命令 {commandId} 到 {targetIP}");
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Error($"[P2P] 发送命令失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 带重试的UDP命令发送（解决UDP丢包问题）
        /// </summary>
        public async System.Threading.Tasks.Task<bool> SendUdpCommandWithRetryAsync(
            string targetIP,
            int targetPort,
            Models.Command command,
            int maxRetries = 3,
            int baseDelayMs = 300
        )
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var json = JsonConvert.SerializeObject(command);
                    var data = Encoding.UTF8.GetBytes(json);
                    using var udpClient = new UdpClient();
                    udpClient.Send(data, data.Length, targetIP, targetPort);
                    return true;
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Warn($"[UDP] 发送命令失败 (尝试 {i + 1}/{maxRetries}): {ex.Message}");
                    if (i < maxRetries - 1)
                        await System.Threading.Tasks.Task.Delay(baseDelayMs * (i + 1));
                }
            }
            return false;
        }

        /// <summary>
        /// 向指定设备发送文件推送下载请求（通过UDP PUSH_DOWNLOAD_FILES）
        /// 共享方法：RemoteFileBrowser 和 ResourceLibrary 均调用此方法
        /// </summary>
        public async System.Threading.Tasks.Task RequestPushDownloadAsync(
            string remoteIP,
            string[] fileNames
        )
        {
            var localIP = GetLocalIPForRemote(remoteIP);
            var pushCmd = new Models.Command
            {
                EventID = Models.Command.PUSH_DOWNLOAD_FILES,
                Data = new Newtonsoft.Json.Linq.JObject
                {
                    ["requesterIP"] = localIP,
                    ["fileNames"] = Newtonsoft.Json.Linq.JArray.FromObject(fileNames)
                }
            };
            await SendUdpCommandWithRetryAsync(remoteIP, CommonVars.ControlPort, pushCmd);
            DNHper.NLogger.Info($"[P2P] 已请求 {remoteIP} 推送 {fileNames.Length} 个文件");
        }

        /// <summary>
        /// 通过UDP请求远程设备的共享文件列表（Rx: Subject → Where → Take(1) → Timeout → ToTask）
        /// </summary>
        public async System.Threading.Tasks.Task<SharedFileInfo[]> RequestRemoteFileListAsync(
            string remoteIP,
            int timeoutMs = 15000
        )
        {
            var requestId = Guid.NewGuid().ToString();
            try
            {
                var localIP = GetLocalIPForRemote(remoteIP);
                var command = new Models.Command
                {
                    EventID = Models.Command.LIST_SHARED_FILES,
                    Data = new Newtonsoft.Json.Linq.JObject
                    {
                        ["requesterIP"] = localIP,
                        ["requestId"] = requestId
                    }
                };

                var sent = await SendUdpCommandWithRetryAsync(
                    remoteIP,
                    CommonVars.ControlPort,
                    command
                );
                if (!sent)
                {
                    DNHper.NLogger.Warn($"[P2P] 发送文件列表请求失败: {remoteIP}");
                    return Array.Empty<SharedFileInfo>();
                }

                // 通过 Rx Subject 等待响应（替代 TCS + Task.WhenAny 模式）
                return await _fileListResponseSubject
                    .Where(e => e.RequestId == requestId)
                    .Select(e => e.Files)
                    .Take(1)
                    .Timeout(TimeSpan.FromMilliseconds(timeoutMs))
                    .ToTask();
            }
            catch (TimeoutException)
            {
                DNHper.NLogger.Warn($"[P2P] 请求远程文件列表超时: {remoteIP}");
                return Array.Empty<SharedFileInfo>();
            }
            catch (Exception ex)
            {
                DNHper.NLogger.Error($"[P2P] 请求远程文件列表失败: {ex.Message}");
                return Array.Empty<SharedFileInfo>();
            }
        }

        /// <summary>
        /// 处理UDP文件列表响应回调（推送到 Subject，Rx 管道自动匹配 requestId）
        /// </summary>
        public void OnListSharedFilesResponse(string requestId, SharedFileInfo[] files)
        {
            _fileListResponseSubject.OnNext((requestId, files ?? Array.Empty<SharedFileInfo>()));
        }

        /// <summary>
        /// 带并发控制的远程包下载任务执行（用于批量下载）
        /// </summary>
        private async System.Threading.Tasks.Task ThrottledExecuteRemotePackageTaskAsync(
            RemotePackageTask task
        )
        {
            await _batchDownloadSemaphore.WaitAsync();
            try
            {
                await ExecuteRemotePackageTaskAsync(task);
            }
            finally
            {
                _batchDownloadSemaphore.Release();
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _cleanUp?.Dispose();
            _machineCache?.Dispose();
            _taskManager?.Dispose();
            _packageTaskCache?.Dispose();
            _transferService?.Dispose();
            _batchDownloadSemaphore?.Dispose();
            _exportCompletedSubject?.Dispose();
            _fileListResponseSubject?.Dispose();
            // 取消所有未完成的包任务
            foreach (var kvp in _packageTaskCancellations)
            {
                kvp.Value?.Cancel();
                kvp.Value?.Dispose();
            }
            _packageTaskCancellations.Clear();
        }

        #endregion
    }

    #region 辅助类

    /// <summary>
    /// 状态过滤选项
    /// </summary>
    public class StatusFilterOption
    {
        public string DisplayName { get; set; }
        public MachineStatus? Value { get; set; }
    }

    #endregion
}
