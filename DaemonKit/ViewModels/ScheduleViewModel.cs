using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        Backup
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
        private DateTime _time;

        [XmlAttribute]
        public DateTime Time
        {
            get => _time;
            set => this.RaiseAndSetIfChanged(ref _time, value);
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
            this.WhenAnyValue(x => x.Time)
                .Select(x => x.ToString("HH:mm:ss"))
                .ToProperty(this, x => x.TimeString, out _timeString);
            this.DeleteCommand = ReactiveCommand.Create(() => { });
            CalculateStatus();
        }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> DeleteCommand { get; protected set; }
    }

    public class ScheduleViewModel : ReactiveObject
    {
        private ProcessItem editingProcessItem { get; set; } = null!;
        public ProcessItem EditingProcessItem => editingProcessItem;

        public string SetEditingProcessItem(ProcessItem item)
        {
            editingProcessItem = item;

            TaskTypes = editingProcessItem.IsSuperRoot
                ? new ObservableCollection<string>(rootTaskTypes)
                : new ObservableCollection<string>(childTaskTypes);
            ScheduleItems = new ObservableCollection<ScheduleItem>(
                editingProcessItem.ScheduleItems
            );

            if (editingProcessItem.IsSuperRoot)
            {
                return $"{editingProcessItem.Name} - 计划任务";
            }
            return $"{editingProcessItem.Name} ({editingProcessItem.Path}) - 计划任务";
        }

        public ObservableCollection<string> TaskTypes { get; set; } =
            new ObservableCollection<string> { };

        private List<string> rootTaskTypes = new List<string>
        {
            "启动 (进程)",
            "停止 (进程)",
            "关闭 (电脑)",
            "重启 (电脑)"
        };
        private List<string> childTaskTypes = new List<string> { "启动 (进程)", "停止 (进程)" };

        public List<string> TaskTriggerTypes { get; } = new List<string> { "每天" };

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

        // 按时间排序命令
        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> SortByTimeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> SaveCommand { get; protected set; }

        public ScheduleViewModel()
        {
            AddScheduleCommand = ReactiveCommand.Create(() =>
            {
                var _newItem = new ScheduleItem();
                ScheduleItems.Add(_newItem);
                _newItem.DeleteCommand.Subscribe(_ =>
                {
                    ScheduleItems.Remove(_newItem);
                });
            });

            SaveCommand = ReactiveCommand.Create(() => { });

            SortByTimeCommand = ReactiveCommand.Create(() =>
            {
                ScheduleItems = new ObservableCollection<ScheduleItem>(
                    ScheduleItems.OrderBy(x => x.Time)
                );
            });
        }
    }
}
