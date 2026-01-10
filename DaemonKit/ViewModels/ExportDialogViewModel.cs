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
    /// 导出对话框 ViewModel
    /// </summary>
    public class ExportDialogViewModel : ReactiveObject
    {
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
            set => this.RaiseAndSetIfChanged(ref _isExporting, value);
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

        public ObservableCollection<ProcessItem> ProcessTree { get; set; }
        public ReactiveCommand<Unit, Unit> ExportCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        private CancellationTokenSource _cancellationTokenSource;
        private Action<bool> _closeAction;

        public ExportDialogViewModel(IEnumerable<ProcessItem> processTree, Action<bool> closeAction)
        {
            ProcessTree = new ObservableCollection<ProcessItem>(
                processTree ?? new List<ProcessItem>()
            );
            _closeAction = closeAction;

            // 默认选中配置项
            IncludeConfigs = true;
            IncludePrograms = false;

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
                _closeAction?.Invoke(false);
            });
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

            try
            {
                IsExporting = true;
                ProgressPercentage = 0;
                StatusMessage = "准备导出...";
                _cancellationTokenSource = new CancellationTokenSource();

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

                // 进程树配置跟随程序文件一起导出
                if (IncludePrograms && selectedNodes != null && selectedNodes.Any())
                {
                    if (System.IO.File.Exists(AppPathes.TreeViewDataPath))
                        configFiles.Add(AppPathes.TreeViewDataPath);
                }

                // 创建进度回调
                var statusProgress = new Progress<string>(msg => StatusMessage = msg);
                var compressionProgress = new Progress<CompressionProgress>(p =>
                {
                    ProgressPercentage = p.Percentage;
                    CurrentFile = p.CurrentFile;
                });
                var copyProgress = new Progress<FileCopyProgress>(p =>
                {
                    ProgressPercentage = p.Percentage;
                    CurrentFile = p.CurrentFile;
                });

                // 执行导出
                var success = await ExportImportService.ExportPackageAsync(
                    saveDialog.FileName,
                    configFiles,
                    selectedNodes,
                    IncludePrograms && selectedNodes != null && selectedNodes.Any(),
                    Description,
                    statusProgress,
                    compressionProgress,
                    copyProgress,
                    _cancellationTokenSource.Token
                );

                if (success)
                {
                    MessageBox.Show(
                        "配置包导出成功！",
                        "导出完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    _closeAction?.Invoke(true);
                }
                else
                {
                    MessageBox.Show(
                        "配置包导出失败，请查看日志了解详情。",
                        "导出失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "导出已取消";
                MessageBox.Show("导出操作已取消。", "取消", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"导出失败: {ex.Message}";
                MessageBox.Show(
                    $"导出失败：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            finally
            {
                IsExporting = false;
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
