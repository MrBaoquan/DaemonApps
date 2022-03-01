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
        public int Delay = 500;

        [XmlAttribute]
        public bool Enable = true;

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
        public bool IsLeaf { get => Childs.Count <= 0; }

        [XmlIgnore]
        private string nodePath {
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
            this.Childs = new ObservableCollection<ProcessItem> ();

            this.RunNodeCommand = ReactiveCommand.Create (() => { });
            this.KillNodeCommand = ReactiveCommand.Create (() => { });
            this.ToggleEnableCommand = ReactiveCommand.Create<bool, bool> (_isEnable => _isEnable);

            Status = -1;

            this.RunNodeCommand.Subscribe (_ => {
                RunNode ();
            });

            this.KillNodeCommand.Subscribe (_ => {
                KillNode ();
            });

            this.ToggleEnableCommand.Subscribe (_isEnable => {
                Childs.ToList ().ForEach (_child => {
                    _child.MetaData.Enable = _isEnable;
                    _child.Enable = _isEnable;
                });
            });

            this.WhenAnyValue (x => x.Enable).Subscribe (_isEnable => {
                KillNode ();
                BtnRunVisibility = _isEnable?Visibility.Visible : Visibility.Hidden;
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
            }
        }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> RunNodeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<Unit, Unit> KillNodeCommand { get; protected set; }

        [XmlIgnore]
        public ReactiveCommand<bool, bool> ToggleEnableCommand { get; protected set; }

        private IDisposable _runNodeHandler = null;
        static void ClearHandler (ref IDisposable InHandler) {
            if (InHandler != null) {
                InHandler.Dispose ();
                InHandler = null;
            }
        }

        // 执行节点任务
        public void RunNode () {
            if (Enable) {
                ProcManager.DaemonProcess (nodePath, metaData.Arguments, metaData.RunAs);
                daemonNode ();
            }

            if (_runNodeHandler != null) {
                _runNodeHandler.Dispose ();
                _runNodeHandler = null;
            }

            Status = 1;
            Childs.ToList ().ForEach (_child => {
                _child.KillNode ();
                _child.Status = 0;
            });
            _runNodeHandler = Observable.Merge (
                Childs.Select (
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
        }

        private int delayDaemon = 5000;
        private int daemonInterval = 500;
        private int maxError = 1;

        // 守护当前进程节点
        IDisposable daemonHandler = null;
        private int currentError = 0;

        private void daemonNode () {
            if (IsSuperRoot) return;
            NLogger.Info ("守护进程:{0}, Delay:{1}, Interval:{2}", nodePath, delayDaemon, daemonInterval);
            currentError = 0;

            daemonHandler = Observable.Timer (TimeSpan.FromMilliseconds (delayDaemon), TimeSpan.FromMilliseconds (daemonInterval)).Subscribe (_ => {
                var _process = WinAPI.FindProcess (nodePath);
                if (_process == default (Process)) {
                    NLogger.Warn ("进程:{0} 已退出，正在尝试重新启动进程链...", nodePath);
                    RootNode.KillNode ();
                    RootNode.RunNode ();
                } else if (!_process.Responding) {
                    ++currentError;
                    NLogger.Warn ("进程:{0} 未响应，容忍度: {1}/{2}", nodePath, currentError, maxError);
                    if (currentError >= maxError) {
                        NLogger.Warn ("进程:{0} 未响应，正在尝试重新启动进程链...", nodePath);
                        RootNode.KillNode ();
                        RootNode.RunNode ();
                    }
                }
                if (metaData.KeepTop)
                    ProcManager.KeepTopWindow (nodePath);
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

            Status = -1;
            ProcManager.KillProcess (nodePath);
            Childs.ToList ().ForEach (_child => {
                _child.KillNode ();
            });
        }

        public void SyncEnable () {
            new List<ProcessItem> { this }.Flatten<ProcessItem> (_item => _item.Childs).ToList ().ForEach (_child => {
                _child.MetaData.Enable = Enable;
                _child.Enable = Enable;
            });
        }

        private string _name = string.Empty;
        [XmlIgnore]
        public string Name { set => this.RaiseAndSetIfChanged (ref _name, value); get => _name; }
        private bool _enable = true;
        [XmlIgnore]
        public bool Enable { set => this.RaiseAndSetIfChanged (ref _enable, value); get => _enable; }

        private int _delay = 500;
        [XmlAttribute]
        public int Delay { set => this.RaiseAndSetIfChanged (ref _delay, value); get => _delay; }

        private string _path = string.Empty;
        [XmlAttribute]
        public string Path { set => this.RaiseAndSetIfChanged (ref _path, value); get => _path; }

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
        private Visibility btnLoadingVisibility = Visibility.Collapsed;
        [XmlIgnore]
        public Visibility BtnLoadingVisibility { get => btnLoadingVisibility; set => this.RaiseAndSetIfChanged (ref btnLoadingVisibility, value); }

        [XmlIgnore]
        private Visibility btnStopVisibility = Visibility.Collapsed;
        [XmlIgnore]
        public Visibility BtnStopVisibility { get => btnStopVisibility; set => this.RaiseAndSetIfChanged (ref btnStopVisibility, value); }

        public ObservableCollection<ProcessItem> Childs { set; get; }

        /// <summary>
        /// 添加子节点
        /// </summary>
        /// <param name="InChild"></param>
        public void AddChild (ProcessItem InChild) {
            InChild.Parent = this;
            Childs.Add (InChild);
        }

        /// <summary>
        /// 移除子节点
        /// </summary>
        /// <param name="InChild"></param>
        public void RemoveChild (ProcessItem InChild) {
            Childs.Remove (InChild);
        }

        /// <summary>
        /// 同步子节点的父级关系
        /// </summary>
        public void SyncRelationships () {
            Action<ProcessItem> _sync = null;
            _sync = (ProcessItem InItem) => {
                InItem.Childs.ToList ().ForEach (_child => {
                    _child.Parent = InItem;
                    if (_child.Childs.Count > 0) {
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
            this.Childs.ToList ().ForEach (_childNode => {
                _childNode.SyncSettings (appSettings);
            });
        }

    }
}