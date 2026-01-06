using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Xml.Serialization;
using DaemonKit.Core;
using DNHper;
using DynamicData.Binding;
using ReactiveUI;

namespace DaemonKit
{
    public enum ScheduleTaskType
    {
        Start,
        Stop,
        Shutdown,
        Restart,
        Backup,
        RestartApp // 重启程序
    }

    public class ScheduleItem : ReactiveObject
    {
        // 任务类型
        private ScheduleTaskType _taskType = ScheduleTaskType.Start;

        [XmlAttribute]
        public ScheduleTaskType TaskType
        {
            get => _taskType;
            set => this.RaiseAndSetIfChanged(ref _taskType, value);
        }

        // 触发器
        private Core.TriggerType _trigger = Core.TriggerType.Daily;

        [XmlAttribute]
        public Core.TriggerType Trigger
        {
            get => _trigger;
            set => this.RaiseAndSetIfChanged(ref _trigger, value);
        }

        // 任务名称
        private string _name = string.Empty;

        [XmlAttribute]
        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        // 时间
        private DateTime _time = DateTime.Now;

        [XmlAttribute]
        public DateTime Time
        {
            get => _time;
            set => this.RaiseAndSetIfChanged(ref _time, value);
        }

        // 延迟秒数（用于程序启动后触发器）
        private int _delaySeconds = 60;

        [XmlAttribute]
        public int DelaySeconds
        {
            get => _delaySeconds;
            set => this.RaiseAndSetIfChanged(ref _delaySeconds, value);
        }

        // 时间字符串
        private ObservableAsPropertyHelper<string> _timeString;

        [XmlIgnore]
        public string TimeString => _timeString.Value;

        // 是否启用任务
        private bool _enabled = true;

        [XmlAttribute]
        public bool Enabled
        {
            get => _enabled;
            set => this.RaiseAndSetIfChanged(ref _enabled, value);
        }

        // 任务状态
        private int _status = 0; // 0: 过期, 1: 待执行, 2: 已执行

        [XmlIgnore]
        public int Status
        {
            get => _status;
            set => this.RaiseAndSetIfChanged(ref _status, value);
        }

        // 计算默认状态值
        public void CalculateStatus()
        {
            if (Trigger == Core.TriggerType.Daily)
            {
                Status = canDailyExecute() ? 0 : 1;
            }
            else if (
                Trigger == Core.TriggerType.OnAppStart || Trigger == Core.TriggerType.OnAppStartOnce
            )
            {
                Status = 1; // 程序启动后的任务默认为待执行
            }
            else
            {
                Status = 1;
            }
        }

        public bool CanExecute()
        {
            if (!Enabled || Status != 1)
            {
                return false;
            }

            if (Trigger == Core.TriggerType.Daily)
            {
                return canDailyExecute();
            }
            else if (
                Trigger == Core.TriggerType.OnAppStart || Trigger == Core.TriggerType.OnAppStartOnce
            )
            {
                return true; // 由外部控制执行时机
            }

            return false;
        }

        public void MarkAsExecuted()
        {
            Status = 2;
        }

        // 判断任务是否达到执行时间
        private bool canDailyExecute()
        {
            var now = DateTime.Now;
            var scheduleTime = new DateTime(
                now.Year,
                now.Month,
                now.Day,
                Time.Hour,
                Time.Minute,
                Time.Second
            );
            return now >= scheduleTime;
        }

        public ScheduleItem()
        {
            // 根据触发器类型显示不同的字符串
            this.WhenAnyValue(x => x.Trigger, x => x.Time, x => x.DelaySeconds)
                .Select(x =>
                {
                    if (
                        x.Item1 == Core.TriggerType.OnAppStart
                        || x.Item1 == Core.TriggerType.OnAppStartOnce
                    )
                    {
                        return $"{x.Item3} 秒";
                    }
                    return x.Item2.ToString("HH:mm:ss");
                })
                .ToProperty(this, x => x.TimeString, out _timeString);
            this.DeleteCommand = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            CalculateStatus();
        }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> DeleteCommand { get; protected set; }
    }

    /// <summary>
    /// 新版本的计划任务视图模型（支持更灵活的任务配置）
    /// </summary>
    public class ScheduleViewModel : ReactiveObject
    {
        private GlobalScheduleConfig globalScheduleConfig { get; set; } = null!;
        public GlobalScheduleConfig GlobalSchedule => globalScheduleConfig;

        private ProcessItem rootProcessNode { get; set; } = null!;
        public ProcessItem RootProcessNode => rootProcessNode;

        public void SetGlobalConfig(GlobalScheduleConfig config, ProcessItem rootNode)
        {
            globalScheduleConfig = config;
            rootProcessNode = rootNode;

            // 使用全局配置的任务列表
            ScheduleTaskConfigs = new ObservableCollection<ScheduleTaskConfig>(
                globalScheduleConfig.ScheduleTasks
            );

            this.RaisePropertyChanged(nameof(GlobalSchedule));
            this.RaisePropertyChanged(nameof(RootProcessNode));
        }

        /// <summary>
        /// 触发器类型选项
        /// </summary>
        public List<string> TriggerTypeOptions { get; } =
            new List<string> { "每天指定时间", "每天首次启动后延迟X秒", "每次启动后延迟X秒", "启动后每隔X秒循环执行" };

        /// <summary>
        /// 任务操作类型选项
        /// </summary>
        private ObservableCollection<string> _taskActionTypes = new ObservableCollection<string>();
        public ObservableCollection<string> TaskActionTypes
        {
            get => _taskActionTypes;
            set => this.RaiseAndSetIfChanged(ref _taskActionTypes, value);
        }

        /// <summary>
        /// 新的任务配置列表（推荐使用）
        /// </summary>
        private ObservableCollection<ScheduleTaskConfig> _scheduleTaskConfigs =
            new ObservableCollection<ScheduleTaskConfig>();

        public ObservableCollection<ScheduleTaskConfig> ScheduleTaskConfigs
        {
            get => _scheduleTaskConfigs;
            set
            {
                this.RaiseAndSetIfChanged(ref _scheduleTaskConfigs, value);
                _scheduleTaskConfigs
                    .ToList()
                    .ForEach(x =>
                    {
                        x.DeleteCommand?.Subscribe(_ =>
                        {
                            ScheduleTaskConfigs.Remove(x);
                        });
                    });
            }
        }

        /// <summary>
        /// 当前选中的任务配置
        /// </summary>
        private ScheduleTaskConfig? _selectedTaskConfig;
        public ScheduleTaskConfig? SelectedTaskConfig
        {
            get => _selectedTaskConfig;
            set => this.RaiseAndSetIfChanged(ref _selectedTaskConfig, value);
        }

        /// <summary>
        /// 旧的任务项（保留以保证兼容性）
        /// </summary>
        public ObservableCollection<string> TaskTypes { get; set; } =
            new ObservableCollection<string> { };

        private List<string> rootTaskTypes = new List<string>
        {
            "启动 (进程)",
            "停止 (进程)",
            "关闭 (电脑)",
            "重启 (电脑)",
            "重启 (程序)"
        };
        private List<string> childTaskTypes = new List<string> { "启动 (进程)", "停止 (进程)" };

        public List<string> TaskTriggerTypes { get; } =
            new List<string> { "每天", "程序启动后", "每天首次启动后" };

        private ObservableCollection<ScheduleItem> _scheduleItems =
            new ObservableCollection<ScheduleItem>();

        public ObservableCollection<ScheduleItem> ScheduleItems
        {
            get => _scheduleItems;
            set
            {
                this.RaiseAndSetIfChanged(ref _scheduleItems, value);
                _scheduleItems
                    .ToList()
                    .ForEach(x =>
                    {
                        x.DeleteCommand.Subscribe(_ =>
                        {
                            ScheduleItems.Remove(x);
                        });
                    });
            }
        }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> AddScheduleCommand { get; protected set; }

        /// <summary>
        /// 添加新的任务配置命令
        /// </summary>
        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> AddTaskConfigCommand { get; protected set; }

        // 按时间排序命令
        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> SortByTimeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> SaveCommand { get; protected set; }

        public ScheduleViewModel()
        {
            AddScheduleCommand = ReactiveCommand.Create(
                () =>
                {
                    var _newItem = new ScheduleItem();
                    ScheduleItems.Add(_newItem);
                    _newItem.DeleteCommand.Subscribe(_ =>
                    {
                        ScheduleItems.Remove(_newItem);
                    });
                },
                outputScheduler: RxApp.MainThreadScheduler
            );

            /// <summary>
            /// 添加新的任务配置 - 使用对话框
            /// </summary>
            AddTaskConfigCommand = ReactiveCommand.Create(
                () =>
                {
                    // 打开编辑对话框创建新任务
                    var dialog = new ScheduleTaskEditDialog(null, rootProcessNode);

                    // 需要获取主窗口来设置Owner
                    var mainWindow = System.Windows.Application.Current.Windows
                        .OfType<System.Windows.Window>()
                        .FirstOrDefault(w => w is Schedule);

                    if (mainWindow != null)
                    {
                        dialog.Owner = mainWindow;
                    }

                    if (dialog.ShowDialog() == true && dialog.Result != null)
                    {
                        var newConfig = dialog.Result;

                        // 订阅删除命令
                        newConfig.DeleteCommand?.Subscribe(_ =>
                        {
                            ScheduleTaskConfigs.Remove(newConfig);
                            // 删除后立即保存
                            SaveTaskConfigs();
                        });

                        ScheduleTaskConfigs.Add(newConfig);
                        // 添加后立即保存
                        SaveTaskConfigs();
                    }
                },
                outputScheduler: RxApp.MainThreadScheduler
            );

            SaveCommand = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );

            SortByTimeCommand = ReactiveCommand.Create(
                () =>
                {
                    ScheduleItems = new ObservableCollection<ScheduleItem>(
                        ScheduleItems.OrderBy(x => x.Time)
                    );
                },
                outputScheduler: RxApp.MainThreadScheduler
            );
        }

        /// <summary>
        /// 保存任务配置到全局配置
        /// </summary>
        public void SaveTaskConfigs()
        {
            if (globalScheduleConfig != null && ScheduleTaskConfigs != null)
            {
                globalScheduleConfig.ScheduleTasks = ScheduleTaskConfigs.ToList();

                try
                {
                    USerialization.SerializeXML(globalScheduleConfig, AppPathes.GlobalSchedulePath);

                    // 简单同步备份
                    if (!Directory.Exists(AppPathes.ConfigDir_BackUp))
                    {
                        Directory.CreateDirectory(AppPathes.ConfigDir_BackUp);
                    }
                    File.Copy(
                        AppPathes.GlobalSchedulePath,
                        AppPathes.GlobalSchedulePath_Backup,
                        true
                    );
                    NLogger.Info("计划任务配置已保存");
                }
                catch (Exception ex)
                {
                    NLogger.Error($"保存计划任务配置失败: {ex.Message}");
                }
            }
        }
    }
}
