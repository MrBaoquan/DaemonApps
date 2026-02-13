using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Models;
using DaemonKit.PowerSaving;
using DaemonKit.Utilities;
using ReactiveUI;

namespace DaemonKit
{
    public class ProcessCommandParameter
    {
        public string Path = string.Empty;
        public string Arguments = string.Empty;
        public bool RunAs = true;
    }

    public partial class MainViewModel : ReactiveObject
    {
        private PowerSavingViewModel? _powerSaving;
        public PowerSavingViewModel? PowerSaving
        {
            get => _powerSaving;
            set => this.RaiseAndSetIfChanged(ref _powerSaving, value);
        }

        // 暴露 AppSettings 以供 UI 绑定
        public AppSettings? AppSettings => MainWindow.AppSettings;

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
            AddTreeNode = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            EditTreeNode = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            DeleteTreeNode = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            EditSchedule = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            ShowInExplorer = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            ShowAppDirectory = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            RunNodeTree = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            KillNodeTree = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            OpenSettings = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            ToggleEnable = ReactiveCommand.Create<ProcessItem, ProcessItem>(
                _item => _item,
                outputScheduler: RxApp.MainThreadScheduler
            );
            RunProcess = ReactiveCommand.Create<ProcessCommandParameter, ProcessCommandParameter>(
                _parameter => _parameter,
                outputScheduler: RxApp.MainThreadScheduler
            );
            OpenRemotePanel = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            OpenTransferList = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            OpenResourceLibrary = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            OpenPowerSavingPanel = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );

            OpenScheduleWindow = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );

            PickColor = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            TakeScreenshot = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );

            this.EnableNameInput = ReactiveCommand.Create<ProcessItem, ProcessItem>(
                _item => _item,
                outputScheduler: RxApp.MainThreadScheduler
            );
            this.ConfirmNameInput = ReactiveCommand.Create<ProcessItem, ProcessItem>(
                _item => _item,
                outputScheduler: RxApp.MainThreadScheduler
            );

            this.ShowWindow = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            this.HideWindow = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            this.Quit = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );

            this.ShutdownSystem = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            this.RestartSystem = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );
            this.ToggleTouchScreen = ReactiveCommand.Create(
                () => { },
                outputScheduler: RxApp.MainThreadScheduler
            );

            this.ExportNodePackage = ReactiveCommand.Create<ProcessItem, ProcessItem>(
                _item => _item,
                outputScheduler: RxApp.MainThreadScheduler
            );

            // 初始化音量控制
            InitializeVolumeControl();
        }

        private string _Text = "测试内容";
        public string Text
        {
            get { return _Text; }
            set { this.RaiseAndSetIfChanged(ref _Text, value); }
        }

        private ProcessItem? _rootProcessNode;
        public ProcessItem? RootProcessNode
        {
            get { return _rootProcessNode; }
            set { this.RaiseAndSetIfChanged(ref _rootProcessNode, value); }
        }

        private GlobalScheduleConfig? _globalSchedule;
        public GlobalScheduleConfig? GlobalSchedule
        {
            get { return _globalSchedule; }
            set { this.RaiseAndSetIfChanged(ref _globalSchedule, value); }
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
        public ReactiveCommand<Unit, Unit> OpenRemotePanel { get; protected set; }
        public ReactiveCommand<Unit, Unit> OpenTransferList { get; protected set; }
        public ReactiveCommand<Unit, Unit> OpenResourceLibrary { get; protected set; }
        public ReactiveCommand<Unit, Unit> OpenPowerSavingPanel { get; protected set; }

        public ReactiveCommand<Unit, Unit> OpenScheduleWindow { get; protected set; }

        public ReactiveCommand<Unit, Unit> PickColor { get; protected set; }
        public ReactiveCommand<Unit, Unit> TakeScreenshot { get; protected set; }

        public ReactiveCommand<Unit, Unit> ShowWindow { get; protected set; }
        public ReactiveCommand<Unit, Unit> HideWindow { get; protected set; }
        public ReactiveCommand<Unit, Unit> Quit { get; protected set; }

        public ReactiveCommand<Unit, Unit> ShutdownSystem { get; protected set; }
        public ReactiveCommand<Unit, Unit> RestartSystem { get; protected set; }
        public ReactiveCommand<Unit, Unit> ToggleTouchScreen { get; protected set; }
        public ReactiveCommand<ProcessItem, ProcessItem> ExportNodePackage { get; protected set; }
    }
}
