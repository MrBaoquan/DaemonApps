using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Windows;
using System.Xml.Serialization;
using DaemonKit.Core;
using DaemonKit.Utilities;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Models
{
    public enum TriggerType
    {
        Daily,
        Interval,
        OnAppStart, // 程序启动后
        OnAppStartOnce // 每天首次启动后
    }

    public class TaskTrigger
    {
        [XmlAttribute]
        public TriggerType Mode { get; set; } = TriggerType.Daily;

        [XmlAttribute]
        public string Time { get; set; } = "00:00";

        [XmlAttribute]
        public int Interval { get; set; } = 60;

        [XmlAttribute]
        public int Unit { get; set; } = 2; // 0: 秒 1: 分 2: 时
    }

    public class ProcessMetaData
    {
        // 进程展示名
        [XmlAttribute]
        public string Name { get; set; } = string.Empty;

        [XmlAttribute]
        // 进程路径
        public string Path { get; set; } = string.Empty;

        [XmlAttribute]
        public string Arguments { get; set; } = string.Empty;

        [XmlAttribute]
        public bool RunAs { get; set; } = true;

        [XmlAttribute]
        public bool KeepTop { get; set; } = false;

        [XmlAttribute]
        public bool NoDaemon { get; set; } = false;

        [XmlAttribute]
        public bool IsScript { get; set; } = false;

        [XmlAttribute]
        public bool MoveWindow { get; set; } = false;

        [XmlAttribute]
        public bool ResizeWindow { get; set; } = false;

        [XmlAttribute]
        public bool MinimizedStartUp { get; set; } = false;

        [XmlAttribute]
        public int Delay { get; set; } = 500;

        [XmlAttribute]
        public bool Enable { get; set; } = true;

        [XmlAttribute]
        public int PosX { get; set; } = 0;

        [XmlAttribute]
        public int PosY { get; set; } = 0;

        [XmlAttribute]
        public int Width { get; set; } = 0;

        [XmlAttribute]
        public int Height { get; set; } = 0;

        [XmlElement("Schedule")]
        public List<TaskTrigger> Triggers = new List<TaskTrigger>();
    }

    public partial class ProcessItem : ReactiveObject
    {
        [XmlIgnore]
        public ProcessItem? Parent { get; set; }

        [XmlIgnore]
        private bool _isSelected;

        /// <summary>
        /// 是否选中（用于导出时的多选）
        /// 选中子节点时会自动选中所有父节点（依赖链）
        /// 取消选中时会自动取消所有子节点
        /// </summary>
        [XmlIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    this.RaiseAndSetIfChanged(ref _isSelected, value);

                    if (value)
                    {
                        // 选中时，强制选中所有父节点（依赖链）
                        SelectAllParents();
                    }
                    else
                    {
                        // 取消选中时，递归取消所有子节点
                        DeselectAllChildren();
                    }
                }
            }
        }

        /// <summary>
        /// 选中所有父节点
        /// </summary>
        private void SelectAllParents()
        {
            var parent = Parent;
            while (parent != null && !parent.IsSuperRoot)
            {
                if (!parent._isSelected)
                {
                    parent._isSelected = true;
                    parent.RaisePropertyChanged(nameof(IsSelected));
                }
                parent = parent.Parent;
            }
        }

        /// <summary>
        /// 取消选中所有子节点
        /// </summary>
        private void DeselectAllChildren()
        {
            if (Children == null)
                return;

            foreach (var child in Children)
            {
                if (child._isSelected)
                {
                    child._isSelected = false;
                    child.RaisePropertyChanged(nameof(IsSelected));
                    child.DeselectAllChildren();
                }
            }
        }

        [XmlIgnore]
        public ProcessItem RootNode
        {
            get
            {
                var _node = Parent;
                if (_node == null || _node.Parent == null)
                    return this;
                while (_node != null && _node.Parent != null && _node.Parent.IsSuperRoot == false)
                {
                    _node = _node.Parent;
                }
                return _node ?? this;
            }
        }

        [XmlIgnore]
        public bool IsSuperRoot
        {
            get => Parent == null;
        }

        [XmlIgnore]
        public bool IsLeaf
        {
            get => Children.Count <= 0;
        }

        private string _nodeId = string.Empty;

        /// <summary>
        /// 节点唯一标识（用于区分同名节点），若未设置则自动生成
        /// </summary>
        [XmlAttribute]
        public string NodeId
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_nodeId))
                {
                    _nodeId = Guid.NewGuid().ToString("N");
                }
                return _nodeId;
            }
            set => _nodeId = value;
        }

        [XmlIgnore]
        public string ShortNodeId
        {
            get
            {
                var id = NodeId;
                return id.Length > 8 ? id.Substring(0, 8) : id;
            }
        }

        [XmlIgnore]
        public string NodePath
        {
            get
            {
                if (!System.IO.Path.IsPathRooted(MetaData.Path))
                {
                    return System.IO.Path.Combine(AppPathes.AppDir, MetaData.Path);
                }
                return MetaData.Path;
            }
        }

        private List<ProcessItem> TraceToRoot(ProcessItem InItem)
        {
            List<ProcessItem> _list = new List<ProcessItem>() { InItem };
            while (InItem.Parent != null)
            {
                _list.Add(InItem.Parent);
                InItem = InItem.Parent;
            }
            return _list;
        }

        public ProcessItem()
        {
            this.Children = new ObservableCollection<ProcessItem>();
            this.RunNodeCommand = ReactiveCommand.Create(() => { });
            this.KillNodeCommand = ReactiveCommand.Create(() => { });
            this.DisableNameInput = ReactiveCommand.Create(() => { });
            this.ToggleEnableCommand = ReactiveCommand.Create<bool, bool>(_isEnable => _isEnable);
            this.ScheduleCommand = ReactiveCommand.Create(() => { });

            Status = -1;

            this.RunNodeCommand.Subscribe(_ =>
            {
                RunNode();
            });

            this.KillNodeCommand.Subscribe(_ =>
            {
                KillNode();
            });

            this.ToggleEnableCommand.Subscribe(_isEnable =>
            {
                Children
                    .ToList()
                    .ForEach(_child =>
                    {
                        _child.MetaData.Enable = _isEnable;
                        _child.Enable = _isEnable;
                    });
            });

            this.WhenAnyValue(x => x.Enable)
                .Subscribe(_isEnable =>
                {
                    if (!_isEnable)
                    {
                        KillNode();
                    }
                    BtnRunVisibility = _isEnable ? Visibility.Visible : Visibility.Hidden;
                });

            this.DisableNameInput.Subscribe(_ =>
            {
                this.NameInputVisibility = Visibility.Hidden;
            });
        }

        private ProcessMetaData metaData = new ProcessMetaData();
        public ProcessMetaData MetaData
        {
            get => metaData;
            set
            {
                metaData = value;
                Name = metaData.Name;
                Path = System.IO.Path.GetFileName(metaData.Path);
                Enable = metaData.Enable;
                Delay = metaData.Delay;
                NameField = Name;
            }
        }

        [XmlElement("ScheduleItem")]
        public List<ScheduleItem> ScheduleItems { get; set; } = new List<ScheduleItem>();

        /// <summary>新的计划任务配置列表</summary>
        [XmlElement("ScheduleTaskConfig")]
        public List<ScheduleTaskConfig> ScheduleTaskConfigs { get; set; } =
            new List<ScheduleTaskConfig>();

        /// <summary>全局计划任务启用标志</summary>
        private bool _scheduleTasksEnabled = true;

        [XmlAttribute]
        public bool ScheduleTasksEnabled
        {
            get => _scheduleTasksEnabled;
            set => this.RaiseAndSetIfChanged(ref _scheduleTasksEnabled, value);
        }

        [XmlIgnore]
        public string ProcessName => System.IO.Path.GetFileName(metaData.Path);

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> RunNodeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> KillNodeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<bool, bool> ToggleEnableCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> DisableNameInput { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> ScheduleCommand { get; protected set; }

        private IDisposable? _runNodeHandler = null;

        static void ClearHandler(ref IDisposable? InHandler)
        {
            if (InHandler != null)
            {
                InHandler.Dispose();
                InHandler = null;
            }
        }

        private IDisposable? m_runChildDisposables;

        // 刷新结点计划任务
        public List<(ProcessItem processItem, ScheduleItem scheduleItem)> RefreshSchedule()
        {
            return AllChildren()
                .SelectMany(_child => _child.ScheduleItems.Select(_item => (_child, _item)))
                .Where(_ => _._item.CanExecute())
                .ToList();

            // .ToList()
            // .ForEach(_child =>
            // {
            //     _child.ScheduleItems
            //         .ToList()
            //         .ForEach(_item =>
            //         {
            //             if (_item.CanExecute())
            //             {
            //                 _item.MarkAsExecuted();
            //                 if (_item.TaskType == ScheduleTaskType.Start)
            //                 {
            //                     NLogger.Info($"执行计划任务: {_item.TaskType} {_child.Name}");
            //                     _child.RunNode();
            //                 }
            //                 else if (_item.TaskType == ScheduleTaskType.Stop)
            //                 {
            //                     _child.KillNode();
            //                 }
            //                 else if (_item.TaskType == ScheduleTaskType.Shutdown)
            //                 {
            //                     NLogger.Info($"执行计划任务: {_item.TaskType} {_child.Name}");
            //                 }
            //             }
            //         });
            // });
        }

        public List<ProcessItem> AllChildren()
        {
            List<ProcessItem> _list = new List<ProcessItem>();
            _list.Add(this);
            Children
                .ToList()
                .ForEach(_child =>
                {
                    _list.AddRange(_child.AllChildren());
                });
            return _list;
        }

        public void SyncEnable()
        {
            new List<ProcessItem> { this }
                .Flatten<ProcessItem>(_item => _item.Children)
                .ToList()
                .ForEach(_child =>
                {
                    _child.MetaData.Enable = Enable;
                    _child.Enable = Enable;
                });
        }

        public void EnableNameInput()
        {
            this.NameInputVisibility = Visibility.Visible;
        }

        private string _name = string.Empty;

        [XmlIgnore]
        public string Name
        {
            set => this.RaiseAndSetIfChanged(ref _name, value);
            get => _name;
        }

        private string _nameField = string.Empty;

        [XmlIgnore]
        public string NameField
        {
            set => this.RaiseAndSetIfChanged(ref _nameField, value);
            get => _nameField;
        }
        private bool _enable = true;

        [XmlIgnore]
        public bool Enable
        {
            set => this.RaiseAndSetIfChanged(ref _enable, value);
            get => _enable;
        }

        private int _delay = 500;

        [XmlAttribute]
        public int Delay
        {
            set => this.RaiseAndSetIfChanged(ref _delay, value);
            get => _delay;
        }

        private string _path = string.Empty;

        [XmlAttribute]
        public string Path
        {
            set => this.RaiseAndSetIfChanged(ref _path, value);
            get => _path;
        }

        public bool IsRuning
        {
            get => Status == 1;
        }

        [XmlIgnore]
        private int _status = -1; // -1 未启动 0 启动中  1 已启动
        public int Status
        {
            set
            {
                BtnRunVisibility = Visibility.Collapsed;
                BtnLoadingVisibility = Visibility.Collapsed;
                BtnStopVisibility = Visibility.Collapsed;

                if (value == -1)
                {
                    BtnRunVisibility = Enable ? Visibility.Visible : Visibility.Hidden;
                }
                else if (value == 0)
                {
                    BtnLoadingVisibility = Enable ? Visibility.Visible : Visibility.Hidden;
                }
                else if (value == 1)
                {
                    BtnStopVisibility = Enable ? Visibility.Visible : Visibility.Hidden;
                }
                _status = value;
            }
            get => _status;
        }

        [XmlIgnore]
        private Visibility btnRunVisibility = Visibility.Collapsed;

        [XmlIgnore]
        public Visibility BtnRunVisibility
        {
            get => btnRunVisibility;
            set => this.RaiseAndSetIfChanged(ref btnRunVisibility, value);
        }

        [XmlIgnore]
        private Visibility nameInputVisibility = Visibility.Collapsed;

        [XmlIgnore]
        public Visibility NameInputVisibility
        {
            get => nameInputVisibility;
            set => this.RaiseAndSetIfChanged(ref nameInputVisibility, value);
        }

        [XmlIgnore]
        private Visibility btnLoadingVisibility = Visibility.Collapsed;

        [XmlIgnore]
        public Visibility BtnLoadingVisibility
        {
            get => btnLoadingVisibility;
            set => this.RaiseAndSetIfChanged(ref btnLoadingVisibility, value);
        }

        [XmlIgnore]
        private Visibility btnStopVisibility = Visibility.Collapsed;

        [XmlIgnore]
        public Visibility BtnStopVisibility
        {
            get => btnStopVisibility;
            set => this.RaiseAndSetIfChanged(ref btnStopVisibility, value);
        }

        public ObservableCollection<ProcessItem> Children { set; get; }

        /// <summary>
        /// 添加子节点
        /// </summary>
        /// <param name="InChild"></param>
        public void AddChild(ProcessItem InChild)
        {
            InChild.Parent = this;
            Children.Add(InChild);
        }

        /// <summary>
        /// 移除子节点
        /// </summary>
        /// <param name="InChild"></param>
        public void RemoveChild(ProcessItem InChild)
        {
            Children.Remove(InChild);
        }

        /// <summary>
        /// 同步子节点的父级关系
        /// </summary>
        public void SyncRelationships()
        {
            Action<ProcessItem> _sync = _ => { };
            _sync = (ProcessItem InItem) =>
            {
                InItem.Children
                    .ToList()
                    .ForEach(_child =>
                    {
                        _child.Parent = InItem;
                        if (_child.Children.Count > 0)
                        {
                            _sync(_child);
                        }
                    });
            };
            _sync(this);
        }

        public void SyncSettings(AppSettings appSettings)
        {
            this.delayDaemon = appSettings.DelayDaemon;
            this.daemonInterval = appSettings.DaemonInterval;
            this.maxError = appSettings.ErrorCount;
            this.Children
                .ToList()
                .ForEach(_childNode =>
                {
                    _childNode.SyncSettings(appSettings);
                });
        }

        public void ConfirmNameInput()
        {
            if (NameField.Trim() == string.Empty)
            {
                NLogger.Warn("备注名不能为空");
                return;
            }
            Name = NameField;
            metaData.Name = Name;
            NameInputVisibility = Visibility.Collapsed;
        }
    }
}
