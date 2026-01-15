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
    /// 导出对话框 ViewModel
    /// </summary>
    public class ExportDialogViewModel : ReactiveObject
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private bool _includeConfigs;
        private bool _includePrograms;
        private string _description;
        private bool _isExporting;
        private double _progressPercentage;
        private string _statusMessage;
        private string _currentFile;

        public bool IncludeConfigs
        {
            get => _includeConfigs;
            set => this.RaiseAndSetIfChanged(ref _includeConfigs, value);
        }

        public bool IncludePrograms
        {
            get => _includePrograms;
            set => this.RaiseAndSetIfChanged(ref _includePrograms, value);
        }

        public string Description
        {
            get => _description;
            set => this.RaiseAndSetIfChanged(ref _description, value);
        }

        public bool IsExporting
        {
            get => _isExporting;
            set
            {
                this.RaiseAndSetIfChanged(ref _isExporting, value);
                // 发送进度更新
                SendProgressUpdate();
            }
        }

        public double ProgressPercentage
        {
            get => _progressPercentage;
            set
            {
                this.RaiseAndSetIfChanged(ref _progressPercentage, value);
                // 发送进度更新
                SendProgressUpdate();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                this.RaiseAndSetIfChanged(ref _statusMessage, value);
                // 发送进度更新
                SendProgressUpdate();
            }
        }

        public string CurrentFile
        {
            get => _currentFile;
            set
            {
                this.RaiseAndSetIfChanged(ref _currentFile, value);
                // 发送进度更新
                SendProgressUpdate();
            }
        }

        public ObservableCollection<ProcessItem> ProcessTree { get; set; }
        public ReactiveCommand<Unit, Unit> ExportCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearSelectionCommand { get; }
        public ReactiveCommand<Unit, Unit> MinimizeCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelExportCommand { get; }

        // 选中节点列表（用于显示）
        private string _selectedNodesText;
        public string SelectedNodesText
        {
            get => _selectedNodesText;
            set => this.RaiseAndSetIfChanged(ref _selectedNodesText, value);
        }

        private CancellationTokenSource _cancellationTokenSource;
        private Action<bool> _closeAction;
        private Window _dialogWindow;

        public ExportDialogViewModel(IEnumerable<ProcessItem> processTree, Action<bool> closeAction)
        {
            ProcessTree = new ObservableCollection<ProcessItem>(
                processTree ?? new List<ProcessItem>()
            );
            _closeAction = closeAction;

            // 确保根节点始终选中且不可取消
            if (ProcessTree != null && ProcessTree.Count > 0)
            {
                var rootNode = ProcessTree.FirstOrDefault(p => p.Parent == null);
                if (rootNode != null)
                {
                    rootNode.IsSelected = true;
                    // 订阅根节点的IsSelected变化，确保它始终为true
                    rootNode
                        .WhenAnyValue(x => x.IsSelected)
                        .Where(isSelected => !isSelected)
                        .Subscribe(_ => rootNode.IsSelected = true);
                }
            }

            // 默认全选配置项和程序
            IncludeConfigs = true;
            IncludePrograms = true;

            // 默认全选进程树中的所有节点
            SetSelection(ProcessTree, true);

            var canExport = this.WhenAnyValue(
                x => x.IsExporting,
                x => x.IncludeConfigs,
                x => x.IncludePrograms,
                (isExporting, configs, programs) => !isExporting && (configs || programs)
            );

            ExportCommand = ReactiveCommand.CreateFromTask(ExecuteExportAsync, canExport);
            CancelCommand = ReactiveCommand.Create(() =>
            {
                _cancellationTokenSource?.Cancel();
                // 直接关闭窗口，避免在非对话框模式下触发 DialogResult 异常
                _dialogWindow?.Close();
            });

            // 选择操作
            SelectAllCommand = ReactiveCommand.Create(() => SetSelection(ProcessTree, true));
            ClearSelectionCommand = ReactiveCommand.Create(() => SetSelection(ProcessTree, false));

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
                            OperationType = PackageOperationType.Export,
                            IsActive = IsExporting,
                            ProgressPercentage = ProgressPercentage,
                            StatusMessage = StatusMessage,
                            CurrentFile = CurrentFile,
                            DialogInstance = _dialogWindow
                        }
                    );
                }
            });

            // 取消打包命令
            var canCancel = this.WhenAnyValue(x => x.IsExporting);
            CancelExportCommand = ReactiveCommand.Create(
                () =>
                {
                    _cancellationTokenSource?.Cancel();
                },
                canCancel
            );
        }

        private async Task ExecuteExportAsync()
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "DaemonKit 配置包 (*.dkit)|*.dkit",
                DefaultExt = ".dkit",
                FileName = $"DaemonKit_Export_{DateTime.Now:yyyyMMdd_HHmmss}.dkit"
            };

            if (saveDialog.ShowDialog() != true)
                return;

            ProgressWindow progressWindow = null;
            try
            {
                // 收集要导出的配置文件
                var configFiles = new List<string>();

                // 其他配置项（任务计划、快捷键等）
                if (IncludeConfigs)
                {
                    if (System.IO.File.Exists(AppPathes.ScheduleConfigPath))
                        configFiles.Add(AppPathes.ScheduleConfigPath);
                    if (System.IO.File.Exists(AppPathes.HotkeyConfigPath))
                        configFiles.Add(AppPathes.HotkeyConfigPath);
                    if (System.IO.File.Exists(AppPathes.AppSettingPath))
                        configFiles.Add(AppPathes.AppSettingPath);
                    if (System.IO.File.Exists(AppPathes.GlobalSchedulePath))
                        configFiles.Add(AppPathes.GlobalSchedulePath);
                }

                // 收集选中的进程节点
                var selectedNodes = IncludePrograms ? GetSelectedNodes(ProcessTree) : null;

                // 生成摘要信息
                var summaryParts = new List<string>();
                if (IncludeConfigs)
                    summaryParts.Add("配置");
                if (IncludePrograms && selectedNodes != null && selectedNodes.Any())
                    summaryParts.Add($"{selectedNodes.Count}个程序");

                var summary = summaryParts.Any() ? string.Join("、", summaryParts) : "无";

                // 创建进度窗口
                var progressViewModel = new ProgressWindowViewModel(PackageOperationType.Export)
                {
                    PackagePath = saveDialog.FileName,
                    OperationSummary = summary
                };
                progressWindow = new ProgressWindow(progressViewModel)
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };

                // 关闭配置对话框
                _closeAction?.Invoke(true);

                // 显示进度窗口
                progressWindow.Show();

                // 进程树配置跟随程序文件一起导出
                if (IncludePrograms && selectedNodes != null && selectedNodes.Any())
                {
                    if (System.IO.File.Exists(AppPathes.TreeViewDataPath))
                        configFiles.Add(AppPathes.TreeViewDataPath);
                }

                // 创建进度回调
                var statusProgress = new Progress<string>(msg =>
                {
                    ReactiveUI.MessageBus.Current.SendMessage(
                        new PackageProgressInfo
                        {
                            IsActive = true,
                            StatusMessage = msg,
                            OperationType = PackageOperationType.Export,
                            DialogInstance = progressWindow
                        }
                    );
                });
                var compressionProgress = new Progress<CompressionProgress>(p =>
                {
                    ReactiveUI.MessageBus.Current.SendMessage(
                        new PackageProgressInfo
                        {
                            IsActive = true,
                            ProgressPercentage = p.Percentage,
                            CurrentFile = p.CurrentFile,
                            OperationType = PackageOperationType.Export,
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
                            ProgressPercentage = p.Percentage,
                            CurrentFile = p.CurrentFile,
                            OperationType = PackageOperationType.Export,
                            DialogInstance = progressWindow
                        }
                    );
                });

                // 执行导出
                var success = await ExportImportService.ExportPackageAsync(
                    saveDialog.FileName,
                    configFiles,
                    selectedNodes ?? Enumerable.Empty<ProcessItem>(),
                    IncludePrograms && selectedNodes != null && selectedNodes.Any(),
                    Description,
                    statusProgress,
                    compressionProgress,
                    copyProgress,
                    progressViewModel.CancellationToken
                );

                if (success)
                {
                    ReactiveUI.MessageBus.Current.SendMessage(
                        new PackageProgressInfo
                        {
                            StatusMessage = "导出完成",
                            ProgressPercentage = 100,
                            IsActive = false,
                            OperationType = PackageOperationType.Export,
                            DialogInstance = progressWindow
                        }
                    );
                }
                else
                {
                    ReactiveUI.MessageBus.Current.SendMessage(
                        new PackageProgressInfo
                        {
                            StatusMessage = "导出失败，请查看日志",
                            ProgressPercentage = 0,
                            IsActive = false,
                            OperationType = PackageOperationType.Export,
                            DialogInstance = progressWindow
                        }
                    );
                }
            }
            catch (OperationCanceledException)
            {
                NLog.LogManager.GetCurrentClassLogger().Info("导出操作被用户取消");
                ReactiveUI.MessageBus.Current.SendMessage(
                    new PackageProgressInfo
                    {
                        StatusMessage = "导出已取消",
                        IsActive = false,
                        OperationType = PackageOperationType.Export,
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

                NLog.LogManager.GetCurrentClassLogger().Error(ex, "导出软件包时发生异常");
                ReactiveUI.MessageBus.Current.SendMessage(
                    new PackageProgressInfo
                    {
                        StatusMessage = $"导出失败: {errorMessage}",
                        IsActive = false,
                        OperationType = PackageOperationType.Export,
                        DialogInstance = progressWindow
                    }
                );
            }
            finally
            {
                IsExporting = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                // 通知主窗口移除状态栏进度指示（确保异常退出时也清理）
                // 注意：不重置ProgressPercentage，让它保持在最后的值（通常是100%）
                ReactiveUI.MessageBus.Current.SendMessage(
                    new PackageProgressInfo
                    {
                        IsActive = false,
                        OperationType = PackageOperationType.Export
                    }
                );
            }
        }

        /// <summary>
        /// 获取用户选中的二级节点（父节点是超级根节点的节点）。
        /// 三级及以下节点自动作为二级节点的子树包含，不需要单独导出。
        /// 如果用户选择了三级节点，其父级二级节点会被依赖链自动选中，因此这里只需收集父级二级节点。
        /// </summary>
        private List<ProcessItem> GetSelectedNodes(IEnumerable<ProcessItem> nodes)
        {
            var selected = new List<ProcessItem>();

            void Collect(IEnumerable<ProcessItem> items)
            {
                if (items == null)
                    return;

                foreach (var item in items)
                {
                    // 只收集：被选中 且 父节点是超级根节点（即二级节点）
                    if (item.IsSelected && item.Parent != null && item.Parent.IsSuperRoot)
                    {
                        selected.Add(item);
                        Logger.Info(
                            $"[GetSelectedNodes] Selected Level-2 node: {item.Name}, NodeId={item.NodeId}"
                        );
                    }

                    // 递归继续向下，确保能找到层级较深处被选中而父级为超级根的节点
                    if (item.Children != null && item.Children.Count > 0)
                    {
                        Collect(item.Children);
                    }
                }
            }

            Collect(nodes);
            Logger.Info($"[GetSelectedNodes] Total selected Level-2 nodes: {selected.Count}");
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

            // 处理窗口关闭事件：如果正在导出，视为最小化
            _dialogWindow.Closing += (s, e) =>
            {
                if (IsExporting)
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
            if (_dialogWindow != null && IsExporting)
            {
                ReactiveUI.MessageBus.Current.SendMessage(
                    new PackageProgressInfo
                    {
                        OperationType = PackageOperationType.Export,
                        IsActive = IsExporting,
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
