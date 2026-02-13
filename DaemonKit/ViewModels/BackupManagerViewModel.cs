using DaemonKit.Models;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Windows;

namespace DaemonKit.ViewModels
{
    /// <summary>
    /// 备份管理窗口 ViewModel
    /// </summary>
    public class BackupManagerViewModel : ReactiveObject
    {
        private ObservableCollection<BackupInfo> _backups;
        public ObservableCollection<BackupInfo> Backups
        {
            get => _backups;
            set => this.RaiseAndSetIfChanged(ref _backups, value);
        }

        private BackupInfo _selectedBackup;
        public BackupInfo SelectedBackup
        {
            get => _selectedBackup;
            set => this.RaiseAndSetIfChanged(ref _selectedBackup, value);
        }

        public ReactiveCommand<Unit, Unit> CreateBackupCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenBackupFolderCommand { get; }
        public ReactiveCommand<BackupInfo, Unit> RestoreBackupCommand { get; }
        public ReactiveCommand<BackupInfo, Unit> DeleteBackupCommand { get; }

        public BackupManagerViewModel()
        {
            Backups = new ObservableCollection<BackupInfo>();

            // 创建备份命令
            CreateBackupCommand = ReactiveCommand.Create(() =>
            {
                var backupsDir = AppPathes.BackupsDir;
                if (!Directory.Exists(backupsDir))
                {
                    Directory.CreateDirectory(backupsDir);
                }

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var packageName = $"backup_{Environment.MachineName}_{timestamp}.dkp.zip";
                var packagePath = Path.Combine(backupsDir, packageName);

                var window = new Views.BackupPackageWindow(packagePath);
                window.Owner = Application.Current.MainWindow;
                if (window.ShowDialog() == true)
                {
                    LoadBackups();
                }
            });

            // 刷新列表命令
            RefreshCommand = ReactiveCommand.Create(() =>
            {
                LoadBackups();
            });

            // 打开备份目录命令
            OpenBackupFolderCommand = ReactiveCommand.Create(() =>
            {
                var backupsDir = AppPathes.BackupsDir;
                if (!Directory.Exists(backupsDir))
                {
                    Directory.CreateDirectory(backupsDir);
                }
                System.Diagnostics.Process.Start("explorer.exe", backupsDir);
            });

            // 恢复备份命令
            RestoreBackupCommand = ReactiveCommand.Create<BackupInfo>(backup =>
            {
                if (backup == null)
                    return;

                var result = MessageBox.Show(
                    $"确定要恢复备份 \"{backup.FileName}\" 吗？\n\n恢复后将覆盖当前配置，建议先创建当前配置的备份。",
                    "确认恢复",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result != MessageBoxResult.Yes)
                    return;

                try
                {
                    NLogger.Info($"[Backup] 开始恢复备份: {backup.FullPath}");

                    // 打开导入对话框，让用户选择备份文件后进行导入
                    var importDialog = new Views.ImportDialog();
                    importDialog.Owner = Application.Current.MainWindow;
                    // 设置包路径，导入对话框会自行加载元数据
                    if (importDialog.DataContext is ViewModels.ImportDialogViewModel vm)
                    {
                        vm.PackagePath = backup.FullPath;
                        // 异步加载清单并在UI线程更新
                        Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            try
                            {
                                vm.Manifest =
                                    await Services.ExportImportService.ReadPackageManifestAsync(
                                        backup.FullPath
                                    );
                            }
                            catch (Exception ex)
                            {
                                NLogger.Warn($"[Backup] 读取备份清单失败: {ex.Message}");
                            }
                        });
                    }
                    importDialog.ShowDialog();
                }
                catch (Exception ex)
                {
                    NLogger.Error($"[Backup] 恢复备份失败: {ex.Message}");
                    MessageBox.Show(
                        $"恢复备份失败: {ex.Message}",
                        "错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            });

            // 删除备份命令
            DeleteBackupCommand = ReactiveCommand.Create<BackupInfo>(backup =>
            {
                if (backup == null)
                    return;

                var result = MessageBox.Show(
                    $"确定要删除备份 \"{backup.FileName}\" 吗？\n\n此操作不可恢复。",
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );

                if (result != MessageBoxResult.Yes)
                    return;

                try
                {
                    File.Delete(backup.FullPath);
                    NLogger.Info($"[Backup] 已删除备份: {backup.FullPath}");
                    Backups.Remove(backup);
                }
                catch (Exception ex)
                {
                    NLogger.Error($"[Backup] 删除备份失败: {ex.Message}");
                    MessageBox.Show(
                        $"删除备份失败: {ex.Message}",
                        "错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            });

            // 初始加载 - 异步执行避免阻塞UI
            Application.Current.Dispatcher.InvokeAsync(() => LoadBackups());
        }

        /// <summary>
        /// 加载备份列表
        /// </summary>
        private async void LoadBackups()
        {
            Backups.Clear();

            var backupsDir = AppPathes.BackupsDir;
            if (!Directory.Exists(backupsDir))
            {
                return;
            }

            try
            {
                var files = Directory
                    .GetFiles(backupsDir, "*.dkp.zip")
                    .OrderByDescending(f => new FileInfo(f).CreationTime)
                    .ToList();

                // 在后台线程读取文件信息和元数据
                var backupInfos = await System.Threading.Tasks.Task.Run(() =>
                {
                    var infos = new System.Collections.Generic.List<BackupInfo>();
                    foreach (var file in files)
                    {
                        var backupInfo = BackupInfo.FromFile(file);
                        // 尝试读取描述（从清单）
                        try
                        {
                            var manifest = Services.ExportImportService.ReadPackageManifestSync(
                                file
                            );
                            if (manifest != null)
                            {
                                backupInfo.Description = manifest.Description ?? string.Empty;
                            }
                        }
                        catch
                        {
                            // 忽略元数据读取错误
                        }
                        infos.Add(backupInfo);
                    }
                    return infos;
                });

                // 在UI线程更新集合
                foreach (var info in backupInfos)
                {
                    Backups.Add(info);
                }

                NLogger.Info($"[Backup] 已加载 {Backups.Count} 个备份");
            }
            catch (Exception ex)
            {
                NLogger.Error($"[Backup] 加载备份列表失败: {ex.Message}");
            }
        }
    }
}
