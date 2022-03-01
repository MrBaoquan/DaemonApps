using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Core;
using ReactiveUI;

namespace DaemonKit {

    public class ProcessCommandParameter {
        public string Path = string.Empty;
        public string Arguments = string.Empty;
        public bool RunAs = true;
    }
    public class MainViewModel : ReactiveObject {
        private ProcessCommandParameter openCMD_args = new ProcessCommandParameter { Path = "cmd.exe", RunAs = true };
        public ProcessCommandParameter OpenCMD_args { get => openCMD_args; }

        private ProcessCommandParameter openPowerShell_args = new ProcessCommandParameter { Path = "powershell.exe", RunAs = true };
        public ProcessCommandParameter OpenPowerShell_args { get => openPowerShell_args; }

        private ProcessCommandParameter openAppRoot_args = new ProcessCommandParameter { Path = "explorer.exe", RunAs = true, Arguments = AppPathes.AppRoot };
        public ProcessCommandParameter OpenAppRoot_args { get => openAppRoot_args; }

        private ProcessCommandParameter openFileExplorer_args = new ProcessCommandParameter { Path = "explorer.exe", RunAs = false };
        public ProcessCommandParameter OpenFileExplorer_args { get => openFileExplorer_args; }

        private ProcessCommandParameter openUpdatePage_args = new ProcessCommandParameter { Path = "explorer.exe", Arguments = "https://gitee.com/MrBaoquan/daemon-apps/releases", RunAs = true };
        public ProcessCommandParameter OpenUpdatePage_args { get => openUpdatePage_args; }
        public MainViewModel () {

            AddTreeNode = ReactiveCommand.Create (() => { });
            EditTreeNode = ReactiveCommand.Create (() => { });
            DeleteTreeNode = ReactiveCommand.Create (() => { });
            ShowInExplorer = ReactiveCommand.Create (() => { });
            ShowAppDirectory = ReactiveCommand.Create (() => { });
            RunNodeTree = ReactiveCommand.Create (() => { });
            KillNodeTree = ReactiveCommand.Create (() => { });
            OpenSettings = ReactiveCommand.Create (() => { });
            ToggleEnable = ReactiveCommand.Create<ProcessItem, ProcessItem> (_item => _item);

            RunProcess = ReactiveCommand.Create<ProcessCommandParameter, ProcessCommandParameter> (_parameter => _parameter);
        }

        private string _Text = "测试内容";
        public string Text {
            get { return _Text; }
            set { this.RaiseAndSetIfChanged (ref _Text, value); }
        }

        public ReactiveCommand<Unit, Unit> AddTreeNode { get; protected set; }
        public ReactiveCommand<Unit, Unit> EditTreeNode { get; protected set; }
        public ReactiveCommand<Unit, Unit> DeleteTreeNode { get; protected set; }
        /// <summary>
        /// 打开进程所在目录
        /// </summary>
        /// <value></value>
        public ReactiveCommand<Unit, Unit> ShowInExplorer { get; protected set; }
        public ReactiveCommand<Unit, Unit> ShowAppDirectory { get; protected set; }
        public ReactiveCommand<Unit, Unit> RunNodeTree { get; protected set; }
        public ReactiveCommand<Unit, Unit> KillNodeTree { get; protected set; }
        public ReactiveCommand<ProcessItem, ProcessItem> ToggleEnable { get; protected set; }
        public ReactiveCommand<Unit, Unit> OpenSettings { get; protected set; }
        public ReactiveCommand<ProcessCommandParameter, ProcessCommandParameter> RunProcess { get; protected set; }

    }
}