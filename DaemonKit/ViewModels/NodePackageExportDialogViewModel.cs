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
using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using Microsoft.Win32;
using ReactiveUI;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 节点包导出对话框 ViewModel — 创建 NodeFull / NodePatch 包
    /// 补丁模式（Overlay/Replace）已移至导入端，由用户在应用时决定
    /// </summary>
    public class NodePackageExportDialogViewModel : ReactiveObject
    {
        private readonly ProcessItem _node;
        private readonly Action<bool> _closeDialog;
        private CancellationTokenSource _cts;

        /// <summary>程序根目录（由 DetectProgramType + GetProgramRootDirectory 推导）</summary>
        private string _programRootDir;

        #region 属性

        private string _nodeName;
        public string NodeName
        {
            get => _nodeName;
            set => this.RaiseAndSetIfChanged(ref _nodeName, value);
        }

        private string _exeName;
        public string ExeName
        {
            get => _exeName;
            set => this.RaiseAndSetIfChanged(ref _exeName, value);
        }

        private bool _isNodeFull = true;
        public bool IsNodeFull
        {
            get => _isNodeFull;
            set
            {
                this.RaiseAndSetIfChanged(ref _isNodeFull, value);
                this.RaisePropertyChanged(nameof(FileSelectionVisibility));
            }
        }

        private bool _isNodePatch;
        public bool IsNodePatch
        {
            get => _isNodePatch;
            set
            {
                this.RaiseAndSetIfChanged(ref _isNodePatch, value);
                this.RaisePropertyChanged(nameof(FileSelectionVisibility));
                if (value && FileTreeRoots.Count == 0)
                    ScanFiles();
            }
        }

        private string _version;
        public string Version
        {
            get => _version;
            set => this.RaiseAndSetIfChanged(ref _version, value);
        }

        private string _description;
        public string Description
        {
            get => _description;
            set => this.RaiseAndSetIfChanged(ref _description, value);
        }

        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private bool _isExporting;
        public bool IsExporting
        {
            get => _isExporting;
            set
            {
                this.RaiseAndSetIfChanged(ref _isExporting, value);
                this.RaisePropertyChanged(nameof(CanExport));
                this.RaisePropertyChanged(nameof(ProgressVisibility));
            }
        }

        public bool CanExport => !IsExporting;

        public Visibility FileSelectionVisibility =>
            IsNodePatch ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ProgressVisibility =>
            IsExporting ? Visibility.Visible : Visibility.Collapsed;

        #endregion

        #region 文件树

        /// <summary>文件树顶层节点列表（绑定到 TreeView）</summary>
        public ObservableCollection<FileTreeNode> FileTreeRoots { get; } =
            new ObservableCollection<FileTreeNode>();

        private string _fileSelectionSummary;

        /// <summary>已选文件摘要，如 "已选 3 / 120 个文件，共 2.5 MB"</summary>
        public string FileSelectionSummary
        {
            get => _fileSelectionSummary;
            set => this.RaiseAndSetIfChanged(ref _fileSelectionSummary, value);
        }

        private string _filterText;

        /// <summary>搜索过滤文本</summary>
        public string FilterText
        {
            get => _filterText;
            set => this.RaiseAndSetIfChanged(ref _filterText, value);
        }

        public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
        public ReactiveCommand<Unit, Unit> DeselectAllCommand { get; }
        public ReactiveCommand<Unit, Unit> CollapseAllCommand { get; }

        #endregion

        public ReactiveCommand<Unit, Unit> ExportCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        public NodePackageExportDialogViewModel(ProcessItem node, Action<bool> closeDialog)
        {
            _node = node;
            _closeDialog = closeDialog;

            NodeName = node?.Name ?? "未知节点";
            ExeName = node != null ? Path.GetFileName(node.NodePath) : "";

            ExportCommand = ReactiveCommand.CreateFromTask(ExecuteExportAsync);
            CancelCommand = ReactiveCommand.Create(() =>
            {
                _cts?.Cancel();
                _closeDialog?.Invoke(false);
            });

            SelectAllCommand = ReactiveCommand.Create(() =>
            {
                foreach (var root in FileTreeRoots)
                    root.IsSelected = true;
                UpdateSummary();
            });

            DeselectAllCommand = ReactiveCommand.Create(() =>
            {
                foreach (var root in FileTreeRoots)
                    root.IsSelected = false;
                UpdateSummary();
            });

            CollapseAllCommand = ReactiveCommand.Create(() =>
            {
                SetExpandedRecursive(FileTreeRoots, false);
            });

            // 预解析程序根目录
            if (node != null && !string.IsNullOrEmpty(node.NodePath))
            {
                _programRootDir = ExportImportService.GetNodeProgramRootDirectory(node.NodePath);
            }

            // 搜索过滤：输入防抖 300ms 后应用
            this.WhenAnyValue(x => x.FilterText)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(filter =>
                {
                    foreach (var root in FileTreeRoots)
                        root.ApplyFilter(filter);
                });
        }

        /// <summary>
        /// 扫描程序根目录，构建文件树
        /// </summary>
        private void ScanFiles()
        {
            FileTreeRoots.Clear();

            if (string.IsNullOrEmpty(_programRootDir) || !Directory.Exists(_programRootDir))
            {
                FileSelectionSummary = "无法读取程序目录";
                return;
            }

            try
            {
                var tree = FileTreeNode.BuildTree(_programRootDir);
                foreach (var node in tree)
                {
                    SubscribeSelectionChanged(node);
                    FileTreeRoots.Add(node);
                }
                UpdateSummary();
            }
            catch (Exception ex)
            {
                FileSelectionSummary = $"扫描失败: {ex.Message}";
            }
        }

        /// <summary>递归订阅所有节点的选中状态变化</summary>
        private void SubscribeSelectionChanged(FileTreeNode node)
        {
            node.WhenAnyValue(x => x.IsSelected).Subscribe(_ => UpdateSummary());

            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                    SubscribeSelectionChanged(child);
            }
        }

        /// <summary>更新已选文件摘要</summary>
        private void UpdateSummary()
        {
            var allFiles = FileTreeRoots.SelectMany(r => r.GetAllFileNodes()).ToList();
            var selected = allFiles.Count(f => f.IsSelected == true);
            var total = allFiles.Count;
            var totalSize = allFiles.Where(f => f.IsSelected == true).Sum(f => f.FileSize);
            FileSelectionSummary = $"已选 {selected} / {total} 个文件，共 {FormatSize(totalSize)}";
        }

        /// <summary>递归设置展开/折叠状态</summary>
        private static void SetExpandedRecursive(IEnumerable<FileTreeNode> nodes, bool expanded)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory)
                {
                    node.IsExpanded = expanded;
                    SetExpandedRecursive(node.Children, expanded);
                }
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private async Task ExecuteExportAsync()
        {
            if (_node == null)
                return;

            var packageType = IsNodeFull ? PackageType.NodeFull : PackageType.NodePatch;

            // 补丁包模式下验证是否选择了文件
            List<string> selectedFiles = null;
            if (packageType == PackageType.NodePatch)
            {
                selectedFiles = FileTreeRoots.SelectMany(r => r.GetSelectedFiles()).ToList();

                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show(
                        "请至少选择一个文件加入补丁包。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return;
                }
            }

            // 根据包类型确定默认文件扩展名和名称
            var ext = packageType == PackageType.NodePatch ? ".dkp-patch.zip" : ".dkp.zip";
            var typeSuffix = packageType == PackageType.NodePatch ? "补丁" : "全量";
            var defaultName = $"{_node.Name}_{typeSuffix}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";

            var saveDialog = new SaveFileDialog
            {
                Filter =
                    packageType == PackageType.NodePatch
                        ? "DaemonKit 补丁包 (*.dkp-patch.zip)|*.dkp-patch.zip"
                        : "DaemonKit 全量包 (*.dkp.zip)|*.dkp.zip",
                DefaultExt = ext,
                FileName = defaultName
            };

            if (saveDialog.ShowDialog() != true)
                return;

            IsExporting = true;
            _cts = new CancellationTokenSource();

            try
            {
                var statusProgress = new Progress<string>(msg =>
                {
                    StatusMessage = msg;
                });

                var compressionProgress = new Progress<CompressionProgress>(p =>
                {
                    StatusMessage = $"压缩中... {p.Percentage:F0}%";
                });

                var success = await ExportImportService.CreateNodePackageAsync(
                    _node,
                    saveDialog.FileName,
                    packageType,
                    Version,
                    Description,
                    statusProgress,
                    compressionProgress,
                    _cts.Token,
                    selectedFiles
                );

                if (success)
                {
                    // 直接打开导出文件所在文件夹并选中文件
                    System.Diagnostics.Process.Start(
                        "explorer.exe",
                        $"/select,\"{saveDialog.FileName}\""
                    );
                    _closeDialog?.Invoke(true);
                }
                else
                {
                    StatusMessage = "导出失败";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"导出失败: {ex.Message}";
                NLog.LogManager.GetCurrentClassLogger().Error(ex, "节点包导出失败");
            }
            finally
            {
                IsExporting = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}
