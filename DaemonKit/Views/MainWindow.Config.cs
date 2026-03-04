using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DaemonKit.Core;
using DaemonKit.Models;
using DaemonKit.PowerSaving;
using DaemonKit.Services;
using DaemonKit.Utilities;
using DNHper;
using ReactiveMarbles.ObservableEvents;
using ReactiveUI;

namespace DaemonKit
{
    public partial class MainWindow
    {
        #region Configuration Management

        /// <summary>
        /// 加载拓展菜单
        /// </summary>
        private void loadExtensions()
        {
            if (!File.Exists(AppPathes.ExtensionConfigPath))
            {
                USerialization.SerializeXML(new ExtensionConfig(), AppPathes.ExtensionConfigPath);
            }

            try
            {
                var _extConfig = USerialization.DeserializeXML<ExtensionConfig>(
                    AppPathes.ExtensionConfigPath
                );
                var _sysMgrMenu = new MenuItem { Header = "系统" };
                var _toolMenu = new MenuItem { Header = "工具" };

                // 统计 System 和 Tool 类别的项数，用于添加分隔线
                int systemItemCount = _extConfig.Extensions.Count(e => e.Group == "System");
                int systemBasicCount = 2; // 前两项：控制面板、任务管理器
                bool systemSeparatorAdded = false;

                _extConfig.Extensions
                    .WithIndex()
                    .ToList()
                    .ForEach(_extention =>
                    {
                        var _menuItem = new MenuItem { Header = _extention.item.Name };

                        Action<(Extension item, int index)> _handleMenuClick = (_ext) =>
                        {
                            var _extensionPath = Path.Combine(
                                AppPathes.ExtensionPath,
                                _ext.item.Path
                            );
                            if (!Path.IsPathRooted(_ext.item.Path) && File.Exists(_extensionPath))
                            {
                                WinAPI.OpenProcess(_extensionPath, _ext.item.Args, _ext.item.RunAs);
                            }
                            else
                            {
                                WinAPI.OpenProcess(_ext.item.Path, _ext.item.Args, _ext.item.RunAs);
                            }
                        };

                        _menuItem
                            .Events()
                            .Click.Subscribe(_ =>
                            {
                                _handleMenuClick(_extention);
                            });

                        var _menuCommand = ReactiveCommand.Create<
                            (Extension item, int index),
                            (Extension item, int index)
                        >(_param => _param);
                        _menuCommand.Subscribe(_ext =>
                        {
                            _handleMenuClick(_ext);
                        });

                        //_menuItem.InputGestureText = string.Format ("Ctrl+F{0}", _extention.index + 1);
                        InputBindings.Add(
                            new KeyBinding
                            {
                                Command = _menuCommand,
                                Key = Key.F1 + _extention.index,
                                Modifiers = ModifierKeys.Control,
                                CommandParameter = _extention
                            }
                        );
                        if (_extention.item.Group == "System")
                        {
                            // 在基础系统工具和高级设置项之间添加分隔线
                            if (
                                _sysMgrMenu.Items.Count == systemBasicCount
                                && !systemSeparatorAdded
                                && systemItemCount > systemBasicCount
                            )
                            {
                                _sysMgrMenu.Items.Add(new Separator());
                                systemSeparatorAdded = true;
                            }
                            _sysMgrMenu.Items.Add(_menuItem);
                        }
                        else
                        {
                            _toolMenu.Items.Add(_menuItem);
                        }
                    });

                this.MainMenu.Items.Insert(2, _sysMgrMenu);
                this.MainMenu.Items.Insert(3, _toolMenu);
            }
            catch (Exception ex)
            {
                NLogger.Warn("加载扩展菜单失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 加载配置文件
        /// </summary>
        private void loadConfig()
        {
            if (!File.Exists(AppPathes.TreeViewDataPath))
            {
                if (!Directory.Exists(Path.GetDirectoryName(AppPathes.TreeViewDataPath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(AppPathes.TreeViewDataPath));
                if (File.Exists(AppPathes.TreeViewDataPath_Backup))
                {
                    File.Copy(AppPathes.TreeViewDataPath_Backup, AppPathes.TreeViewDataPath, true);
                }
                else
                {
                    rootProcessNode = new ProcessItem
                    {
                        MetaData = new ProcessMetaData
                        {
                            Name = "[ 进程树 ]",
                            Delay = 0,
                            Path = string.Empty
                        }
                    };
                    USerialization.SerializeXML(rootProcessNode, AppPathes.TreeViewDataPath);
                }
            }
            if (
                File.ReadAllText(AppPathes.TreeViewDataPath).Length == 0
                && File.Exists(AppPathes.TreeViewDataPath_Backup)
            )
            {
                File.Copy(AppPathes.TreeViewDataPath_Backup, AppPathes.TreeViewDataPath, true);
            }
            rootProcessNode = USerialization.DeserializeXML<ProcessItem>(
                AppPathes.TreeViewDataPath
            );
            rootProcessNode.SyncRelationships();

            // 将 rootProcessNode 传递给 ViewModel 以便 XAML 绑定
            ViewModel.RootProcessNode = rootProcessNode;

            if (!File.Exists(AppPathes.AppSettingPath))
            {
                USerialization.SerializeXML(new AppSettings(), AppPathes.AppSettingPath);
            }
            if (
                File.ReadAllText(AppPathes.AppSettingPath).Length == 0
                && File.Exists(AppPathes.AppSettingPath_Backup)
            )
            {
                File.Copy(AppPathes.AppSettingPath_Backup, AppPathes.AppSettingPath, true);
            }
            AppSettings = USerialization.DeserializeXML<AppSettings>(AppPathes.AppSettingPath);

            // 应用端口覆盖和认证设置
            CommonVars.ApplyPortOverrides(
                AppSettings.CustomMetaPort,
                AppSettings.CustomControlPort,
                AppSettings.CustomFileTransferPort,
                AppSettings.AuthToken
            );

            // 加载全局计划任务配置
            if (!File.Exists(AppPathes.GlobalSchedulePath))
            {
                // 首次运行或升级，从 rootProcessNode 迁移数据
                NLogger.Info("未找到全局计划任务配置，尝试迁移旧数据...");
                GlobalSchedule = MigrateScheduleTasksToGlobal(rootProcessNode);
                USerialization.SerializeXML(GlobalSchedule, AppPathes.GlobalSchedulePath);
                NLogger.Info("已迁移 {Count} 个计划任务到全局配置", GlobalSchedule.ScheduleTasks.Count);
            }
            else
            {
                if (
                    File.ReadAllText(AppPathes.GlobalSchedulePath).Length == 0
                    && File.Exists(AppPathes.GlobalSchedulePath_Backup)
                )
                {
                    File.Copy(
                        AppPathes.GlobalSchedulePath_Backup,
                        AppPathes.GlobalSchedulePath,
                        true
                    );
                }
                GlobalSchedule = USerialization.DeserializeXML<GlobalScheduleConfig>(
                    AppPathes.GlobalSchedulePath
                );
            }

            // 验证全局配置
            if (!GlobalSchedule.Validate(out string validationError))
            {
                NLogger.Warn("全局计划任务配置验证失败: {ValidationError}", validationError);
            }

            // 将全局配置传递给 ViewModel
            ViewModel.GlobalSchedule = GlobalSchedule;

            Utils.SyncSettings();
            rootProcessNode.SyncSettings(AppSettings);

            // 根据配置决定是否注册全局快捷键
            if (AppSettings.EnableGlobalHotKey)
            {
                Utils.RegisterHotKey(this, AppSettings);
                NLogger.Info("已注册全局快捷键");
            }

            // 注册前台窗口监听，实现远程工具焦点时自动挂起快捷键
            RegisterForegroundHook();

            // 根据配置决定是否禁用触摸屏
            // 注意：SetupDI 设备枚举可能耗时数秒（遍历全部设备类），必须移至后台线程
            if (AppSettings.DisableTouchScreen)
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (DeviceManager.SetTouchScreenEnabled(false))
                        {
                            NLogger.Info("触摸屏已禁用");
                        }
                        else
                        {
                            NLogger.Warn("触摸屏禁用失败");
                        }
                    }
                    catch (Exception ex)
                    {
                        NLogger.Error("初始化触摸屏状态时发生异常: {Message}", ex.Message);
                    }
                });
            }

            // 初始化服务层（确保 AppSettings 已加载）
            _powerSavingService.Initialize(AppSettings);
            _idleMonitorService = new IdleMonitorService(_powerSavingService, AppSettings);
            _idleMonitorService.StartMonitoring();

            if (ViewModel != null)
            {
                ViewModel.PowerSaving = _powerSavingService.ViewModel;
            }
        }

        /// <summary>
        /// 导入包后热重载：停止旧进程树 → 重新加载配置 → 刷新 UI → 重建服务
        /// </summary>
        /// <param name="autoStartProcessTree">是否自动启动新进程树（手动导入=false, 自动导入=true）</param>
        private void ReloadAfterImport(bool autoStartProcessTree = false)
        {
            try
            {
                // 1. 停止旧进程树
                NLogger.Info("[导入] 停止当前进程树...");
                rootProcessNode?.KillNode();

                // 2. 销毁旧的任务调度引擎
                _scheduleTaskEngine?.Dispose();

                // 3. 停止旧的空闲监控服务
                _idleMonitorService?.StopMonitoring();

                // 4. 重新加载配置（反序列化新的 rootProcessNode + AppSettings + GlobalSchedule）
                NLogger.Info("[导入] 重新加载配置...");
                loadConfig();

                // 5. 刷新 TreeView UI 绑定
                this.ProcessTree.Items.Clear();
                this.ProcessTree.Items.Add(rootProcessNode);

                // 6. 重建任务调度引擎
                _scheduleTaskEngine = new ScheduleTaskEngine(rootProcessNode, GlobalSchedule)
                {
                    ConfirmHandler = ConfirmSchedulePowerActionAsync,
                    PowerSavingViewModelProvider = () => _powerSavingService.ViewModel
                };
                _scheduleTaskEngine.TaskExecuting += (sender, context) =>
                {
                    NLogger.Info(
                        "[任务] 执行: [{TaskName}] - {Action}",
                        context.TaskConfig.Name,
                        context.TaskConfig.Action
                    );
                };
                _scheduleTaskEngine.TaskExecuted += (sender, context) =>
                {
                    if (context.IsSuccess)
                    {
                        NLogger.Info("[任务] 完成: {Result}", context.Result);
                    }
                    else
                    {
                        NLogger.Error("[任务] 失败: {ErrorMessage}", context.ErrorMessage);
                    }
                };

                // 7. 按需启动新进程树
                if (autoStartProcessTree)
                {
                    NLogger.Info("[导入] 自动启动新进程树");
                    rootProcessNode.RunNode();
                }

                NLogger.Info("[导入] 热重载完成");
            }
            catch (Exception ex)
            {
                NLogger.Error("[导入] 热重载失败: {ErrorMessage}", ex.Message);
                MessageBox.Show(
                    $"重新加载配置失败：{ex.Message}\n\n建议重启应用。",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        // 数据持久化
        private void saveConfig()
        {
            try
            {
                // 尝试保存配置，如果文件被锁定则重试
                SaveConfigWithRetry(() =>
                {
                    USerialization.SerializeXML(rootProcessNode, AppPathes.TreeViewDataPath);
                    USerialization.SerializeXML(AppSettings, AppPathes.AppSettingPath);
                    USerialization.SerializeXML(GlobalSchedule, AppPathes.GlobalSchedulePath);
                });

                // 备份配置文件（只备份成功保存的文件）
                try
                {
                    File.Copy(AppPathes.TreeViewDataPath, AppPathes.TreeViewDataPath_Backup, true);
                }
                catch (Exception ex)
                {
                    NLogger.Warn("备份 TreeView 配置失败: {Message}", ex.Message);
                }

                try
                {
                    File.Copy(
                        AppPathes.ExtensionConfigPath,
                        AppPathes.ExtensionConfigPath_Backup,
                        true
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn("备份扩展配置失败: {Message}", ex.Message);
                }

                try
                {
                    File.Copy(AppPathes.AppSettingPath, AppPathes.AppSettingPath_Backup, true);
                }
                catch (Exception ex)
                {
                    NLogger.Warn("备份应用设置失败: {ErrorMessage}", ex.Message);
                }

                try
                {
                    File.Copy(
                        AppPathes.GlobalSchedulePath,
                        AppPathes.GlobalSchedulePath_Backup,
                        true
                    );
                }
                catch (Exception ex)
                {
                    NLogger.Warn("备份全局计划失败: {ErrorMessage}", ex.Message);
                }

                NLogger.Info("配置文件保存成功.");
            }
            catch (Exception ex)
            {
                NLogger.Error("保存配置文件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 只保存 AppSettings，不保存其他配置（用于节能模式配置频繁更新的场景）
        /// </summary>
        private void SaveAppSettingsOnly()
        {
            try
            {
                SaveConfigWithRetry(() =>
                {
                    USerialization.SerializeXML(AppSettings, AppPathes.AppSettingPath);
                });

                // 备份应用设置
                try
                {
                    File.Copy(AppPathes.AppSettingPath, AppPathes.AppSettingPath_Backup, true);
                }
                catch (Exception ex)
                {
                    NLogger.Warn("备份应用设置失败: {Message}", ex.Message);
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("保存应用设置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 带重试机制的配置保存
        /// </summary>
        private void SaveConfigWithRetry(Action saveAction, int maxRetries = 3, int delayMs = 50)
        {
            Exception lastException = null;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    saveAction();
                    return; // 保存成功，直接返回
                }
                catch (IOException ex) when (ex.HResult == -2147024864) // 0x80070020: File is in use
                {
                    lastException = ex;
                    if (i < maxRetries - 1)
                    {
                        // 等待后重试
                        System.Threading.Thread.Sleep(delayMs);
                    }
                }
            }

            // 所有重试都失败了，抛出最后一个异常
            if (lastException != null)
            {
                throw lastException;
            }
        }

        /// <summary>
        /// 迁移旧的计划任务数据到全局配置
        /// 从进程树的所有节点收集任务，合并到全局配置中
        /// </summary>
        private GlobalScheduleConfig MigrateScheduleTasksToGlobal(ProcessItem rootNode)
        {
            var globalConfig = GlobalScheduleConfig.CreateDefault();

            // 保留根节点的启用状态
            globalConfig.ScheduleTasksEnabled = rootNode.ScheduleTasksEnabled;

            // 递归收集所有节点的任务
            CollectTasksFromNode(rootNode, globalConfig.ScheduleTasks, rootNode);

            return globalConfig;
        }

        /// <summary>
        /// 递归收集节点的计划任务
        /// </summary>
        private void CollectTasksFromNode(
            ProcessItem node,
            List<ScheduleTaskConfig> globalTasks,
            ProcessItem rootNode
        )
        {
            if (node.ScheduleTaskConfigs != null && node.ScheduleTaskConfigs.Count > 0)
            {
                foreach (var task in node.ScheduleTaskConfigs)
                {
                    var migratedTask = task.Clone();

                    // 设置目标节点信息（对于节点级操作）
                    if (migratedTask.IsNodeLevelAction())
                    {
                        migratedTask.TargetNodeId = node.Name; // 使用Name作为标识
                        migratedTask.TargetNodeName = node.Name;
                    }

                    globalTasks.Add(migratedTask);
                }
            }

            // 递归处理子节点
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    CollectTasksFromNode(child, globalTasks, rootNode);
                }
            }
        }

        #endregion
    }
}
