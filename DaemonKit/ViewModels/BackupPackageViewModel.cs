using DaemonKit.Models;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DaemonKit.Views;
using DNHper;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using System.Windows;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 创建备份窗口 ViewModel
    /// </summary>
    public class BackupPackageViewModel : ReactiveObject
    {
        private readonly Window _window;
        private readonly string _defaultPackagePath;

        private string _backupName;
        public string BackupName
        {
            get => _backupName;
            set => this.RaiseAndSetIfChanged(ref _backupName, value);
        }

        private string _description;
        public string Description
        {
            get => _description;
            set => this.RaiseAndSetIfChanged(ref _description, value);
        }

        private bool _includePrograms;
        public bool IncludePrograms
        {
            get => _includePrograms;
            set => this.RaiseAndSetIfChanged(ref _includePrograms, value);
        }

        private bool _isExporting;
        public bool IsExporting
        {
            get => _isExporting;
            set
            {
                this.RaiseAndSetIfChanged(ref _isExporting, value);
                this.RaisePropertyChanged(nameof(IsNotExporting));
            }
        }

        public bool IsNotExporting => !IsExporting;

        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private double _progressPercentage;
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => this.RaiseAndSetIfChanged(ref _progressPercentage, value);
        }

        public ReactiveCommand<Unit, Unit> CreateBackupCommand { get; }

        public BackupPackageViewModel(string defaultPackagePath, Window window)
        {
            _window = window;
            _defaultPackagePath = defaultPackagePath;

            // 从默认路径提取备份名称
            BackupName = Path.GetFileNameWithoutExtension(defaultPackagePath);
            Description = $"手动备份 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            IncludePrograms = false;

            CreateBackupCommand = ReactiveCommand.CreateFromTask(CreateBackupAsync);
        }

        private async Task CreateBackupAsync()
        {
            try
            {
                IsExporting = true;
                StatusMessage = "准备创建备份...";
                ProgressPercentage = 0;

                // 构建最终文件路径
                var backupsDir = AppPathes.BackupsDir;
                if (!Directory.Exists(backupsDir))
                {
                    Directory.CreateDirectory(backupsDir);
                }

                var safeName = string.Join("_", BackupName.Split(Path.GetInvalidFileNameChars()));
                var packagePath = Path.Combine(backupsDir, safeName + ".dkp.zip");

                NLogger.Info($"[Backup] 开始创建备份: {packagePath}");

                // 获取主窗口的进程树根节点
                var mainWindow = Application.Current.MainWindow as DaemonKit.MainWindow;
                if (mainWindow?.ViewModel?.RootProcessNode == null)
                {
                    throw new InvalidOperationException("无法获取进程树根节点");
                }

                var rootNode = mainWindow.ViewModel.RootProcessNode;

                // 收集配置文件
                var configFiles = new List<string>
                {
                    AppPathes.TreeViewDataPath,
                    AppPathes.AppSettingPath,
                    AppPathes.GlobalSchedulePath,
                    AppPathes.ScheduleConfigPath,
                    AppPathes.HotkeyConfigPath,
                    AppPathes.ExtensionConfigPath
                }
                    .Where(File.Exists)
                    .ToList();

                // 收集进程树所有节点
                var allNodes = new List<ProcessItem> { rootNode };
                CollectAllNodes(rootNode, allNodes);

                // 进度回调
                var statusProgress = new Progress<string>(msg =>
                {
                    StatusMessage = msg;
                });

                var compressionProgress = new Progress<CompressionProgress>(p =>
                {
                    ProgressPercentage = p.Percentage;
                    StatusMessage = $"压缩中: {p.CurrentFile}";
                });

                // 执行导出
                var success = await ExportImportService.ExportPackageAsync(
                    packagePath,
                    configFiles,
                    allNodes,
                    includeAllPrograms: IncludePrograms,
                    description: Description,
                    statusProgress: statusProgress,
                    compressionProgress: compressionProgress
                );

                if (success)
                {
                    NLogger.Info($"[Backup] 备份创建成功: {packagePath}");
                    MessageBox.Show(
                        $"备份创建成功！\n\n文件路径: {packagePath}",
                        "成功",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );

                    _window.DialogResult = true;
                    _window.Close();
                }
                else
                {
                    throw new Exception("备份创建失败，请查看日志");
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"[Backup] 创建备份失败: {ex.Message}");
                MessageBox.Show(
                    $"创建备份失败: {ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            finally
            {
                IsExporting = false;
            }
        }

        private void CollectAllNodes(ProcessItem node, List<ProcessItem> nodes)
        {
            if (node?.Children == null)
                return;
            foreach (var child in node.Children)
            {
                nodes.Add(child);
                CollectAllNodes(child, nodes);
            }
        }
    }
}
