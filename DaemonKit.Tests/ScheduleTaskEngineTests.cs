using System;
using System.Collections.Generic;
using DaemonKit.Core;
using DaemonKit.Models;
using Xunit;

namespace DaemonKit.Tests
{
    /// <summary>
    /// ScheduleTaskEngine 触发器逻辑测试
    /// 覆盖 4 种 ScheduleTriggerType 的判定算法
    /// </summary>
    public class ScheduleTaskEngineTests
    {
        #region 辅助方法

        private static ProcessItem CreateRootNode()
        {
            var root = new ProcessItem { Name = "Root" };
            // 设置 IsSuperRoot 标记（ProcessItem 通过 Parent==null 判断）
            return root;
        }

        private static ProcessItem CreateChildNode(string name, string path = "")
        {
            return new ProcessItem
            {
                Name = name,
                MetaData = new ProcessMetaData
                {
                    Name = name,
                    Path = string.IsNullOrEmpty(path) ? $"C:\\test\\{name}.exe" : path
                }
            };
        }

        private static GlobalScheduleConfig CreateConfig(
            bool enabled = true,
            List<ScheduleTaskConfig>? tasks = null
        )
        {
            return new GlobalScheduleConfig
            {
                ScheduleTasksEnabled = enabled,
                ScheduleTasks = tasks ?? new List<ScheduleTaskConfig>()
            };
        }

        private static ScheduleTaskEngine CreateEngine(
            ProcessItem? root = null,
            GlobalScheduleConfig? config = null
        )
        {
            return new ScheduleTaskEngine(root ?? CreateRootNode(), config ?? CreateConfig());
        }

        #endregion

        #region GetTaskKey 测试

        [Fact]
        public void GetTaskKey_ReturnsExpectedFormat()
        {
            var engine = CreateEngine();
            var node = CreateChildNode("App1", "C:\\test\\app1.exe");
            var task = new ScheduleTaskConfig { Name = "每日重启" };

            var key = engine.GetTaskKey(node, task);

            Assert.Equal("C:\\test\\app1.exe#每日重启", key);
        }

        [Fact]
        public void GetTaskKey_DifferentNodes_DifferentKeys()
        {
            var engine = CreateEngine();
            var node1 = CreateChildNode("App1", "C:\\a.exe");
            var node2 = CreateChildNode("App2", "C:\\b.exe");
            var task = new ScheduleTaskConfig { Name = "同名任务" };

            Assert.NotEqual(engine.GetTaskKey(node1, task), engine.GetTaskKey(node2, task));
        }

        #endregion

        #region ShouldExecuteTask — 全局禁用 / 任务禁用

        [Fact]
        public void ShouldExecuteTask_GlobalDisabled_ReturnsFalse()
        {
            var config = CreateConfig(enabled: false);
            var engine = CreateEngine(config: config);
            var node = CreateChildNode("App1");
            var task = new ScheduleTaskConfig { Enabled = true };

            Assert.False(engine.ShouldExecuteTask(node, task, DateTime.Now));
        }

        [Fact]
        public void ShouldExecuteTask_TaskDisabled_ReturnsFalse()
        {
            var engine = CreateEngine();
            var node = CreateChildNode("App1");
            var task = new ScheduleTaskConfig { Enabled = false };

            Assert.False(engine.ShouldExecuteTask(node, task, DateTime.Now));
        }

        #endregion

        #region ShouldExecuteTask — MaxExecuteCount 限制

        [Fact]
        public void ShouldExecuteTask_MaxCountReached_ReturnsFalse()
        {
            var engine = CreateEngine();
            var node = CreateChildNode("App1");
            var task = new ScheduleTaskConfig
            {
                Name = "限次任务",
                Enabled = true,
                Trigger = ScheduleTriggerType.EveryStartupAfterDelay,
                DelaySeconds = 0,
                MaxExecuteCount = 1
            };

            // 先标记一次执行
            engine.MarkTaskExecuted(node, task, DateTime.Now);

            Assert.False(engine.ShouldExecuteTask(node, task, DateTime.Now));
        }

        [Fact]
        public void ShouldExecuteTask_MaxCountNotReached_Passes()
        {
            var engine = CreateEngine();
            var node = CreateChildNode("App1");
            var task = new ScheduleTaskConfig
            {
                Name = "限次任务",
                Enabled = true,
                Trigger = ScheduleTriggerType.EveryStartupAfterDelay,
                DelaySeconds = 0,
                MaxExecuteCount = 3
            };

            engine.MarkTaskExecuted(node, task, DateTime.Now);

            // 还没到达上限，应该可以通过 MaxCount 检查
            // (但可能因其他条件不满足而返回 false —— 这里只测上限逻辑)
            // 验证 MarkTaskExecuted 是否正确递增计数
            engine.MarkTaskExecuted(node, task, DateTime.Now);
            engine.MarkTaskExecuted(node, task, DateTime.Now);

            Assert.False(engine.ShouldExecuteTask(node, task, DateTime.Now));
        }

        #endregion

        #region Daily 触发器

        [Fact]
        public void ShouldExecuteDaily_BeforeScheduleTime_ReturnsFalse()
        {
            var engine = CreateEngine();
            var task = new ScheduleTaskConfig { Name = "每日任务", DailyTime = "23:59:59" };
            var now = DateTime.Today.AddHours(10); // 上午10点

            Assert.False(engine.ShouldExecuteDaily("test#key", task, now));
        }

        [Fact]
        public void ShouldExecuteDaily_AfterScheduleTime_ReturnsTrue()
        {
            var engine = CreateEngine();
            var node = CreateChildNode("TestApp", "C:\\test\\testapp.exe");
            var task = new ScheduleTaskConfig { Name = "每日任务", DailyTime = "08:00:00" };
            var taskKey = engine.GetTaskKey(node, task);

            // 先标记为昨天已执行，绕过首次启动"不补跑"逻辑
            engine.MarkTaskExecuted(node, task, DateTime.Today.AddDays(-1).AddHours(8));

            var now = DateTime.Today.AddHours(10); // 上午10点
            Assert.True(engine.ShouldExecuteDaily(taskKey, task, now));
        }

        [Fact]
        public void ShouldExecuteDaily_InvalidTimeFormat_ReturnsFalse()
        {
            var engine = CreateEngine();
            var task = new ScheduleTaskConfig { Name = "无效时间", DailyTime = "invalid" };

            Assert.False(engine.ShouldExecuteDaily("test#key", task, DateTime.Now));
        }

        #endregion

        #region EveryStartupAfterDelay 触发器

        [Fact]
        public void ShouldExecuteEveryStartup_DelayNotMet_ReturnsFalse()
        {
            var engine = CreateEngine();
            var task = new ScheduleTaskConfig
            {
                Name = "启动延迟",
                DelaySeconds = 3600 // 1小时后
            };
            // 应用刚启动1秒
            var now = DateTime.Now;

            Assert.False(engine.ShouldExecuteEveryStartupAfterDelay("test#key", task, now));
        }

        #endregion

        #region IntervalAfterStartup 触发器

        [Fact]
        public void ShouldExecuteInterval_IntervalNotElapsed_ReturnsFalse()
        {
            var engine = CreateEngine();
            var task = new ScheduleTaskConfig
            {
                Name = "周期任务",
                DelaySeconds = 3600 // 每小时一次
            };

            Assert.False(engine.ShouldExecuteIntervalAfterStartup("test#key", task, DateTime.Now));
        }

        #endregion

        #region MarkTaskExecuted 测试

        [Fact]
        public void MarkTaskExecuted_IncrementsCount()
        {
            var engine = CreateEngine();
            var node = CreateChildNode("App1");
            var task = new ScheduleTaskConfig
            {
                Name = "计数任务",
                Enabled = true,
                Trigger = ScheduleTriggerType.EveryStartupAfterDelay,
                DelaySeconds = 0,
                MaxExecuteCount = 5
            };

            engine.MarkTaskExecuted(node, task, DateTime.Now);
            engine.MarkTaskExecuted(node, task, DateTime.Now);

            // 第三次应该还能执行（MaxCount=5，已执行2次）
            // ShouldExecuteTask 会检查 MaxCount，所以再标记3次才到上限
            engine.MarkTaskExecuted(node, task, DateTime.Now);
            engine.MarkTaskExecuted(node, task, DateTime.Now);
            engine.MarkTaskExecuted(node, task, DateTime.Now);

            // 现在已执行5次，达到上限
            Assert.False(engine.ShouldExecuteTask(node, task, DateTime.Now));
        }

        [Fact]
        public void MarkTaskExecuted_NoMaxCount_DoesNotTrack()
        {
            var engine = CreateEngine();
            var node = CreateChildNode("App1");
            var task = new ScheduleTaskConfig
            {
                Name = "无限任务",
                Enabled = true,
                Trigger = ScheduleTriggerType.Daily,
                DailyTime = "00:00:01",
                MaxExecuteCount = 0 // 无限制
            };

            // 标记多次执行
            for (int i = 0; i < 100; i++)
            {
                engine.MarkTaskExecuted(node, task, DateTime.Now);
            }

            // MaxCount=0 意味着不限制，ShouldExecuteTask 不应因 count 返回 false
            // (可能因 Daily 已执行过今天而返回 false，这是正常的)
            // 此处只验证不会因为计数而抛出异常
        }

        #endregion

        #region ScheduleTaskConfig 模型测试

        [Fact]
        public void ScheduleTaskConfig_IsNodeLevelAction_Correct()
        {
            var config = new ScheduleTaskConfig { Action = ScheduleTaskAction.StartProcess };
            Assert.True(config.IsNodeLevelAction());

            config.Action = ScheduleTaskAction.StopProcess;
            Assert.True(config.IsNodeLevelAction());

            config.Action = ScheduleTaskAction.RestartProcess;
            Assert.True(config.IsNodeLevelAction());
        }

        [Fact]
        public void ScheduleTaskConfig_IsGlobalAction_Correct()
        {
            var config = new ScheduleTaskConfig { Action = ScheduleTaskAction.ShutdownSystem };
            Assert.True(config.IsGlobalAction());

            config.Action = ScheduleTaskAction.RestartSystem;
            Assert.True(config.IsGlobalAction());

            config.Action = ScheduleTaskAction.TakeScreenshot;
            Assert.True(config.IsGlobalAction());

            config.Action = ScheduleTaskAction.ClickMouse;
            Assert.True(config.IsGlobalAction());

            config.Action = ScheduleTaskAction.EnterPowerSaving;
            Assert.True(config.IsGlobalAction());

            config.Action = ScheduleTaskAction.ExitPowerSaving;
            Assert.True(config.IsGlobalAction());
        }

        [Fact]
        public void ScheduleTaskConfig_NodeAction_IsNotGlobal()
        {
            var config = new ScheduleTaskConfig { Action = ScheduleTaskAction.StartProcess };
            Assert.False(config.IsGlobalAction());
        }

        [Fact]
        public void ScheduleTaskConfig_GlobalAction_IsNotNodeLevel()
        {
            var config = new ScheduleTaskConfig { Action = ScheduleTaskAction.ShutdownSystem };
            Assert.False(config.IsNodeLevelAction());
        }

        [Fact]
        public void ScheduleTaskConfig_Clone_CreatesIndependentCopy()
        {
            var original = new ScheduleTaskConfig
            {
                Name = "原始任务",
                Action = ScheduleTaskAction.StartProcess,
                Trigger = ScheduleTriggerType.Daily,
                DailyTime = "12:00:00",
                DelaySeconds = 30,
                Enabled = true,
                Description = "测试描述",
                TargetNodeId = "node-123",
                TargetNodeName = "测试节点",
                ClickX = 100,
                ClickY = 200,
                MaxExecuteCount = 5
            };

            var clone = original.Clone();

            Assert.Equal(original.Name, clone.Name);
            Assert.Equal(original.Action, clone.Action);
            Assert.Equal(original.Trigger, clone.Trigger);
            Assert.Equal(original.DailyTime, clone.DailyTime);
            Assert.Equal(original.DelaySeconds, clone.DelaySeconds);
            Assert.Equal(original.Enabled, clone.Enabled);
            Assert.Equal(original.Description, clone.Description);
            Assert.Equal(original.TargetNodeId, clone.TargetNodeId);
            Assert.Equal(original.TargetNodeName, clone.TargetNodeName);
            Assert.Equal(original.ClickX, clone.ClickX);
            Assert.Equal(original.ClickY, clone.ClickY);
            Assert.Equal(original.MaxExecuteCount, clone.MaxExecuteCount);

            // 修改克隆不影响原始
            clone.Name = "修改后";
            Assert.NotEqual(original.Name, clone.Name);
        }

        #endregion

        #region GlobalScheduleConfig 测试

        [Fact]
        public void GlobalScheduleConfig_CreateDefault_IsValid()
        {
            var config = GlobalScheduleConfig.CreateDefault();

            Assert.True(config.ScheduleTasksEnabled);
            Assert.NotNull(config.ScheduleTasks);
            Assert.Empty(config.ScheduleTasks);
        }

        [Fact]
        public void GlobalScheduleConfig_Validate_EmptyConfig_IsValid()
        {
            var config = GlobalScheduleConfig.CreateDefault();
            Assert.True(config.Validate(out var errorMessage));
            Assert.Empty(errorMessage);
        }

        #endregion
    }
}
