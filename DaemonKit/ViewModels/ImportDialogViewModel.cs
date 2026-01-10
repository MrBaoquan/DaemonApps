using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using Microsoft.Win32;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private PackageMetadata _metadata;
        private bool _importConfigs; // 是否导入配置
        private bool _importProcesses; // 是否导入进程
        private bool _hasConfigs; // 包内是否有配置文件
        private bool _hasProcesses; // 包内是否有进程树数据
        private bool _clearExistingTree;
        private bool _overwriteConflicts; // 新增：冲突时是否覆盖
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

        public PackageMetadata Metadata
        {
            get => _metadata;
            set => this.RaiseAndSetIfChanged(ref _metadata, value);
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

        public ObservableCollection<ProcessItem> AvailableProcessTree
        {
            get => _availableProcessTree;
            set => this.RaiseAndSetIfChanged(ref _availableProcessTree, value);
        }

        public bool IsImporting
        {
            get => _isImporting;
            set => this.RaiseAndSetIfChanged(ref _isImporting, value);
        }

        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => this.RaiseAndSetIfChanged(ref _progressPercentage, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public string CurrentFile
        {
            get => _currentFile;
            set => this.RaiseAndSetIfChanged(ref _currentFile, value);
        }

        public ReactiveCommand<Unit, Unit> BrowseCommand { get; }
        public ReactiveCommand<Unit, Unit> ImportCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        private CancellationTokenSource _cancellationTokenSource;
        private Action<bool> _closeAction;

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
                _closeAction?.Invoke(false);
            });
        }

        private async Task ExecuteBrowseAsync()
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "DaemonKit 配置包 (*.dkit)|*.dkit",
                DefaultExt = ".dkit"
            };

            if (openDialog.ShowDialog() == true)
            {
                PackagePath = openDialog.FileName;

                // 读取元数据
                try
                {
                    StatusMessage = "读取包信息...";
                    Metadata = await ExportImportService.ReadPackageMetadataAsync(PackagePath);

                    // 检测包内是否有配置文件和进程树
                    HasConfigs =
                        Metadata?.IncludedConfigs != null && Metadata.IncludedConfigs.Any();
                    HasProcesses =
                        Metadata?.IncludedPrograms != null && Metadata.IncludedPrograms.Any();

                    // 设置默认勾选状态
                    ImportConfigs = HasConfigs;
                    ImportProcesses = false; // 默认不勾选进程导入

                    // 读取包中的进程树数据（List<ProcessItem>）
                    if (HasProcesses)
                    {
                        var nodeList = await ExportImportService.ReadProcessTreeFromPackageAsync(
                            PackagePath
                        );
                        if (nodeList != null && nodeList.Any())
                        {
                            AvailableProcessTree.Clear();
                            // 直接显示所有节点（支持多根节点）
                            foreach (var node in nodeList)
                            {
                                AvailableProcessTree.Add(node);
                            }
                            StatusMessage = $"已加载包信息";
                        }
                        else
                        {
                            HasProcesses = false; // 实际没有进程树数据
                            StatusMessage = "包中没有进程树数据";
                        }
                    }
                    else
                    {
                        StatusMessage = "包中没有进程树数据";
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = $"读取包信息失败: {ex.Message}";
                    MessageBox.Show(
                        $"无法读取包信息：{ex.Message}",
                        "错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                    Metadata = null;
                }
            }
        }

        private async Task ExecuteImportAsync()
        {
            if (string.IsNullOrEmpty(PackagePath) || !System.IO.File.Exists(PackagePath))
            {
                MessageBox.Show("请选择有效的配置包文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 确认对话框
            var confirmMessage = "确定要导入选中的内容吗？";
            if (ImportConfigs && ImportProcesses)
            {
                confirmMessage = "将导入配置文件和进程树，是否继续？";
            }
            else if (ImportConfigs)
            {
                confirmMessage = "将导入配置文件，是否继续？";
            }
            else if (ImportProcesses)
            {
                confirmMessage = "将导入进程树，是否继续？";
            }
            else
            {
                MessageBox.Show("请至少选择一项导入内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                confirmMessage,
                "确认导入",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                IsImporting = true;
                ProgressPercentage = 0;
                StatusMessage = "准备导入...";
                _cancellationTokenSource = new CancellationTokenSource();

                // 创建进度回调
                var statusProgress = new Progress<string>(msg => StatusMessage = msg);
                var decompressionProgress = new Progress<CompressionProgress>(p =>
                {
                    ProgressPercentage = p.Percentage;
                    CurrentFile = p.CurrentFile;
                });
                var copyProgress = new Progress<FileCopyProgress>(p =>
                {
                    ProgressPercentage = p.Percentage;
                    CurrentFile = p.CurrentFile;
                });

                // 收集选中的进程节点
                var selectedNodes = GetSelectedNodes(AvailableProcessTree);

                // 执行导入（覆盖配置固定为true，AutoMovePrograms固定为true）
                var success = await ExportImportService.ImportPackageAsync(
                    PackagePath,
                    ImportConfigs, // 是否导入配置
                    ImportProcesses && selectedNodes != null && selectedNodes.Any(), // 是否导入进程
                    selectedNodes,
                    ClearExistingTree,
                    OverwriteConflicts, // 传递冲突覆盖策略
                    statusProgress,
                    decompressionProgress,
                    copyProgress,
                    _cancellationTokenSource.Token
                );

                if (success)
                {
                    MessageBox.Show(
                        "配置包导入成功！请重启应用以加载新配置。",
                        "导入完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    _closeAction?.Invoke(true);
                }
                else
                {
                    MessageBox.Show(
                        "配置包导入失败，请查看日志了解详情。",
                        "导入失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "导入已取消";
                MessageBox.Show("导入操作已取消。", "取消", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"导入失败: {ex.Message}";
                MessageBox.Show(
                    $"导入失败：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            finally
            {
                IsImporting = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
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
                        // 添加选中的节点（包括其所有子树）
                        selected.Add(item);
                        // 不再递归子节点，因为子树已经包含在父节点中
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
    }
}
