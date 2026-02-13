using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DaemonKit.Views;
using Microsoft.Win32;
using NLog;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 导入对话框 ViewModel
    /// </summary>
    public class ImportDialogViewModel : ReactiveObject
    {
        private string _packagePath;
        private PackageManifest _manifest;
        private bool _importConfigs; // 是否导入配置
        private bool _importProcesses; // 是否导入进程
        private bool _hasConfigs; // 包内是否有配置文件
        private bool _hasProcesses; // 包内是否有进程树数据
        private bool _clearExistingTree;
        private bool _overwriteConflicts; // 新增：冲突时是否覆盖
        private bool _useImportedProjectName; // 新增：是否使用导入的项目名称
        private string _importedProjectName; // 新增：导入包中的项目名称
        private bool _isImporting;
        private double _progressPercentage;
        private string _statusMessage;
        private string _currentFile;
        private ObservableCollection<ProcessItem> _availableProcessTree;

        public string PackagePath
        {
            get => _packagePath;
            set => this.RaiseAndSetIfChanged(ref _packagePath, value);
        }

        /// <summary>
        /// 统一包清单（新格式，同时兼容旧 metadata.json 转换）
        /// </summary>
        public PackageManifest Manifest
        {
            get => _manifest;
            set => this.RaiseAndSetIfChanged(ref _manifest, value);
        }

        public bool ImportConfigs
        {
            get => _importConfigs;
            set => this.RaiseAndSetIfChanged(ref _importConfigs, value);
        }

        public bool ImportProcesses
        {
            get => _importProcesses;
            set => this.RaiseAndSetIfChanged(ref _importProcesses, value);
        }

        public bool HasConfigs
        {
            get => _hasConfigs;
            set => this.RaiseAndSetIfChanged(ref _hasConfigs, value);
        }

        public bool HasProcesses
        {
            get => _hasProcesses;
            set => this.RaiseAndSetIfChanged(ref _hasProcesses, value);
        }

        public bool ClearExistingTree
        {
            get => _clearExistingTree;
            set => this.RaiseAndSetIfChanged(ref _clearExistingTree, value);
        }

        public bool OverwriteConflicts
        {
            get => _overwriteConflicts;
            set => this.RaiseAndSetIfChanged(ref _overwriteConflicts, value);
        }

        public string ImportedProjectName
        {
            get => _importedProjectName;
            set => this.RaiseAndSetIfChanged(ref _importedProjectName, value);
        }

        public bool UseImportedProjectName
        {
            get => _useImportedProjectName;
            set => this.RaiseAndSetIfChanged(ref _useImportedProjectName, value);
        }

        public ObservableCollection<ProcessItem> AvailableProcessTree
        {
            get => _availableProcessTree;
            set => this.RaiseAndSetIfChanged(ref _availableProcessTree, value);
        }

        public bool IsImporting
        {
            get => _isImporting;
            set
            {
                this.RaiseAndSetIfChanged(ref _isImporting, value);
                SendProgressUpdate();
            }
        }

        public double ProgressPercentage
        {
            get => _progressPercentage;
            set
            {
                this.RaiseAndSetIfChanged(ref _progressPercentage, value);
                SendProgressUpdate();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                this.RaiseAndSetIfChanged(ref _statusMessage, value);
                SendProgressUpdate();
            }
        }

        public string CurrentFile
        {
            get => _currentFile;
            set
            {
                this.RaiseAndSetIfChanged(ref _currentFile, value);
                SendProgressUpdate();
            }
        }

        public ReactiveCommand<Unit, Unit> BrowseCommand { get; }
        public ReactiveCommand<Unit, Unit> ImportCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearSelectionCommand { get; }
        public ReactiveCommand<Unit, Unit> MinimizeCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelImportCommand { get; }

        private CancellationTokenSource _cancellationTokenSource;
        private Action<bool> _closeAction;
        private Window _dialogWindow;

        public ImportDialogViewModel(Action<bool> closeAction)
        {
            _closeAction = closeAction;
            _availableProcessTree = new ObservableCollection<ProcessItem>();

            // 默认设置
            ImportConfigs = true; // 默认导入配置
            ImportProcesses = false; // 默认不导入进程
            OverwriteConflicts = true; // 默认覆盖冲突节点

            BrowseCommand = ReactiveCommand.CreateFromTask(ExecuteBrowseAsync);

            var canImport = this.WhenAnyValue(
                x => x.IsImporting,
                x => x.PackagePath,
                (isImporting, path) => !isImporting && !string.IsNullOrEmpty(path)
            );

            ImportCommand = ReactiveCommand.CreateFromTask(ExecuteImportAsync, canImport);
            CancelCommand = ReactiveCommand.Create(() =>
            {
                _cancellationTokenSource?.Cancel();
                _dialogWindow?.Close();
            });

            // 选择操作
            SelectAllCommand = ReactiveCommand.Create(
                () => SetSelection(AvailableProcessTree, true)
            );
            ClearSelectionCommand = ReactiveCommand.Create(
                () => SetSelection(AvailableProcessTree, false)
            );

            // 最小化命令
            MinimizeCommand = ReactiveCommand.Create(() =>
            {
                if (_dialogWindow != null)
                {
                    // 只隐藏窗口，不修改WindowState，避免DialogResult错误
                    _dialogWindow.Hide();

                    // 发送进度消息到主窗口
                    ReactiveUI.MessageBus.Current.SendMessage(
                        new PackageProgressInfo
                        {
                            OperationType = PackageOperationType.Import,
                            IsActive = IsImporting,
                            ProgressPercentage = ProgressPercentage,
                            StatusMessage = StatusMessage,
                            CurrentFile = CurrentFile,
                            DialogInstance = _dialogWindow
                        }
                    );
                }
            });

            // 取消安装命令
            var canCancel = this.WhenAnyValue(x => x.IsImporting);
            CancelImportCommand = ReactiveCommand.Create(
                () =>
                {
                    _cancellationTokenSource?.Cancel();
                },
                canCancel
            );
        }

        private async Task ExecuteBrowseAsync()
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "DaemonKit 包 (*.dkp.zip;*.dkp-patch.zip)|*.dkp.zip;*.dkp-patch.zip",
                DefaultExt = ".dkp.zip"
            };

            if (openDialog.ShowDialog() == true)
            {
                PackagePath = openDialog.FileName;

                // 读取统一清单（优先 manifest.json，回退 metadata.json）
                try
                {
                    StatusMessage = "读取包信息...";
                    Manifest = await ExportImportService.ReadPackageManifestAsync(PackagePath);

                    // 如果是 NodeFull / NodePatch 类型，重定向到 NodePackageDialog
                    if (Manifest != null && Manifest.PackageType != PackageType.TreeBundle)
                    {
                        StatusMessage = "检测到节点更新包，正在打开更新对话框...";
                        // 需要从主窗口获取所有进程节点
                        _dialogWindow?.Close();
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                var mainWindow = Application.Current.MainWindow;
                                var allNodes = GetAllProcessNodesFromMainWindow(mainWindow);
                                var rootNode = (
                                    mainWindow?.DataContext as MainViewModel
                                )?.RootProcessNode;
                                var dialog = new Views.NodePackageDialog(
                                    PackagePath,
                                    allNodes,
                                    rootNode
                                )
                                {
                                    Owner = mainWindow
                                };
                                dialog.ShowDialog();
                            }
                            catch (Exception ex)
                            {
                                NLog.LogManager.GetCurrentClassLogger().Error(ex, "打开节点包对话框失败");
                            }
                        });
                        return;
                    }

                    // TreeBundle 类型 — 正常加载
                    var tree = Manifest?.Tree;
                    ImportedProjectName = tree?.ProjectName;
                    UseImportedProjectName = false; // 默认不勾选，保护现有项目

                    // 检测包内是否有配置文件和进程树
                    HasConfigs = tree?.IncludedConfigs != null && tree.IncludedConfigs.Any();
                    HasProcesses = tree?.Programs != null && tree.Programs.Any();

                    // 设置默认勾选状态 - 根据包内容默认全选
                    ImportConfigs = HasConfigs; // 有配置则默认选中
                    ImportProcesses = HasProcesses; // 有程序则默认选中

                    // 读取包中的进程树数据（List<ProcessItem>）
                    if (HasProcesses)
                    {
                        try
                        {
                            var nodeList =
                                await ExportImportService.ReadProcessTreeFromPackageAsync(
                                    PackagePath
                                );
                            if (nodeList != null && nodeList.Any())
                            {
                                AvailableProcessTree.Clear();

                                // 导出的树应该只有一个虚拟根节点，其下包含实际的二级节点
                                foreach (var rootNode in nodeList)
                                {
                                    AvailableProcessTree.Add(rootNode);

                                    // 记录项目名称（来自虚拟根节点）
                                    if (
                                        rootNode.MetaData != null
                                        && !string.IsNullOrEmpty(rootNode.MetaData.Name)
                                    )
                                    {
                                        ImportedProjectName = rootNode.MetaData.Name;
                                        NLog.LogManager
                                            .GetCurrentClassLogger()
                                            .Info($"Imported project name: {ImportedProjectName}");
                                    }
                                }

                                // 默认全选进程树中的所有节点
                                SetSelection(AvailableProcessTree, true);

                                StatusMessage = $"已加载包信息";
                            }
                            else
                            {
                                HasProcesses = false; // 实际没有进程树数据
                                StatusMessage = "包中没有进程树数据";
                            }
                        }
                        catch (Exception treeEx)
                        {
                            NLog.LogManager.GetCurrentClassLogger().Error(treeEx, "读取进程树失败");
                            HasProcesses = false;
                            StatusMessage = $"读取进程树失败: {treeEx.Message}";
                        }
                    }
                    else
                    {
                        StatusMessage = "包中没有进程树数据";
                    }
                }
                catch (InvalidDataException ex)
                {
                    // ZIP格式错误
                    NLog.LogManager.GetCurrentClassLogger().Error(ex, "无效的包文件格式");
                    StatusMessage = "文件格式错误";
                    MessageBox.Show(
                        $"无效的包文件：{ex.Message}\n\n请选择正确的 .dkp.zip 格式文件。",
                        "文件格式错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                    Manifest = null;
                    HasConfigs = false;
                    HasProcesses = false;
                }
                catch (Exception ex)
                {
                    NLog.LogManager.GetCurrentClassLogger().Error(ex, "读取包信息失败");
                    StatusMessage = $"读取包信息失败: {ex.Message}";
                    MessageBox.Show(
                        $"无法读取包信息：{ex.Message}\n\n请确保：\n1. 文件是有效的 .dkp.zip 包\n2. 文件未被其他程序占用\n3. 文件没有损坏",
                        "错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                    Manifest = null;
                    HasConfigs = false;
                    HasProcesses = false;
                }
            }
        }

        /// <summary>
        /// 从主窗口获取所有进程节点扁平列表
        /// </summary>
        private List<ProcessItem> GetAllProcessNodesFromMainWindow(Window mainWindow)
        {
            var allNodes = new List<ProcessItem>();
            try
            {
                if (
                    mainWindow?.DataContext is MainViewModel mainVM
                    && mainVM.RootProcessNode != null
                )
                {
                    void Collect(ProcessItem item)
                    {
                        if (item == null)
                            return;
                        allNodes.Add(item);
                        if (item.Children != null)
                        {
                            foreach (var child in item.Children)
                                Collect(child);
                        }
                    }
                    Collect(mainVM.RootProcessNode);
                }
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "获取进程节点列表失败");
            }
            return allNodes;
        }

        /// <summary>
        /// 从指定路径加载包信息（供外部调用，如资源库部署）
        /// </summary>
        public async Task LoadPackageFromPathAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                StatusMessage = "包文件不存在";
                return;
            }

            PackagePath = filePath;

            try
            {
                StatusMessage = "读取包信息...";
                Manifest = await ExportImportService.ReadPackageManifestAsync(PackagePath);

                // 如果是 NodeFull / NodePatch 类型，标记为非 TreeBundle 并返回
                // 调用方（ResourceLibraryViewModel）应根据 Manifest.PackageType 做路由
                if (Manifest != null && Manifest.PackageType != PackageType.TreeBundle)
                {
                    HasConfigs = false;
                    HasProcesses = false;
                    StatusMessage = "检测到节点包，需通过更新对话框处理";
                    return;
                }

                var tree = Manifest?.Tree;
                ImportedProjectName = tree?.ProjectName;
                UseImportedProjectName = false;

                HasConfigs = tree?.IncludedConfigs != null && tree.IncludedConfigs.Any();
                HasProcesses = tree?.Programs != null && tree.Programs.Any();

                ImportConfigs = HasConfigs;
                ImportProcesses = HasProcesses;

                if (HasProcesses)
                {
                    try
                    {
                        var nodeList = await ExportImportService.ReadProcessTreeFromPackageAsync(
                            PackagePath
                        );
                        if (nodeList != null && nodeList.Any())
                        {
                            AvailableProcessTree.Clear();
                            foreach (var rootNode in nodeList)
                            {
                                AvailableProcessTree.Add(rootNode);
                                if (
                                    rootNode.MetaData != null
                                    && !string.IsNullOrEmpty(rootNode.MetaData.Name)
                                )
                                {
                                    ImportedProjectName = rootNode.MetaData.Name;
                                }
                            }
                            SetSelection(AvailableProcessTree, true);
                            StatusMessage = "已加载包信息";
                        }
                        else
                        {
                            HasProcesses = false;
                            StatusMessage = "包中没有进程树数据";
                        }
                    }
                    catch (Exception treeEx)
                    {
                        NLog.LogManager.GetCurrentClassLogger().Error(treeEx, "读取进程树失败");
                        HasProcesses = false;
                        StatusMessage = $"读取进程树失败: {treeEx.Message}";
                    }
                }
                else
                {
                    StatusMessage = "包中没有进程树数据";
                }
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Error(ex, "读取包信息失败");
                StatusMessage = $"读取包信息失败: {ex.Message}";
                Manifest = null;
                HasConfigs = false;
                HasProcesses = false;
            }
        }

        private async Task ExecuteImportAsync()
        {
            ProgressWindow progressWindow = null;
            try
            {
                if (string.IsNullOrEmpty(PackagePath) || !System.IO.File.Exists(PackagePath))
                {
                    MessageBox.Show(
                        "请选择有效的配置包文件。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return;
                }

                // 检查是否至少选择了一项导入内容
                if (!ImportConfigs && !ImportProcesses)
                {
                    MessageBox.Show(
                        "请至少选择一项导入内容。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return;
                }

                try
                {
                    // 收集选中的进程节点
                    var selectedNodes = GetSelectedNodes(AvailableProcessTree);

                    // 检查根节点是否被选中
                    bool isRootNodeSelected = false;
                    if (AvailableProcessTree != null && AvailableProcessTree.Count > 0)
                    {
                        var rootNode = AvailableProcessTree.FirstOrDefault(p => p.Parent == null);
                        isRootNodeSelected = rootNode?.IsSelected == true;
                    }

                    // 根节点选中 = 清空模式 + 同步项目名称
                    bool clearMode = isRootNodeSelected;
                    bool useProjectName =
                        isRootNodeSelected && !string.IsNullOrEmpty(ImportedProjectName);

                    // 生成摘要信息
                    var summaryParts = new List<string>();
                    if (ImportConfigs)
                        summaryParts.Add("配置文件");
                    if (ImportProcesses && selectedNodes != null && selectedNodes.Any())
                        summaryParts.Add($"{selectedNodes.Count} 个程序");
                    var summary = $"导入内容: {string.Join("、", summaryParts)}";

                    // 创建进度窗口 ViewModel
                    var progressViewModel = new ProgressWindowViewModel(PackageOperationType.Import)
                    {
                        PackagePath = PackagePath,
                        OperationSummary = summary
                    };

                    // 创建并显示进度窗口
                    progressWindow = new ProgressWindow(progressViewModel)
                    {
                        Owner = Application.Current.MainWindow
                    };
                    progressWindow.Show();

                    // 关闭配置对话框
                    _closeAction?.Invoke(true);

                    // 创建进度回调 - 发送消息到 MessageBus
                    var statusProgress = new Progress<string>(msg =>
                    {
                        ReactiveUI.MessageBus.Current.SendMessage(
                            new PackageProgressInfo
                            {
                                IsActive = true,
                                OperationType = PackageOperationType.Import,
                                StatusMessage = msg,
                                DialogInstance = progressWindow
                            }
                        );
                    });

                    var decompressionProgress = new Progress<CompressionProgress>(p =>
                    {
                        ReactiveUI.MessageBus.Current.SendMessage(
                            new PackageProgressInfo
                            {
                                IsActive = true,
                                OperationType = PackageOperationType.Import,
                                ProgressPercentage = p.Percentage,
                                StatusMessage = "解压文件中...",
                                CurrentFile = p.CurrentFile,
                                DialogInstance = progressWindow
                            }
                        );
                    });

                    var copyProgress = new Progress<FileCopyProgress>(p =>
                    {
                        ReactiveUI.MessageBus.Current.SendMessage(
                            new PackageProgressInfo
                            {
                                IsActive = true,
                                OperationType = PackageOperationType.Import,
                                ProgressPercentage = p.Percentage,
                                StatusMessage = "复制文件中...",
                                CurrentFile = p.CurrentFile,
                                DialogInstance = progressWindow
                            }
                        );
                    });

                    // 执行导入（覆盖模式固定为true）
                    var success = await ExportImportService.ImportPackageAsync(
                        PackagePath,
                        ImportConfigs, // 是否导入配置
                        ImportProcesses && selectedNodes != null && selectedNodes.Any(), // 是否导入进程
                        selectedNodes,
                        clearMode, // 根节点选中时清空
                        true, // 覆盖模式固定为true
                        useProjectName, // 根据根节点选择状态决定是否使用导入的项目名称
                        ImportedProjectName, // 传递导入的项目名称
                        statusProgress,
                        decompressionProgress,
                        copyProgress,
                        progressViewModel.CancellationToken
                    );

                    if (success)
                    {
                        ReactiveUI.MessageBus.Current.SendMessage(
                            new PackageProgressInfo
                            {
                                IsActive = false,
                                OperationType = PackageOperationType.Import,
                                StatusMessage = "导入完成！请重启应用以加载新配置",
                                ProgressPercentage = 100,
                                DialogInstance = progressWindow
                            }
                        );
                    }
                    else
                    {
                        ReactiveUI.MessageBus.Current.SendMessage(
                            new PackageProgressInfo
                            {
                                IsActive = false,
                                OperationType = PackageOperationType.Import,
                                StatusMessage = "导入失败，请查看日志",
                                DialogInstance = progressWindow
                            }
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    NLog.LogManager.GetCurrentClassLogger().Info("导入操作被用户取消");

                    ReactiveUI.MessageBus.Current.SendMessage(
                        new PackageProgressInfo
                        {
                            IsActive = false,
                            OperationType = PackageOperationType.Import,
                            StatusMessage = "导入已取消",
                            DialogInstance = progressWindow
                        }
                    );
                }
                catch (Exception ex)
                {
                    var errorMessage = ex.Message;

                    // 对常见错误提供友好提示
                    if (ex is IOException ioEx && ioEx.HResult == unchecked((int)0x80070020))
                    {
                        errorMessage =
                            $"文件被占用，无法访问。请关闭可能正在使用该文件的程序（如压缩软件、文件管理器等）后重试。\n详细信息：{ex.Message}";
                    }
                    else if (ex is UnauthorizedAccessException)
                    {
                        errorMessage = $"没有访问权限。请确保对目标位置有写入权限，或尝试以管理员身份运行程序。\n详细信息：{ex.Message}";
                    }

                    NLog.LogManager.GetCurrentClassLogger().Error(ex, "导入软件包时发生未处理的异常");

                    ReactiveUI.MessageBus.Current.SendMessage(
                        new PackageProgressInfo
                        {
                            IsActive = false,
                            OperationType = PackageOperationType.Import,
                            StatusMessage = $"导入失败: {errorMessage}",
                            DialogInstance = progressWindow
                        }
                    );
                }
            }
            catch (Exception outerEx)
            {
                // 最外层异常捕获，防止程序崩溃
                NLog.LogManager.GetCurrentClassLogger().Fatal(outerEx, "导入操作发生严重错误");
                ReactiveUI.MessageBus.Current.SendMessage(
                    new PackageProgressInfo
                    {
                        IsActive = false,
                        OperationType = PackageOperationType.Import,
                        StatusMessage = $"严重错误: {outerEx.Message}",
                        DialogInstance = progressWindow
                    }
                );
            }
        }

        /// <summary>
        /// 获取所有选中的节点（避免重复：如果父节点被选中，则不再收集子节点）
        /// </summary>
        private List<ProcessItem> GetSelectedNodes(IEnumerable<ProcessItem> nodes)
        {
            var selected = new List<ProcessItem>();

            void CollectSelected(IEnumerable<ProcessItem> items)
            {
                if (items == null)
                    return;

                foreach (var item in items)
                {
                    if (item.IsSelected)
                    {
                        // 跳过虚拟根节点（没有程序文件的根节点），继续递归其子节点
                        bool isVirtualRoot =
                            item.Parent == null
                            && (
                                string.IsNullOrEmpty(item.MetaData?.Path)
                                || !System.IO.File.Exists(item.NodePath)
                            );

                        if (isVirtualRoot)
                        {
                            // 虚拟根节点：递归收集其选中的子节点
                            if (item.Children != null && item.Children.Count > 0)
                            {
                                CollectSelected(item.Children);
                            }
                        }
                        else
                        {
                            // 实际节点：添加它
                            selected.Add(item);
                        }
                    }
                    else if (item.Children != null && item.Children.Count > 0)
                    {
                        // 仅当父节点未被选中时，才递归检查子节点
                        CollectSelected(item.Children);
                    }
                }
            }

            CollectSelected(nodes);
            return selected;
        }

        private void SetSelection(IEnumerable<ProcessItem> nodes, bool selected)
        {
            if (nodes == null)
                return;

            foreach (var item in nodes)
            {
                item.IsSelected = selected;
                if (item.Children != null && item.Children.Count > 0)
                {
                    SetSelection(item.Children, selected);
                }
            }
        }

        /// <summary>
        /// 设置对话框窗口引用（用于最小化）
        /// </summary>
        public void SetDialogWindow(Window window)
        {
            _dialogWindow = window;

            // 处理窗口关闭事件：如果正在安装，视为最小化
            _dialogWindow.Closing += (s, e) =>
            {
                if (IsImporting)
                {
                    e.Cancel = true;
                    MinimizeCommand.Execute().Subscribe();
                }
            };
        }

        /// <summary>
        /// 发送进度更新到主窗口
        /// </summary>
        private void SendProgressUpdate()
        {
            if (_dialogWindow != null && IsImporting)
            {
                ReactiveUI.MessageBus.Current.SendMessage(
                    new PackageProgressInfo
                    {
                        OperationType = PackageOperationType.Import,
                        IsActive = IsImporting,
                        ProgressPercentage = ProgressPercentage,
                        StatusMessage = StatusMessage,
                        CurrentFile = CurrentFile,
                        DialogInstance = _dialogWindow
                    }
                );
            }
        }
    }
}
