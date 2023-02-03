using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Windows;
using System.Xml.Serialization;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Core {

    public class ProcessMetaData {

        // 进程展示名
        [XmlAttribute]
        public string Name = string.Empty;
        [XmlAttribute]
        // 进程路径
        public string Path = string.Empty;

        [XmlAttribute]
        public string Arguments = string.Empty;
        [XmlAttribute]
        public bool RunAs = true;
        [XmlAttribute]
        public bool KeepTop = false;

        [XmlAttribute]
        public bool NoDaemon = false;

        [XmlAttribute]
        public bool MinimizedStartUp = false;

        [XmlAttribute]
        public int Delay = 500;

        [XmlAttribute]
        public bool Enable = true;

        [XmlAttribute]
        public int PosX = 0;
        [XmlAttribute]
        public int PosY = 0;
        [XmlAttribute]
        public int Width = 0;
        [XmlAttribute]
        public int Height = 0;

    }

    public class ProcessItem : ReactiveObject {
        [XmlIgnore]
        public ProcessItem Parent { get; set; }

        [XmlIgnore]
        public ProcessItem RootNode {
            get {
                var _node = Parent;
                if (_node == null || _node.Parent == null) return this;
                while (_node != null && !_node.Parent.IsSuperRoot) {
                    _node = _node.Parent;
                }
                return _node;
            }
        }

        [XmlIgnore]
        public bool IsSuperRoot { get => Parent == null; }

        [XmlIgnore]
        public bool IsLeaf { get => Children.Count <= 0; }

        [XmlIgnore]
        public string NodePath {
            get {
                if (!System.IO.Path.IsPathRooted (MetaData.Path)) {
                    return System.IO.Path.Combine (AppPathes.AppDir, MetaData.Path);
                }
                return MetaData.Path;
            }
        }

        private List<ProcessItem> TraceToRoot (ProcessItem InItem) {
            List<ProcessItem> _list = new List<ProcessItem> () { InItem };
            while (InItem.Parent != null) {
                _list.Add (InItem.Parent);
                InItem = InItem.Parent;
            }
            return _list;
        }

        public ProcessItem () {
            this.Children = new ObservableCollection<ProcessItem> ();

            this.RunNodeCommand = ReactiveCommand.Create (() => { });
            this.KillNodeCommand = ReactiveCommand.Create (() => { });
            this.DisableNameInput = ReactiveCommand.Create (() => { });
            this.ToggleEnableCommand = ReactiveCommand.Create<bool, bool> (_isEnable => _isEnable);

            Status = -1;

            this.RunNodeCommand.Subscribe (_ => {
                RunNode ();
            });

            this.KillNodeCommand.Subscribe (_ => {
                KillNode ();
            });

            this.ToggleEnableCommand.Subscribe (_isEnable => {
                Children.ToList ().ForEach (_child => {
                    _child.MetaData.Enable = _isEnable;
                    _child.Enable = _isEnable;
                });
            });

            this.WhenAnyValue (x => x.Enable)
                .Subscribe (_isEnable => {
                    KillNode ();
                    BtnRunVisibility = _isEnable?Visibility.Visible : Visibility.Hidden;
                });

            this.DisableNameInput.Subscribe (_ => {
                this.NameInputVisibility = Visibility.Hidden;
            });
        }

        [XmlIgnore]
        private ProcessMetaData metaData = new ProcessMetaData ();
        public ProcessMetaData MetaData {
            get => metaData;
            set {
                metaData = value;
                Name = metaData.Name;
                Path = System.IO.Path.GetFileName (metaData.Path);
                Enable = metaData.Enable;
                Delay = metaData.Delay;
                NameField = Name;
            }
        }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> RunNodeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> KillNodeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<bool, bool> ToggleEnableCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> DisableNameInput { get; protected set; }

        private IDisposable _runNodeHandler = null;
        static void ClearHandler (ref IDisposable InHandler) {
            if (InHandler != null) {
                InHandler.Dispose ();
                InHandler = null;
            }
        }

        private IDisposable m_runChildDisposables;
        // 执行节点任务
        public void RunNode () {
            if (Enable) {
                ProcManager.DaemonProcess (NodePath, metaData);
                daemonNode ();
            }

            if (_runNodeHandler != null) {
                _runNodeHandler.Dispose ();
                _runNodeHandler = null;
            }

            Action _runChildNode = () => {
                Children.ToList ().ForEach (_child => {
                    _child.KillNode ();
                    _child.Status = 0;
                });
                _runNodeHandler = Observable.Merge (
                    Children.Select (
                        _child => Observable.Timer (TimeSpan.FromMilliseconds (_child.MetaData.Delay))
                        .Select (_ => _child)
                    )
                ).Subscribe (_childNode => {
                    _childNode.RunNode ();
                }, () => {
                    if (_runNodeHandler != null) {
                        _runNodeHandler.Dispose ();
                        _runNodeHandler = null;
                    }
                });
            };

            // 进程树ROOT根节点延迟启动
            if (this.IsSuperRoot) {
                if (m_runChildDisposables != null) {
                    m_runChildDisposables.Dispose ();
                }
                Status = 0;
                NLogger.Info ("启动进程树, Delay:{0}", delayDaemon);
                m_runChildDisposables = Observable.Timer (TimeSpan.FromMilliseconds (delayDaemon))
                    .Subscribe (_ => {
                        _runChildNode ();
                        Status = 1;
                        m_runChildDisposables = null;
                    });
                return;
            }
            Status = 1;
            _runChildNode ();
        }

        private int delayDaemon = 500;
        private int daemonInterval = 5000;
        private int maxError = 1;

        // 守护当前进程节点
        IDisposable daemonHandler = null;
        [XmlIgnore]
        private int currentError = 0;

        private void daemonNode () {
            if (IsSuperRoot) return;
            if (metaData.NoDaemon) return;

            NLogger.Info ("开始守护进程:{0}", NodePath);

            currentError = 0;
            // 进程启动后, 根据守护间隔进行守护
            daemonHandler = Observable.Interval (TimeSpan.FromMilliseconds (daemonInterval))
                .Skip (1)
                .Subscribe (_ => {
                    var _process = WinAPI.FindProcess (NodePath);
                    if (_process == default (Process)) {
                        NLogger.Warn ("进程:{0} 已退出，正在尝试重新启动进程链...", NodePath);
                        RootNode.KillNode ();
                        RootNode.RunNode ();
                    } else if (!_process.Responding) {
                        ++currentError;
                        NLogger.Warn ("进程:{0} 未响应，容忍度: {1}/{2}", NodePath, currentError, maxError);
                        if (currentError >= maxError) {
                            NLogger.Warn ("进程:{0} 未响应，正在尝试重新启动进程链...", NodePath);
                            RootNode.KillNode ();
                            RootNode.RunNode ();
                            return;
                        }
                    }
                    // 如果需要窗口置顶, 则在守护间隔前3次尝试置顶
                    if ((metaData.KeepTop ||
                            !(metaData.PosX == metaData.PosY && metaData.PosX == 0) || // 需要改变位置
                            !(metaData.Width == metaData.Height && metaData.Width == 0) // 需要改变大小
                        ) &&
                        _ <= 3) {
                        NLogger.Info ($"尝试调整窗口:{NodePath} 第{_}次");

                        ProcManager.KeepTopWindow (
                            _process.MainWindowHandle, metaData.PosX, metaData.PosY, metaData.Width, metaData.Height,
                            (int) (metaData.KeepTop?HWndInsertAfter.HWND_TOPMOST : HWndInsertAfter.HWND_NOTOPMOST)
                        );
                        WinAPI.ShowWindow (_process.MainWindowHandle, (int) CMDShow.SW_SHOW);
                        if (_ == 3) {
                            NLogger.Info ($"尝试调整窗口:{NodePath} 任务结束");
                        }
                    }
                });
        }

        public void KillNode () {

            if (_runNodeHandler != null) {
                _runNodeHandler.Dispose ();
                _runNodeHandler = null;
            }

            if (daemonHandler != null) {
                daemonHandler.Dispose ();
                daemonHandler = null;
            }

            if (this.IsSuperRoot) {
                NLogger.Info ("终止进程树..");
            }

            Status = -1;
            ProcManager.KillProcess (NodePath);
            Children.ToList ().ForEach (_child => {
                _child.KillNode ();
            });
        }

        public void SyncEnable () {
            new List<ProcessItem> { this }.Flatten<ProcessItem> (_item => _item.Children).ToList ().ForEach (_child => {
                _child.MetaData.Enable = Enable;
                _child.Enable = Enable;
            });
        }

        public void EnableNameInput () {
            this.NameInputVisibility = Visibility.Visible;
        }

        private string _name = string.Empty;
        [XmlIgnore]
        public string Name { set => this.RaiseAndSetIfChanged (ref _name, value); get => _name; }

        private string _nameField = string.Empty;
        [XmlIgnore]
        public string NameField { set => this.RaiseAndSetIfChanged (ref _nameField, value); get => _nameField; }
        private bool _enable = true;
        [XmlIgnore]
        public bool Enable { set => this.RaiseAndSetIfChanged (ref _enable, value); get => _enable; }

        private int _delay = 500;
        [XmlAttribute]
        public int Delay { set => this.RaiseAndSetIfChanged (ref _delay, value); get => _delay; }

        private string _path = string.Empty;
        [XmlAttribute]
        public string Path { set => this.RaiseAndSetIfChanged (ref _path, value); get => _path; }

        public bool IsRuning { get => Status == 1; }

        private int _status = -1; // -1 未启动 0 启动中  1 已启动  
        public int Status {
            set {
                BtnRunVisibility = Visibility.Collapsed;
                BtnLoadingVisibility = Visibility.Collapsed;
                BtnStopVisibility = Visibility.Collapsed;

                if (value == -1) {
                    BtnRunVisibility = Enable? Visibility.Visible : Visibility.Hidden;
                } else if (value == 0) {
                    BtnLoadingVisibility = Enable? Visibility.Visible : Visibility.Hidden;
                } else if (value == 1) {
                    BtnStopVisibility = Enable? Visibility.Visible : Visibility.Hidden;
                }
                _status = value;
            }
            get => _status;
        }

        [XmlIgnore]
        private Visibility btnRunVisibility = Visibility.Collapsed;
        [XmlIgnore]
        public Visibility BtnRunVisibility { get => btnRunVisibility; set => this.RaiseAndSetIfChanged (ref btnRunVisibility, value); }

        [XmlIgnore]
        private Visibility nameInputVisibility = Visibility.Collapsed;
        [XmlIgnore]
        public Visibility NameInputVisibility { get => nameInputVisibility; set => this.RaiseAndSetIfChanged (ref nameInputVisibility, value); }

        [XmlIgnore]
        private Visibility btnLoadingVisibility = Visibility.Collapsed;
        [XmlIgnore]
        public Visibility BtnLoadingVisibility { get => btnLoadingVisibility; set => this.RaiseAndSetIfChanged (ref btnLoadingVisibility, value); }

        [XmlIgnore]
        private Visibility btnStopVisibility = Visibility.Collapsed;
        [XmlIgnore]
        public Visibility BtnStopVisibility { get => btnStopVisibility; set => this.RaiseAndSetIfChanged (ref btnStopVisibility, value); }

        public ObservableCollection<ProcessItem> Children { set; get; }

        /// <summary>
        /// 添加子节点
        /// </summary>
        /// <param name="InChild"></param>
        public void AddChild (ProcessItem InChild) {
            InChild.Parent = this;
            Children.Add (InChild);
        }

        /// <summary>
        /// 移除子节点
        /// </summary>
        /// <param name="InChild"></param>
        public void RemoveChild (ProcessItem InChild) {
            Children.Remove (InChild);
        }

        /// <summary>
        /// 同步子节点的父级关系
        /// </summary>
        public void SyncRelationships () {
            Action<ProcessItem> _sync = null;
            _sync = (ProcessItem InItem) => {
                InItem.Children.ToList ().ForEach (_child => {
                    _child.Parent = InItem;
                    if (_child.Children.Count > 0) {
                        _sync (_child);
                    }
                });
            };
            _sync (this);
        }

        public void SyncSettings (AppSettings appSettings) {
            this.delayDaemon = appSettings.DelayDaemon;
            this.daemonInterval = appSettings.DaemonInterval;
            this.maxError = appSettings.ErrorCount;
            this.Children.ToList ().ForEach (_childNode => {
                _childNode.SyncSettings (appSettings);
            });
        }

        public void ConfirmNameInput () {
            if (NameField.Trim () == string.Empty) {
                NLogger.Warn ("备注名不能为空");
                return;
            }
            Name = NameField;
            metaData.Name = Name;
            NameInputVisibility = Visibility.Collapsed;
        }

    }
}