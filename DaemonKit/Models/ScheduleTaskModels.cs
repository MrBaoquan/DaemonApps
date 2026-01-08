using System;
using System.Xml.Serialization;
using ReactiveUI;

namespace DaemonKit.Models
{
    /// <summary>
    /// 任务触发器类型
    /// </summary>
    public enum ScheduleTriggerType
    {
        /// <summary>每天指定时间</summary>
        Daily,

        /// <summary>每天首次启动后延迟X秒</summary>
        OncePerDayAfterStart,

        /// <summary>每次启动后延迟X秒</summary>
        EveryStartupAfterDelay,

        /// <summary>启动后每隔X秒循环执行一次</summary>
        IntervalAfterStartup
    }

    /// <summary>
    /// 任务操作类型
    /// </summary>
    public enum ScheduleTaskAction
    {
        /// <summary>启动进程</summary>
        StartProcess,

        /// <summary>停止进程</summary>
        StopProcess,

        /// <summary>重启进程</summary>
        RestartProcess,

        /// <summary>关闭电脑</summary>
        ShutdownSystem,

        /// <summary>重启电脑</summary>
        RestartSystem,

        /// <summary>全屏截图</summary>
        TakeScreenshot,

        /// <summary>鼠标点击</summary>
        ClickMouse,

        /// <summary>开启节能模式</summary>
        EnterPowerSaving,

        /// <summary>退出节能模式</summary>
        ExitPowerSaving
    }

    /// <summary>
    /// 任务配置
    /// </summary>
    public class ScheduleTaskConfig : ReactiveObject
    {
        /// <summary>任务名称</summary>
        private string _name = string.Empty;

        [XmlAttribute]
        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        /// <summary>任务操作类型</summary>
        private ScheduleTaskAction _action = ScheduleTaskAction.StartProcess;

        [XmlAttribute]
        public ScheduleTaskAction Action
        {
            get => _action;
            set => this.RaiseAndSetIfChanged(ref _action, value);
        }

        /// <summary>触发器类型</summary>
        private ScheduleTriggerType _trigger = ScheduleTriggerType.Daily;

        [XmlAttribute]
        public ScheduleTriggerType Trigger
        {
            get => _trigger;
            set => this.RaiseAndSetIfChanged(ref _trigger, value);
        }

        /// <summary>每天执行的时间 (HH:mm:ss格式)</summary>
        private string _dailyTime = "00:00:00";

        [XmlAttribute]
        public string DailyTime
        {
            get => _dailyTime;
            set => this.RaiseAndSetIfChanged(ref _dailyTime, value);
        }

        /// <summary>延迟或间隔时间(秒)</summary>
        private int _delaySeconds = 60;

        [XmlAttribute]
        public int DelaySeconds
        {
            get => _delaySeconds;
            set => this.RaiseAndSetIfChanged(ref _delaySeconds, value);
        }

        /// <summary>是否启用</summary>
        private bool _enabled = true;

        [XmlAttribute]
        public bool Enabled
        {
            get => _enabled;
            set => this.RaiseAndSetIfChanged(ref _enabled, value);
        }

        /// <summary>备注信息</summary>
        private string _description = string.Empty;

        [XmlAttribute]
        public string Description
        {
            get => _description;
            set => this.RaiseAndSetIfChanged(ref _description, value);
        }

        /// <summary>目标节点ID（仅节点级操作需要）</summary>
        private string _targetNodeId = string.Empty;

        [XmlAttribute]
        public string TargetNodeId
        {
            get => _targetNodeId;
            set => this.RaiseAndSetIfChanged(ref _targetNodeId, value);
        }

        /// <summary>目标节点名称（用于显示）</summary>
        private string _targetNodeName = string.Empty;

        [XmlAttribute]
        public string TargetNodeName
        {
            get => _targetNodeName;
            set => this.RaiseAndSetIfChanged(ref _targetNodeName, value);
        }

        /// <summary>鼠标点击 X 坐标</summary>
        private int _clickX = 0;

        [XmlAttribute]
        public int ClickX
        {
            get => _clickX;
            set => this.RaiseAndSetIfChanged(ref _clickX, value);
        }

        /// <summary>鼠标点击 Y 坐标</summary>
        private int _clickY = 0;

        [XmlAttribute]
        public int ClickY
        {
            get => _clickY;
            set => this.RaiseAndSetIfChanged(ref _clickY, value);
        }

        /// <summary>最大执行次数(0 表示不限制)</summary>
        private int _maxExecuteCount = 0;

        [XmlAttribute]
        public int MaxExecuteCount
        {
            get => _maxExecuteCount;
            set => this.RaiseAndSetIfChanged(ref _maxExecuteCount, value);
        }

        private ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> _deleteCommand;

        [XmlIgnore]
        public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> DeleteCommand
        {
            get => _deleteCommand;
            set => _deleteCommand = value;
        }

        public ScheduleTaskConfig()
        {
            DeleteCommand = ReactiveCommand.Create(
                () => { },
                outputScheduler: ReactiveUI.RxApp.MainThreadScheduler
            );
        }

        public ScheduleTaskConfig Clone()
        {
            return new ScheduleTaskConfig
            {
                Name = this.Name,
                Action = this.Action,
                Trigger = this.Trigger,
                DailyTime = this.DailyTime,
                DelaySeconds = this.DelaySeconds,
                Enabled = this.Enabled,
                Description = this.Description,
                TargetNodeId = this.TargetNodeId,
                TargetNodeName = this.TargetNodeName,
                ClickX = this.ClickX,
                ClickY = this.ClickY,
                MaxExecuteCount = this.MaxExecuteCount
            };
        }

        /// <summary>
        /// 判断该任务是否为节点级操作（需要目标节点）
        /// </summary>
        public bool IsNodeLevelAction()
        {
            return Action == ScheduleTaskAction.StartProcess
                || Action == ScheduleTaskAction.StopProcess
                || Action == ScheduleTaskAction.RestartProcess;
        }

        /// <summary>
        /// 判断该任务是否为全局操作（不需要目标节点）
        /// </summary>
        public bool IsGlobalAction()
        {
            return Action == ScheduleTaskAction.ShutdownSystem
                || Action == ScheduleTaskAction.RestartSystem
                || Action == ScheduleTaskAction.TakeScreenshot
                || Action == ScheduleTaskAction.ClickMouse
                || Action == ScheduleTaskAction.EnterPowerSaving
                || Action == ScheduleTaskAction.ExitPowerSaving;
        }
    }

    /// <summary>
    /// 任务执行上下文
    /// </summary>
    public class ScheduleTaskContext
    {
        /// <summary>目标进程结点</summary>
        public ProcessItem TargetProcess { get; set; }

        /// <summary>任务配置</summary>
        public ScheduleTaskConfig TaskConfig { get; set; }

        /// <summary>执行时间</summary>
        public DateTime ExecuteTime { get; set; } = DateTime.Now;

        /// <summary>执行结果</summary>
        public string Result { get; set; } = string.Empty;

        /// <summary>执行是否成功</summary>
        public bool IsSuccess { get; set; }

        /// <summary>错误信息</summary>
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
