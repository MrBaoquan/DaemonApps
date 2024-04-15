using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Core;
using ReactiveUI;

namespace DaemonKit
{
    public class ProcessCommandParameter
    {
        public string Path = string.Empty;
        public string Arguments = string.Empty;
        public bool RunAs = true;
    }

    public class MainViewModel : ReactiveObject
    {
        private ProcessCommandParameter openCMD_args = new ProcessCommandParameter
        {
            Path = "cmd.exe",
            RunAs = true
        };
        public ProcessCommandParameter OpenCMD_args
        {
            get => openCMD_args;
        }

        private ProcessCommandParameter openPowerShell_args = new ProcessCommandParameter
        {
            Path = "powershell.exe",
            RunAs = true
        };
        public ProcessCommandParameter OpenPowerShell_args
        {
            get => openPowerShell_args;
        }

        private ProcessCommandParameter openAppRoot_args = new ProcessCommandParameter
        {
            Path = "explorer.exe",
            RunAs = true,
            Arguments = AppPathes.AppRoot
        };
        public ProcessCommandParameter OpenAppRoot_args
        {
            get => openAppRoot_args;
        }

        private ProcessCommandParameter openFileExplorer_args = new ProcessCommandParameter
        {
            Path = @"c:\windows\explorer.exe",
            RunAs = true,
            Arguments = ""
        };
        public ProcessCommandParameter OpenFileExplorer_args
        {
            get => openFileExplorer_args;
        }

        private ProcessCommandParameter killFileExplorer_args = new ProcessCommandParameter
        {
            Path = @"taskkill.exe",
            RunAs = true,
            Arguments = "/f /im explorer.exe"
        };
        public ProcessCommandParameter KillFileExplorer_args
        {
            get => killFileExplorer_args;
        }

        private ProcessCommandParameter openUpdatePage_args = new ProcessCommandParameter
        {
            Path = "explorer.exe",
            Arguments = "https://gitee.com/MrBaoquan/daemon-apps/releases",
            RunAs = true
        };
        public ProcessCommandParameter OpenUpdatePage_args
        {
            get => openUpdatePage_args;
        }

        public MainViewModel()
        {
            AddTreeNode = ReactiveCommand.Create(() => { });
            EditTreeNode = ReactiveCommand.Create(() => { });
            DeleteTreeNode = ReactiveCommand.Create(() => { });
            EditSchedule = ReactiveCommand.Create(() => { });
            ShowInExplorer = ReactiveCommand.Create(() => { });
            ShowAppDirectory = ReactiveCommand.Create(() => { });
            RunNodeTree = ReactiveCommand.Create(() => { });
            KillNodeTree = ReactiveCommand.Create(() => { });
            OpenSettings = ReactiveCommand.Create(() => { });
            ToggleEnable = ReactiveCommand.Create<ProcessItem, ProcessItem>(_item => _item);
            RunProcess = ReactiveCommand.Create<ProcessCommandParameter, ProcessCommandParameter>(
                _parameter => _parameter
            );
            SMBShare = ReactiveCommand.Create(() => { });
            SMBUnshare = ReactiveCommand.Create(() => { });
            OpenRemotePanel = ReactiveCommand.Create(() => { });

            OpenScheduleWindow = ReactiveCommand.Create(() => { });

            this.EnableNameInput = ReactiveCommand.Create<ProcessItem, ProcessItem>(_item => _item);
            this.ConfirmNameInput = ReactiveCommand.Create<ProcessItem, ProcessItem>(
                _item => _item
            );

            this.ShowWindow = ReactiveCommand.Create(() => { });
            this.HideWindow = ReactiveCommand.Create(() => { });
            this.Quit = ReactiveCommand.Create(() => { });

            this.ShutdownSystem = ReactiveCommand.Create(() => { });
            this.RestartSystem = ReactiveCommand.Create(() => { });
        }

        private string _Text = "测试内容";
        public string Text
        {
            get { return _Text; }
            set { this.RaiseAndSetIfChanged(ref _Text, value); }
        }

        public ReactiveCommand<Unit, Unit> AddTreeNode { get; protected set; }
        public ReactiveCommand<Unit, Unit> EditTreeNode { get; protected set; }
        public ReactiveCommand<Unit, Unit> DeleteTreeNode { get; protected set; }
        public ReactiveCommand<Unit, Unit> EditSchedule { get; protected set; }
        public ReactiveCommand<Unit, Unit> ShowInExplorer { get; protected set; }
        public ReactiveCommand<Unit, Unit> ShowAppDirectory { get; protected set; }
        public ReactiveCommand<Unit, Unit> RunNodeTree { get; protected set; }
        public ReactiveCommand<Unit, Unit> KillNodeTree { get; protected set; }
        public ReactiveCommand<ProcessItem, ProcessItem> ToggleEnable { get; protected set; }
        public ReactiveCommand<Unit, Unit> OpenSettings { get; protected set; }
        public ReactiveCommand<ProcessCommandParameter, ProcessCommandParameter> RunProcess
        {
            get;
            protected set;
        }
        public ReactiveCommand<ProcessItem, ProcessItem> EnableNameInput { get; protected set; }
        public ReactiveCommand<ProcessItem, ProcessItem> ConfirmNameInput { get; protected set; }

        public ReactiveCommand<Unit, Unit> SMBShare { get; protected set; }
        public ReactiveCommand<Unit, Unit> SMBUnshare { get; protected set; }
        public ReactiveCommand<Unit, Unit> OpenRemotePanel { get; protected set; }

        public ReactiveCommand<Unit, Unit> OpenScheduleWindow { get; protected set; }

        public ReactiveCommand<Unit, Unit> ShowWindow { get; protected set; }
        public ReactiveCommand<Unit, Unit> HideWindow { get; protected set; }
        public ReactiveCommand<Unit, Unit> Quit { get; protected set; }

        public ReactiveCommand<Unit, Unit> ShutdownSystem { get; protected set; }
        public ReactiveCommand<Unit, Unit> RestartSystem { get; protected set; }
    }
}
