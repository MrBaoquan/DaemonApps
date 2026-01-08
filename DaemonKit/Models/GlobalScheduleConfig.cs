using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using ReactiveUI;

namespace DaemonKit.Models
{
    /// <summary>
    /// 全局计划任务配置
    /// 统一管理所有计划任务，包括全局操作和节点操作
    /// </summary>
    [XmlRoot("GlobalScheduleConfig")]
    public class GlobalScheduleConfig : ReactiveObject
    {
        /// <summary>全局计划任务启用标志</summary>
        private bool _scheduleTasksEnabled = true;

        [XmlAttribute]
        public bool ScheduleTasksEnabled
        {
            get => _scheduleTasksEnabled;
            set => this.RaiseAndSetIfChanged(ref _scheduleTasksEnabled, value);
        }

        /// <summary>全局任务配置列表</summary>
        [XmlElement("ScheduleTaskConfig")]
        public List<ScheduleTaskConfig> ScheduleTasks { get; set; } =
            new List<ScheduleTaskConfig>();

        /// <summary>
        /// 创建默认的全局配置实例
        /// </summary>
        public static GlobalScheduleConfig CreateDefault()
        {
            return new GlobalScheduleConfig
            {
                ScheduleTasksEnabled = true,
                ScheduleTasks = new List<ScheduleTaskConfig>()
            };
        }

        /// <summary>
        /// 验证配置的有效性
        /// </summary>
        public bool Validate(out string errorMessage)
        {
            errorMessage = string.Empty;

            if (ScheduleTasks == null)
            {
                errorMessage = "任务列表不能为空";
                return false;
            }

            foreach (var task in ScheduleTasks)
            {
                // 检查节点级任务是否有目标节点
                if (IsNodeLevelAction(task.Action) && string.IsNullOrEmpty(task.TargetNodeId))
                {
                    errorMessage = $"任务 '{task.Name}' 需要指定目标节点";
                    return false;
                }

                if (task.MaxExecuteCount < 0)
                {
                    errorMessage = $"任务 '{task.Name}' 的最大执行次数必须大于等于 0";
                    return false;
                }

                // 检查每日任务的时间格式
                if (task.Trigger == ScheduleTriggerType.Daily)
                {
                    if (!TimeSpan.TryParse(task.DailyTime, out _))
                    {
                        errorMessage = $"任务 '{task.Name}' 的时间格式无效: {task.DailyTime}";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 判断操作是否为节点级操作（需要指定目标节点）
        /// </summary>
        private bool IsNodeLevelAction(ScheduleTaskAction action)
        {
            return action == ScheduleTaskAction.StartProcess
                || action == ScheduleTaskAction.StopProcess
                || action == ScheduleTaskAction.RestartProcess;
        }
    }
}
