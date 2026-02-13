using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 传输任务列表ViewModel - 管理正在下载/已下载的分类展示
    /// 支持实时进度、速度、ETA显示
    /// </summary>
    public class TransferListViewModel : ReactiveObject, IDisposable
    {
        #region 服务引用

        private readonly TransferTaskManager _taskManager;
        private readonly P2PFileTransferService _transferService;
        private readonly CompositeDisposable _disposables = new();

        #endregion

        #region 列表属性

        /// <summary>正在上传的任务列表</summary>
        public ReadOnlyObservableCollection<TransferTaskItem> ActiveUploadTasks =>
            _taskManager.ActiveUploadTasks;

        /// <summary>正在下载的任务列表</summary>
        public ReadOnlyObservableCollection<TransferTaskItem> ActiveDownloadTasks =>
            _taskManager.ActiveDownloadTasks;

        /// <summary>正在传输的任务列表（全部活跃）</summary>
        public ReadOnlyObservableCollection<TransferTaskItem> ActiveTasks =>
            _taskManager.ActiveTasks;

        /// <summary>已完成的任务列表</summary>
        public ReadOnlyObservableCollection<TransferTaskItem> CompletedTasks =>
            _taskManager.CompletedTasks;

        #endregion

        #region 统计属性

        /// <summary>上传任务数</summary>
        public int UploadCount => _taskManager.UploadCount;

        /// <summary>下载任务数</summary>
        public int DownloadCount => _taskManager.DownloadCount;

        /// <summary>活跃任务数</summary>
        public int ActiveCount => _taskManager.ActiveCount;

        /// <summary>已完成任务数</summary>
        public int CompletedCount => _taskManager.CompletedCount;

        /// <summary>总进度</summary>
        public double TotalProgress => _taskManager.TotalProgress;

        /// <summary>总速度显示</summary>
        public string TotalSpeedDisplay => _taskManager.TotalSpeedDisplay;

        private int _selectedTabIndex = 0;

        /// <summary>当前选中的Tab页索引 (0=上传中, 1=下载中, 2=已完成)</summary>
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
        }

        /// <summary>上传列表是否为空</summary>
        public bool IsUploadEmpty => ActiveUploadTasks.Count == 0;

        /// <summary>下载列表是否为空</summary>
        public bool IsDownloadEmpty => ActiveDownloadTasks.Count == 0;

        /// <summary>已完成任务列表是否为空</summary>
        public bool IsCompletedEmpty => CompletedTasks.Count == 0;

        /// <summary>上传Tab标题</summary>
        public string UploadTabHeader => $"上传 ({UploadCount})";

        /// <summary>下载Tab标题</summary>
        public string DownloadTabHeader => $"下载 ({DownloadCount})";

        /// <summary>已完成Tab标题</summary>
        public string CompletedTabHeader => $"已完成 ({CompletedCount})";

        /// <summary>是否有活跃任务（用于控制按钮可用性和进度栏显示）</summary>
        public bool HasActiveTasks => ActiveCount > 0;

        #endregion

        #region Commands

        /// <summary>暂停任务</summary>
        public ReactiveCommand<TransferTaskItem, Unit> PauseCommand { get; }

        /// <summary>恢复任务</summary>
        public ReactiveCommand<TransferTaskItem, Unit> ResumeCommand { get; }

        /// <summary>取消任务</summary>
        public ReactiveCommand<TransferTaskItem, Unit> CancelCommand { get; }

        /// <summary>清除所有已完成任务</summary>
        public ReactiveCommand<Unit, Unit> ClearCompletedCommand { get; }

        /// <summary>清除单个已完成任务</summary>
        public ReactiveCommand<TransferTaskItem, Unit> RemoveTaskCommand { get; }

        /// <summary>暂停所有任务</summary>
        public ReactiveCommand<Unit, Unit> PauseAllCommand { get; }

        /// <summary>恢复所有任务</summary>
        public ReactiveCommand<Unit, Unit> ResumeAllCommand { get; }

        /// <summary>取消所有任务</summary>
        public ReactiveCommand<Unit, Unit> CancelAllCommand { get; }

        /// <summary>打开文件所在目录</summary>
        public ReactiveCommand<TransferTaskItem, Unit> OpenFileLocationCommand { get; }

        /// <summary>部署进程包（.dkp.zip）</summary>
        public ReactiveCommand<TransferTaskItem, Unit> DeployPackageCommand { get; }

        /// <summary>应用节点更新包（.dkp-patch.zip）</summary>
        public ReactiveCommand<TransferTaskItem, Unit> ApplyPatchCommand { get; }

        #endregion

        #region 构造函数

        public TransferListViewModel(
            TransferTaskManager taskManager,
            P2PFileTransferService transferService
        )
        {
            _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));
            _transferService =
                transferService ?? throw new ArgumentNullException(nameof(transferService));

            // 单任务操作命令
            PauseCommand = ReactiveCommand.Create<TransferTaskItem>(task =>
            {
                _transferService.PauseTask(task.TaskId);
                _taskManager.PauseTask(task.TaskId);
            });

            ResumeCommand = ReactiveCommand.CreateFromTask<TransferTaskItem>(async task =>
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

            CancelCommand = ReactiveCommand.Create<TransferTaskItem>(task =>
            {
                _transferService.CancelTask(task.TaskId);
                _taskManager.CancelTask(task.TaskId);
            });

            RemoveTaskCommand = ReactiveCommand.Create<TransferTaskItem>(task =>
            {
                _taskManager.RemoveTask(task.TaskId);
            });

            // 批量操作命令
            ClearCompletedCommand = ReactiveCommand.Create(() =>
            {
                _taskManager.ClearCompleted();
            });

            PauseAllCommand = ReactiveCommand.Create(() =>
            {
                foreach (var task in ActiveTasks.Where(t => t.CanPause).ToList())
                {
                    _transferService.PauseTask(task.TaskId);
                    _taskManager.PauseTask(task.TaskId);
                }
            });

            ResumeAllCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                foreach (var task in ActiveTasks.Where(t => t.CanResume).ToList())
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
                }
            });

            CancelAllCommand = ReactiveCommand.Create(() =>
            {
                foreach (var task in ActiveTasks.Where(t => t.CanCancel).ToList())
                {
                    _transferService.CancelTask(task.TaskId);
                    _taskManager.CancelTask(task.TaskId);
                }
            });

            OpenFileLocationCommand = ReactiveCommand.Create<TransferTaskItem>(task =>
            {
                try
                {
                    if (
                        !string.IsNullOrEmpty(task.LocalPath)
                        && System.IO.File.Exists(task.LocalPath)
                    )
                    {
                        System.Diagnostics.Process.Start(
                            "explorer.exe",
                            $"/select,\"{task.LocalPath}\""
                        );
                    }
                    else
                    {
                        // 打开接收文件目录
                        var dir = Utilities.AppPathes.ReceivedFilesDir;
                        if (System.IO.Directory.Exists(dir))
                        {
                            System.Diagnostics.Process.Start("explorer.exe", dir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DNHper.NLogger.Error($"[传输] 打开文件位置失败: {ex.Message}");
                }
            });

            // 部署进程包（.dkp.zip → 打开导入对话框）
            DeployPackageCommand = ReactiveCommand.Create<TransferTaskItem>(task =>
            {
                if (!task.IsPackage || !task.CanDeploy)
                    return;
                try
                {
                    var filePath = task.LocalPath;
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    {
                        filePath = Path.Combine(AppPathes.ReceivedFilesDir, task.FileName);
                    }
                    if (!File.Exists(filePath))
                    {
                        NLogger.Warn($"[传输] 部署文件不存在: {filePath}");
                        return;
                    }

                    NLogger.Info($"[传输] 部署进程包: {filePath}");
                    var mainWindow = System.Windows.Application.Current.MainWindow;
                    var importDialog = new Views.ImportDialog(filePath) { Owner = mainWindow };
                    importDialog.ShowDialog();

                    if (importDialog.DialogResult == true)
                    {
                        NLogger.Info($"[传输] 部署完成: {task.FileName}");
                        ReactiveUI.MessageBus.Current.SendMessage("ReloadConfig");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Error($"[传输] 部署失败: {ex.Message}");
                }
            });

            // 应用节点更新包（.dkp-patch.zip → 打开NodePackageDialog）
            ApplyPatchCommand = ReactiveCommand.Create<TransferTaskItem>(task =>
            {
                if (!task.IsPatch || !task.CanDeploy)
                    return;
                try
                {
                    var filePath = task.LocalPath;
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    {
                        filePath = Path.Combine(AppPathes.ReceivedFilesDir, task.FileName);
                    }
                    if (!File.Exists(filePath))
                    {
                        NLogger.Warn($"[传输] 更新包文件不存在: {filePath}");
                        return;
                    }

                    NLogger.Info($"[传输] 应用节点更新包: {filePath}");
                    var mainWindow =
                        System.Windows.Application.Current.MainWindow as DaemonKit.MainWindow;
                    var allNodes = new System.Collections.Generic.List<ProcessItem>();
                    ProcessItem rootNode = null;
                    if (mainWindow?.ViewModel?.RootProcessNode != null)
                    {
                        allNodes = mainWindow.ViewModel.RootProcessNode.AllChildren();
                        rootNode = mainWindow.ViewModel.RootProcessNode;
                    }

                    var dialog = new Views.NodePackageDialog(filePath, allNodes, rootNode)
                    {
                        Owner = System.Windows.Application.Current.MainWindow
                    };
                    dialog.ShowDialog();

                    if (dialog.DialogResult == true)
                    {
                        NLogger.Info($"[传输] 更新完成: {task.FileName}");
                    }
                }
                catch (Exception ex)
                {
                    NLogger.Error($"[传输] 应用更新失败: {ex.Message}");
                }
            });

            // 监听TaskManager属性变化并转发到本ViewModel
            int _lastUpload = 0,
                _lastDownload = 0,
                _lastCompleted = 0;
            var refreshSub = Observable
                .Interval(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    var curUpload = _taskManager.UploadCount;
                    var curDownload = _taskManager.DownloadCount;
                    var curCompleted = _taskManager.CompletedCount;
                    var hasActive = _taskManager.ActiveCount > 0;

                    // 活跃任务存在时刷新进度和速度
                    if (hasActive)
                    {
                        this.RaisePropertyChanged(nameof(TotalProgress));
                        this.RaisePropertyChanged(nameof(TotalSpeedDisplay));
                        this.RaisePropertyChanged(nameof(HasActiveTasks));
                    }

                    // 计数变化时刷新Tab标题和空状态（确保完成后同步更新）
                    if (
                        curUpload != _lastUpload
                        || curDownload != _lastDownload
                        || curCompleted != _lastCompleted
                    )
                    {
                        _lastUpload = curUpload;
                        _lastDownload = curDownload;
                        _lastCompleted = curCompleted;

                        this.RaisePropertyChanged(nameof(UploadCount));
                        this.RaisePropertyChanged(nameof(DownloadCount));
                        this.RaisePropertyChanged(nameof(ActiveCount));
                        this.RaisePropertyChanged(nameof(CompletedCount));
                        this.RaisePropertyChanged(nameof(IsUploadEmpty));
                        this.RaisePropertyChanged(nameof(IsDownloadEmpty));
                        this.RaisePropertyChanged(nameof(IsCompletedEmpty));
                        this.RaisePropertyChanged(nameof(UploadTabHeader));
                        this.RaisePropertyChanged(nameof(DownloadTabHeader));
                        this.RaisePropertyChanged(nameof(CompletedTabHeader));
                        this.RaisePropertyChanged(nameof(HasActiveTasks));
                    }
                });

            _disposables.Add(refreshSub);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _disposables.Dispose();
        }

        #endregion
    }
}
