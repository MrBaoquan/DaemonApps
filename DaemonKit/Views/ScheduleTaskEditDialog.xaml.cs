using System;
using System.Windows;
using System.Windows.Input;
using DaemonKit.Models;
using System.Collections.Generic;
using System.Linq;

namespace DaemonKit
{
    /// <summary>
    /// ScheduleTaskEditDialog.xaml 的交互逻辑
    /// </summary>
    public partial class ScheduleTaskEditDialog : Window
    {
        private readonly ScheduleTaskConfig? _originalConfig;
        private readonly bool _isEditMode;
        private ProcessItem? _rootProcessNode;

        /// <summary>
        /// 对话框结果
        /// </summary>
        public ScheduleTaskConfig? Result { get; private set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="config">要编辑的配置，null 表示新建模式</param>
        /// <param name="rootProcessNode">根进程节点，用于节点选择</param>
        public ScheduleTaskEditDialog(
            ScheduleTaskConfig? config = null,
            ProcessItem? rootProcessNode = null
        )
        {
            InitializeComponent();

            _rootProcessNode = rootProcessNode;

            _originalConfig = config;
            _isEditMode = config != null;

            // 设置窗口标题
            if (_isEditMode)
            {
                Title = "编辑计划任务";
            }
            else
            {
                Title = "新建计划任务";
            }

            // 初始化界面
            InitializeFields();
        }

        /// <summary>
        /// 初始化字段
        /// </summary>
        private void InitializeFields()
        {
            // 初始化节点列表
            PopulateNodeList();

            if (_isEditMode && _originalConfig != null)
            {
                // 编辑模式 - 填充现有数据
                TriggerComboBox.SelectedItem = _originalConfig.Trigger;
                ActionComboBox.SelectedItem = _originalConfig.Action;
                EnabledToggle.IsChecked = _originalConfig.Enabled;
                ClickXTextBox.Text = _originalConfig.ClickX.ToString();
                ClickYTextBox.Text = _originalConfig.ClickY.ToString();
                MaxExecuteCountTextBox.Text = _originalConfig.MaxExecuteCount.ToString();

                // 根据触发方式设置参数
                if (_originalConfig.Trigger == ScheduleTriggerType.Daily)
                {
                    if (TimeSpan.TryParse(_originalConfig.DailyTime, out TimeSpan time))
                    {
                        DailyTimePicker.SelectedTime = DateTime.Today.Add(time);
                    }
                }
                else
                {
                    DelaySecondsTextBox.Text = _originalConfig.DelaySeconds.ToString();
                }

                // 设置目标节点
                var targetNode =
                    FindNodeById(_rootProcessNode, _originalConfig.TargetNodeId)
                    ?? FindNodeByName(_rootProcessNode, _originalConfig.TargetNodeName);
                if (targetNode != null)
                {
                    foreach (var item in TargetNodeComboBox.Items)
                    {
                        if (
                            item is Models.ProcessItemWithLevel nodeWithLevel
                            && nodeWithLevel.Item == targetNode
                        )
                        {
                            TargetNodeComboBox.SelectedItem = nodeWithLevel;
                            break;
                        }
                    }
                }
            }
            else
            {
                // 新建模式 - 设置默认值
                TriggerComboBox.SelectedIndex = 0;
                ActionComboBox.SelectedIndex = 0;
                EnabledToggle.IsChecked = true;
                DelaySecondsTextBox.Text = "60";
                ClickXTextBox.Text = "0";
                ClickYTextBox.Text = "0";
                MaxExecuteCountTextBox.Text = "0";
            }

            // 触发界面更新
            UpdateParameterVisibility();
        }

        /// <summary>
        /// 触发方式改变时更新参数输入控件的可见性
        /// </summary>
        private void TriggerComboBox_SelectionChanged(
            object sender,
            System.Windows.Controls.SelectionChangedEventArgs e
        )
        {
            UpdateParameterVisibility();
        }

        /// <summary>
        /// 更新参数区域的可见性
        /// </summary>
        private void UpdateParameterVisibility()
        {
            UpdateNodeSelectorVisibility();
            EnsureDefaultTargetNode();

            if (TriggerComboBox.SelectedItem is ScheduleTriggerType trigger)
            {
                if (trigger == ScheduleTriggerType.Daily)
                {
                    DailyTimePicker.Visibility = Visibility.Visible;
                    DelaySecondsTextBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    DailyTimePicker.Visibility = Visibility.Collapsed;
                    DelaySecondsTextBox.Visibility = Visibility.Visible;
                }

                bool isLoopTrigger = trigger == ScheduleTriggerType.IntervalAfterStartup;
                MaxExecuteCountTextBox.Visibility = isLoopTrigger
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (!isLoopTrigger)
                {
                    MaxExecuteCountTextBox.Text = "0";
                }
            }

            if (ActionComboBox.SelectedItem is ScheduleTaskAction action)
            {
                ClickParamsPanel.Visibility =
                    action == ScheduleTaskAction.ClickMouse
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 关闭按钮点击
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 取消按钮点击
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 确定按钮点击
        /// </summary>
        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // 如果是节点级操作且未选择节点，默认选择根节点
            EnsureDefaultTargetNode();

            // 验证输入
            if (!ValidateInput())
            {
                return;
            }

            // 创建或更新配置
            var config =
                _isEditMode && _originalConfig != null ? _originalConfig : new ScheduleTaskConfig();

            config.Trigger = (ScheduleTriggerType)TriggerComboBox.SelectedItem;
            config.Action = (ScheduleTaskAction)ActionComboBox.SelectedItem;
            if (string.IsNullOrWhiteSpace(config.Name))
            {
                config.Name = $"任务{DateTime.Now:yyyyMMddHHmmss}";
            }
            if (!_isEditMode)
            {
                config.Description = string.Empty;
            }
            config.Enabled = EnabledToggle.IsChecked ?? true;

            // 设置目标节点信息
            if (ActionComboBox.SelectedItem is ScheduleTaskAction selectedAction)
            {
                bool isNodeLevelAction =
                    selectedAction == ScheduleTaskAction.StartProcess
                    || selectedAction == ScheduleTaskAction.StopProcess
                    || selectedAction == ScheduleTaskAction.RestartProcess;

                if (isNodeLevelAction)
                {
                    if (
                        TargetNodeComboBox.SelectedItem is Models.ProcessItemWithLevel nodeWithLevel
                    )
                    {
                        config.TargetNodeId = nodeWithLevel.Item.NodeId;
                        config.TargetNodeName = nodeWithLevel.Item.Name;
                    }
                    else if (_rootProcessNode != null)
                    {
                        config.TargetNodeId = _rootProcessNode.NodeId;
                        config.TargetNodeName = _rootProcessNode.Name;
                    }
                    else
                    {
                        config.TargetNodeId = string.Empty;
                        config.TargetNodeName = string.Empty;
                    }
                }
                else
                {
                    config.TargetNodeId = string.Empty;
                    config.TargetNodeName = string.Empty;
                }
            }

            // 设置参数
            if (config.Trigger == ScheduleTriggerType.Daily)
            {
                if (DailyTimePicker.SelectedTime.HasValue)
                {
                    config.DailyTime = DailyTimePicker.SelectedTime.Value.ToString(@"hh\:mm\:ss");
                }
                else
                {
                    config.DailyTime = "00:00:00";
                }
            }
            else
            {
                if (int.TryParse(DelaySecondsTextBox.Text, out int delaySeconds))
                {
                    config.DelaySeconds = delaySeconds;
                }
                else
                {
                    config.DelaySeconds = 60;
                }
            }

            if (config.Action == ScheduleTaskAction.ClickMouse)
            {
                if (int.TryParse(ClickXTextBox.Text, out var x))
                    config.ClickX = x;
                if (int.TryParse(ClickYTextBox.Text, out var y))
                    config.ClickY = y;
            }

            var trigger = (ScheduleTriggerType)TriggerComboBox.SelectedItem;
            if (trigger == ScheduleTriggerType.IntervalAfterStartup)
            {
                if (!int.TryParse(MaxExecuteCountTextBox.Text, out var maxExec) || maxExec < 0)
                {
                    maxExec = 0;
                }
                config.MaxExecuteCount = maxExec;
            }
            else
            {
                config.MaxExecuteCount = 0;
            }

            Result = config;
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// 验证输入
        /// </summary>
        private bool ValidateInput()
        {
            // 验证触发方式
            if (TriggerComboBox.SelectedItem == null)
            {
                MessageBox.Show("请选择触发方式", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                TriggerComboBox.Focus();
                return false;
            }

            // 验证执行操作
            if (ActionComboBox.SelectedItem == null)
            {
                MessageBox.Show("请选择执行操作", "验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                ActionComboBox.Focus();
                return false;
            }

            // 验证参数
            var trigger = (ScheduleTriggerType)TriggerComboBox.SelectedItem;
            if (trigger == ScheduleTriggerType.Daily)
            {
                if (!DailyTimePicker.SelectedTime.HasValue)
                {
                    MessageBox.Show(
                        "请选择执行时间",
                        "验证失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    DailyTimePicker.Focus();
                    return false;
                }
            }
            else
            {
                if (
                    !int.TryParse(DelaySecondsTextBox.Text, out int delaySeconds)
                    || delaySeconds <= 0
                )
                {
                    MessageBox.Show(
                        "请输入有效的延迟或间隔时间(正整数)",
                        "验证失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    DelaySecondsTextBox.Focus();
                    return false;
                }
            }

            // 验证节点级操作必须选择目标节点
            if (ActionComboBox.SelectedItem is ScheduleTaskAction action)
            {
                bool isNodeLevelAction =
                    action == ScheduleTaskAction.StartProcess
                    || action == ScheduleTaskAction.StopProcess
                    || action == ScheduleTaskAction.RestartProcess;

                if (isNodeLevelAction && TargetNodeComboBox.SelectedItem == null)
                {
                    MessageBox.Show(
                        "节点级操作需要选择目标节点",
                        "验证失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    TargetNodeComboBox.Focus();
                    return false;
                }
            }

            if (ActionComboBox.SelectedItem is ScheduleTaskAction actionSelected)
            {
                if (actionSelected == ScheduleTaskAction.ClickMouse)
                {
                    if (!int.TryParse(ClickXTextBox.Text, out var clickX))
                    {
                        MessageBox.Show(
                            "请输入有效的 X 坐标",
                            "验证失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        ClickXTextBox.Focus();
                        return false;
                    }
                    if (!int.TryParse(ClickYTextBox.Text, out var clickY))
                    {
                        MessageBox.Show(
                            "请输入有效的 Y 坐标",
                            "验证失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        ClickYTextBox.Focus();
                        return false;
                    }

                    if (clickX < 0 || clickY < 0)
                    {
                        MessageBox.Show(
                            "坐标必须为非负整数",
                            "验证失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        return false;
                    }
                }

                if (trigger == ScheduleTriggerType.IntervalAfterStartup)
                {
                    if (!int.TryParse(MaxExecuteCountTextBox.Text, out var maxExec) || maxExec < 0)
                    {
                        MessageBox.Show(
                            "最大执行次数必须为大于等于0的整数（0 表示不限）",
                            "验证失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        MaxExecuteCountTextBox.Focus();
                        return false;
                    }
                }
            }

            return true;
        }

        private void PopulateNodeList()
        {
            TargetNodeComboBox.Items.Clear();
            if (_rootProcessNode != null)
            {
                var allNodesWithLevel = GetAllNodesWithLevel(_rootProcessNode, 0);
                foreach (var nodeWithLevel in allNodesWithLevel)
                {
                    TargetNodeComboBox.Items.Add(nodeWithLevel);
                }
            }
        }

        private List<Models.ProcessItemWithLevel> GetAllNodesWithLevel(ProcessItem root, int level)
        {
            var result = new List<Models.ProcessItemWithLevel>
            {
                new Models.ProcessItemWithLevel(root, level)
            };
            foreach (var child in root.Children)
            {
                result.AddRange(GetAllNodesWithLevel(child, level + 1));
            }
            return result;
        }

        private ProcessItem? FindNodeByName(ProcessItem? root, string name)
        {
            if (root == null)
                return null;
            if (root.Name == name)
                return root;
            foreach (var child in root.Children)
            {
                var found = FindNodeByName(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private ProcessItem? FindNodeById(ProcessItem? root, string nodeId)
        {
            if (root == null || string.IsNullOrEmpty(nodeId))
                return null;

            if (root.NodePath.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
                return root;

            foreach (var child in root.Children)
            {
                var found = FindNodeById(child, nodeId);
                if (found != null)
                    return found;
            }

            return null;
        }

        private void ActionComboBox_SelectionChanged(
            object sender,
            System.Windows.Controls.SelectionChangedEventArgs e
        )
        {
            UpdateNodeSelectorVisibility();
            EnsureDefaultTargetNode();
            UpdateParameterVisibility();
        }

        private void UpdateNodeSelectorVisibility()
        {
            if (ActionComboBox.SelectedItem is ScheduleTaskAction action)
            {
                bool isNodeLevelAction =
                    action == ScheduleTaskAction.StartProcess
                    || action == ScheduleTaskAction.StopProcess
                    || action == ScheduleTaskAction.RestartProcess;
                TargetNodePanel.Visibility = isNodeLevelAction
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void EnsureDefaultTargetNode()
        {
            if (_rootProcessNode == null)
                return;

            if (ActionComboBox.SelectedItem is ScheduleTaskAction action)
            {
                bool isNodeLevelAction =
                    action == ScheduleTaskAction.StartProcess
                    || action == ScheduleTaskAction.StopProcess
                    || action == ScheduleTaskAction.RestartProcess;

                if (isNodeLevelAction && TargetNodeComboBox.SelectedItem == null)
                {
                    if (TargetNodeComboBox.Items.Count > 0)
                    {
                        TargetNodeComboBox.SelectedIndex = 0;
                    }
                }
            }
        }
    }
}
