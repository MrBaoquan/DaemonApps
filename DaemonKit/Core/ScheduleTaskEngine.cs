using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Core
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

        private ReactiveCommand<Unit, Unit> _deleteCommand;

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> DeleteCommand
        {
            get => _deleteCommand;
            set => _deleteCommand = value;
        }

        public ScheduleTaskConfig()
        {
            DeleteCommand = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
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

    /// <summary>
    /// 任务调度引擎
    /// 负责检查、触发和执行所有计划任务
    /// </summary>
    public class ScheduleTaskEngine
    {
        private static readonly object _lock = new object();
        private readonly ProcessItem _rootNode;
        private readonly GlobalScheduleConfig _globalConfig;
        private readonly Dictionary<string, DateTime> _lastExecuteTime =
            new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _oncePerDayStartTime =
            new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> _executionCount = new Dictionary<string, int>();
        private readonly DateTime _appStartTime = Process.GetCurrentProcess().StartTime;
        private readonly string _firstStartMarkerPath = Path.Combine(
            Path.GetTempPath(),
            "DaemonKit",
            "schedule_first_start.marker"
        );
        private readonly bool _isFirstStartOfDay;
        private volatile bool _isDisposed = false;

        /// <summary>省电模式视图模型引用（可选）</summary>
        public Func<object>? PowerSavingViewModelProvider { get; set; }

        /// <summary>任务执行时的回调事件</summary>
        public event EventHandler<ScheduleTaskContext> TaskExecuting;
        public event EventHandler<ScheduleTaskContext> TaskExecuted;
        public Func<ScheduleTaskAction, Task<bool>>? ConfirmHandler { get; set; }

        public ScheduleTaskEngine(ProcessItem rootNode, GlobalScheduleConfig globalConfig)
        {
            _rootNode = rootNode ?? throw new ArgumentNullException(nameof(rootNode));
            _globalConfig = globalConfig ?? throw new ArgumentNullException(nameof(globalConfig));

            _isFirstStartOfDay = InitializeFirstStartMarker();

            // 初始化每日首次启动标记
            RefreshOncePerDayMarkers();
        }

        /// <summary>
        /// 检查并执行所有待执行的任务
        /// 该方法由主窗口每秒调用一次
        /// </summary>
        public async Task CheckAndExecutePendingTasks()
        {
            if (_isDisposed)
                return;

            DateTime now;
            List<(ProcessItem processNode, ScheduleTaskConfig taskConfig)> pendingTasks;

            lock (_lock)
            {
                now = DateTime.Now;
                pendingTasks = CollectPendingTasks(now);
            }

            foreach (var (processNode, taskConfig) in pendingTasks)
            {
                var context = new ScheduleTaskContext
                {
                    TargetProcess = processNode,
                    TaskConfig = taskConfig,
                    ExecuteTime = now
                };

                try
                {
                    TaskExecuting?.Invoke(this, context);
                    await ExecuteTask(context);
                    context.IsSuccess = true;
                    NLogger.Info($"任务执行成功: [{taskConfig.Name}] - {taskConfig.Action}");
                }
                catch (Exception ex)
                {
                    context.IsSuccess = false;
                    context.ErrorMessage = ex.Message;
                    NLogger.Error($"任务执行失败: [{taskConfig.Name}] - {ex.Message}");
                }
                finally
                {
                    lock (_lock)
                    {
                        MarkTaskExecuted(processNode, taskConfig, now);
                    }

                    TaskExecuted?.Invoke(this, context);
                }
            }
        }

        /// <summary>
        /// 收集所有待执行的任务（从全局配置）
        /// </summary>
        private List<(ProcessItem processNode, ScheduleTaskConfig taskConfig)> CollectPendingTasks(
            DateTime now
        )
        {
            var pendingTasks = new List<(ProcessItem, ScheduleTaskConfig)>();

            // 从全局配置获取所有任务
            foreach (var taskConfig in _globalConfig.ScheduleTasks)
            {
                if (!taskConfig.Enabled)
                    continue;

                // 对于节点级操作，找到目标节点
                ProcessItem targetNode = null;
                if (taskConfig.IsNodeLevelAction())
                {
                    targetNode =
                        FindNodeById(taskConfig.TargetNodeId)
                        ?? FindNodeByName(taskConfig.TargetNodeName);
                    if (targetNode == null)
                    {
                        NLogger.Warn(
                            $"任务 '{taskConfig.Name}' 的目标节点未找到 (Id: '{taskConfig.TargetNodeId}', Name: '{taskConfig.TargetNodeName}')"
                        );
                        continue;
                    }
                }
                else
                {
                    // 全局操作使用根节点
                    targetNode = _rootNode;
                }

                if (ShouldExecuteTask(targetNode, taskConfig, now))
                {
                    pendingTasks.Add((targetNode, taskConfig));
                }
            }

            return pendingTasks;
        }

        /// <summary>
        /// 根据名称查找节点
        /// </summary>
        private ProcessItem FindNodeByName(string nodeName)
        {
            if (string.IsNullOrEmpty(nodeName))
                return null;

            var allNodes = _rootNode.AllChildren();
            return allNodes.FirstOrDefault(n => n.Name == nodeName);
        }

        private ProcessItem FindNodeById(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
                return null;

            var allNodes = _rootNode.AllChildren();
            return allNodes.FirstOrDefault(
                n =>
                    (
                        !string.IsNullOrEmpty(n.NodeId)
                        && n.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)
                    ) || n.NodePath.Equals(nodeId, StringComparison.OrdinalIgnoreCase)
            );
        }

        /// <summary>
        /// 判断任务是否应该执行
        /// </summary>
        private bool ShouldExecuteTask(
            ProcessItem processNode,
            ScheduleTaskConfig taskConfig,
            DateTime now
        )
        {
            // 检查全局计划任务启用标志
            if (!_globalConfig.ScheduleTasksEnabled)
                return false;

            // 检查单个任务启用状态
            if (!taskConfig.Enabled)
                return false;

            string taskKey = GetTaskKey(processNode, taskConfig);

            // 执行次数上限
            if (
                taskConfig.MaxExecuteCount > 0
                && _executionCount.TryGetValue(taskKey, out var executed)
                && executed >= taskConfig.MaxExecuteCount
            )
            {
                return false;
            }

            switch (taskConfig.Trigger)
            {
                case ScheduleTriggerType.Daily:
                    return ShouldExecuteDaily(taskKey, taskConfig, now);

                case ScheduleTriggerType.OncePerDayAfterStart:
                    return ShouldExecuteOncePerDayAfterStart(taskKey, taskConfig, now);

                case ScheduleTriggerType.EveryStartupAfterDelay:
                    return ShouldExecuteEveryStartupAfterDelay(taskKey, taskConfig, now);

                case ScheduleTriggerType.IntervalAfterStartup:
                    return ShouldExecuteIntervalAfterStartup(taskKey, taskConfig, now);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 每天指定时间执行
        /// </summary>
        private bool ShouldExecuteDaily(string taskKey, ScheduleTaskConfig taskConfig, DateTime now)
        {
            if (!DateTime.TryParse(taskConfig.DailyTime, out var dailyTime))
                return false;

            var scheduleTime = new DateTime(
                now.Year,
                now.Month,
                now.Day,
                dailyTime.Hour,
                dailyTime.Minute,
                dailyTime.Second
            );

            // 如果应用启动时间已经晚于当天计划时间，则视为当日已执行过，避免启动时立即补跑
            if (!_lastExecuteTime.ContainsKey(taskKey) && _appStartTime > scheduleTime)
            {
                _lastExecuteTime[taskKey] = scheduleTime;
                NLogger.Info($"任务 '{taskConfig.Name}' 当天计划时间已过（{dailyTime:HH:mm:ss}），启动时不补跑");
                return false;
            }

            // 检查今天是否已执行过
            if (_lastExecuteTime.TryGetValue(taskKey, out var lastTime))
            {
                if (lastTime.Date == now.Date)
                {
                    return false; // 今天已执行过
                }
            }

            return now >= scheduleTime;
        }

        /// <summary>
        /// 每天首次启动后延迟X秒执行
        /// </summary>
        private bool ShouldExecuteOncePerDayAfterStart(
            string taskKey,
            ScheduleTaskConfig taskConfig,
            DateTime now
        )
        {
            if (!_isFirstStartOfDay)
                return false;

            // 初始化启动时间
            if (!_oncePerDayStartTime.TryGetValue(taskKey, out var startTime))
            {
                _oncePerDayStartTime[taskKey] = _appStartTime;
                return false;
            }

            // 检查是否是新的一天
            if (startTime.Date != _appStartTime.Date)
            {
                _oncePerDayStartTime[taskKey] = _appStartTime;
                return false;
            }

            // 检查是否已执行过
            if (_lastExecuteTime.TryGetValue(taskKey, out var lastTime))
            {
                if (lastTime.Date == now.Date)
                {
                    return false;
                }
            }

            // 判断是否达到延迟时间
            return (now - _appStartTime).TotalSeconds >= taskConfig.DelaySeconds;
        }

        /// <summary>
        /// 每次启动后延迟X秒执行（不限制频率）
        /// </summary>
        private bool ShouldExecuteEveryStartupAfterDelay(
            string taskKey,
            ScheduleTaskConfig taskConfig,
            DateTime now
        )
        {
            if (!_lastExecuteTime.TryGetValue(taskKey, out var lastTime))
            {
                // 第一次检查，记录应用启动时间
                return (now - _appStartTime).TotalSeconds >= taskConfig.DelaySeconds;
            }

            // 如果应用重启了，重新计时
            if (lastTime < _appStartTime)
            {
                return (now - _appStartTime).TotalSeconds >= taskConfig.DelaySeconds;
            }

            return false;
        }

        /// <summary>
        /// 启动后每隔X秒循环执行（周期性执行）
        /// </summary>
        private bool ShouldExecuteIntervalAfterStartup(
            string taskKey,
            ScheduleTaskConfig taskConfig,
            DateTime now
        )
        {
            if (!_lastExecuteTime.TryGetValue(taskKey, out var lastTime))
            {
                // 第一次执行
                lastTime = _appStartTime;
                _lastExecuteTime[taskKey] = lastTime;
            }

            // 如果应用重启了，重新计时
            if (lastTime < _appStartTime)
            {
                lastTime = _appStartTime;
            }

            return (now - lastTime).TotalSeconds >= taskConfig.DelaySeconds;
        }

        /// <summary>
        /// 执行任务
        /// </summary>
        private async Task ExecuteTask(ScheduleTaskContext context)
        {
            var taskConfig = context.TaskConfig;
            var processNode = context.TargetProcess;

            switch (taskConfig.Action)
            {
                case ScheduleTaskAction.StartProcess:
                    processNode.RunNode();
                    context.Result = $"已启动进程: {processNode.Name}";
                    break;

                case ScheduleTaskAction.StopProcess:
                    processNode.KillNode();
                    context.Result = $"已停止进程: {processNode.Name}";
                    break;

                case ScheduleTaskAction.RestartProcess:
                    processNode.KillNode();
                    Thread.Sleep(500);
                    processNode.RunNode();
                    context.Result = $"已重启进程: {processNode.Name}";
                    break;

                case ScheduleTaskAction.ShutdownSystem:
                    if (ConfirmHandler != null)
                    {
                        var confirmed = await ConfirmHandler.Invoke(taskConfig.Action);
                        if (!confirmed)
                        {
                            context.Result = "用户取消系统关机";
                            return;
                        }
                    }

                    WinAPI.OpenProcess("shutdown.exe", "/s /t 0");
                    context.Result = "已执行系统关闭命令";
                    break;

                case ScheduleTaskAction.RestartSystem:
                    if (ConfirmHandler != null)
                    {
                        var confirmed = await ConfirmHandler.Invoke(taskConfig.Action);
                        if (!confirmed)
                        {
                            context.Result = "用户取消系统重启";
                            return;
                        }
                    }

                    WinAPI.OpenProcess("shutdown.exe", "/r /t 0");
                    context.Result = "已执行系统重启命令";
                    break;

                case ScheduleTaskAction.TakeScreenshot:
                    TakeScreenshotTask();
                    context.Result = "已执行全屏截图";
                    break;

                case ScheduleTaskAction.ClickMouse:
                    PerformMouseClick(taskConfig.ClickX, taskConfig.ClickY);
                    context.Result = $"已执行鼠标点击: ({taskConfig.ClickX}, {taskConfig.ClickY})";
                    break;

                case ScheduleTaskAction.EnterPowerSaving:
                    await ExecuteEnterPowerSavingMode();
                    context.Result = "已开启节能模式";
                    break;

                case ScheduleTaskAction.ExitPowerSaving:
                    await ExecuteExitPowerSavingMode();
                    context.Result = "已退出节能模式";
                    break;

                default:
                    throw new NotSupportedException($"不支持的任务类型: {taskConfig.Action}");
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(
            uint dwFlags,
            uint dx,
            uint dy,
            uint dwData,
            UIntPtr dwExtraInfo
        );

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        private void PerformMouseClick(int x, int y)
        {
            try
            {
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
                mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                NLogger.Error($"执行鼠标点击失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 开启节能模式
        /// </summary>
        private async Task ExecuteEnterPowerSavingMode()
        {
            try
            {
                var viewModel = PowerSavingViewModelProvider?.Invoke();
                if (viewModel == null)
                {
                    NLogger.Warn("无法开启节能模式: PowerSavingViewModel 未初始化");
                    return;
                }

                // 使用反射调用 ApplyPowerSavingCommand
                var commandProperty = viewModel.GetType().GetProperty("ApplyPowerSavingCommand");
                if (commandProperty != null)
                {
                    var command = commandProperty.GetValue(viewModel) as ReactiveUI.ReactiveCommand<Unit, Unit>;
                    if (command != null && command.CanExecute.FirstAsync().Wait())
                    {
                        await command.Execute();
                        NLogger.Info("节能模式已开启");
                    }
                    else
                    {
                        NLogger.Warn("无法执行开启节能模式命令");
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"开启节能模式失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 退出节能模式
        /// </summary>
        private async Task ExecuteExitPowerSavingMode()
        {
            try
            {
                var viewModel = PowerSavingViewModelProvider?.Invoke();
                if (viewModel == null)
                {
                    NLogger.Warn("无法退出节能模式: PowerSavingViewModel 未初始化");
                    return;
                }

                // 使用反射调用 RestoreNormalCommand
                var commandProperty = viewModel.GetType().GetProperty("RestoreNormalCommand");
                if (commandProperty != null)
                {
                    var command = commandProperty.GetValue(viewModel) as ReactiveUI.ReactiveCommand<Unit, Unit>;
                    if (command != null && command.CanExecute.FirstAsync().Wait())
                    {
                        await command.Execute();
                        NLogger.Info("节能模式已退出");
                    }
                    else
                    {
                        NLogger.Warn("无法执行退出节能模式命令");
                    }
                }
            }
            catch (Exception ex)
            {
                NLogger.Error($"退出节能模式失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 全屏截图任务
        /// </summary>
        private void TakeScreenshotTask()
        {
            var screenshotDir = Path.Combine(AppPathes.AppRoot, "Screenshots");
            if (!Directory.Exists(screenshotDir))
                Directory.CreateDirectory(screenshotDir);

            var fileName = $"screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            var filePath = Path.Combine(screenshotDir, fileName);

            // 调用截图功能（与PickerOverlay集成）
            Task.Run(() =>
            {
                try
                {
                    var screenshot = new System.Drawing.Bitmap(
                        System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width,
                        System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height
                    );

                    var graphics = System.Drawing.Graphics.FromImage(screenshot);
                    graphics.CopyFromScreen(
                        System.Windows.Forms.Screen.PrimaryScreen.Bounds.Location,
                        System.Drawing.Point.Empty,
                        System.Windows.Forms.Screen.PrimaryScreen.Bounds.Size
                    );

                    screenshot.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                    screenshot.Dispose();
                    graphics.Dispose();

                    NLogger.Info($"截图已保存: {filePath}");
                }
                catch (Exception ex)
                {
                    NLogger.Error($"截图保存失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 标记任务已执行
        /// </summary>
        private void MarkTaskExecuted(
            ProcessItem processNode,
            ScheduleTaskConfig taskConfig,
            DateTime now
        )
        {
            string taskKey = GetTaskKey(processNode, taskConfig);
            _lastExecuteTime[taskKey] = now;

            if (taskConfig.MaxExecuteCount > 0)
            {
                if (_executionCount.TryGetValue(taskKey, out var count))
                {
                    _executionCount[taskKey] = count + 1;
                }
                else
                {
                    _executionCount[taskKey] = 1;
                }
            }
        }

        /// <summary>
        /// 刷新每日首次启动标记
        /// </summary>
        private void RefreshOncePerDayMarkers()
        {
            var now = DateTime.Now;
            var expiredKeys = _oncePerDayStartTime
                .Where(kv => kv.Value.Date < now.Date)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _oncePerDayStartTime.Remove(key);
            }
        }

        private bool InitializeFirstStartMarker()
        {
            try
            {
                var markerDir = Path.GetDirectoryName(_firstStartMarkerPath);
                if (!string.IsNullOrEmpty(markerDir))
                {
                    Directory.CreateDirectory(markerDir);
                }

                var today = DateTime.Now.Date;

                if (File.Exists(_firstStartMarkerPath))
                {
                    var content = File.ReadAllText(_firstStartMarkerPath).Trim();
                    if (DateTime.TryParse(content, out var recorded) && recorded.Date == today)
                    {
                        return false;
                    }
                }

                File.WriteAllText(_firstStartMarkerPath, today.ToString("yyyy-MM-dd"));
                return true;
            }
            catch (Exception ex)
            {
                NLogger.Warn($"首次启动标记初始化失败: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 获取任务唯一标识
        /// </summary>
        private string GetTaskKey(ProcessItem processNode, ScheduleTaskConfig taskConfig)
        {
            return $"{processNode.NodePath}#{taskConfig.Name}";
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            _isDisposed = true;
            lock (_lock)
            {
                _lastExecuteTime.Clear();
                _oncePerDayStartTime.Clear();
            }
        }
    }
}
