using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Core;
using Microsoft.Win32;
using ReactiveUI;

namespace DaemonKit
{
    public class AppSettings
    {
        public bool StartUp { get; set; } = true;
        public bool MinimizeStartUp { get; set; } = false;
        public bool DisableExplorer { get; set; } = false;
        public bool ShortCut { get; set; } = true;
        public bool EnableGlobalHotKey { get; set; } = true;
        public int StartUpDelay { get; set; } = 0;
        public int DelayDaemon { get; set; } = 500;
        public int DaemonInterval { get; set; } = 5000;
        public int ErrorCount { get; set; } = 5;
        public string CrashWindows { get; set; } = string.Empty;
        public bool SafeKillProcess { get; set; } = false;
        public int SafeKillTimeout { get; set; } = 5000;
        public bool DisableTouchScreen { get; set; } = false;
        public bool EnableCountdownConfirm { get; set; } = true; // 启用重启/关机倒计时确认
    }

    public class SettingsViewModel : ReactiveObject
    {
        public SettingsViewModel()
        {
            Confirm = ReactiveCommand.Create(
                () =>
                {
                    return new AppSettings
                    {
                        StartUp = StartUp,
                        ShortCut = ShortCut,
                        EnableGlobalHotKey = EnableGlobalHotKey,
                        MinimizeStartUp = MinimizeStartUp,
                        DisableExplorer = DisableExplorer,
                        StartUpDelay = StartUpDelay,
                        DelayDaemon = DelayDaemon,
                        DaemonInterval = DaemonInterval,
                        ErrorCount = ErrorCount,
                        CrashWindows = CrashWindows,
                        SafeKillProcess = SafeKillProcess,
                        SafeKillTimeout = SafeKillTimeout,
                        DisableTouchScreen = DisableTouchScreen,
                        EnableCountdownConfirm = EnableCountdownConfirm
                    };
                },
                outputScheduler: RxApp.MainThreadScheduler
            );
            Cancel = ReactiveCommand.Create(() => { }, outputScheduler: RxApp.MainThreadScheduler);
        }

        public void SyncSettings(AppSettings settings)
        {
            StartUp = settings.StartUp;
            ShortCut = settings.ShortCut;
            EnableGlobalHotKey = settings.EnableGlobalHotKey;
            MinimizeStartUp = settings.MinimizeStartUp;
            DisableExplorer = settings.DisableExplorer;
            StartUpDelay = settings.StartUpDelay;
            DelayDaemon = settings.DelayDaemon;
            DaemonInterval = settings.DaemonInterval;
            ErrorCount = settings.ErrorCount;
            CrashWindows = settings.CrashWindows;
            SafeKillProcess = settings.SafeKillProcess;
            SafeKillTimeout = settings.SafeKillTimeout;
            DisableTouchScreen = settings.DisableTouchScreen;
            EnableCountdownConfirm = settings.EnableCountdownConfirm;
        }

        private bool startUP = true;
        public bool StartUp
        {
            get => startUP;
            set => this.RaiseAndSetIfChanged(ref startUP, value);
        }
        private bool shortcut = true;
        public bool ShortCut
        {
            get => shortcut;
            set => this.RaiseAndSetIfChanged(ref shortcut, value);
        }

        private bool enableGlobalHotKey = true;
        public bool EnableGlobalHotKey
        {
            get => enableGlobalHotKey;
            set => this.RaiseAndSetIfChanged(ref enableGlobalHotKey, value);
        }

        private bool minimizeStartUp = false;
        public bool MinimizeStartUp
        {
            get => minimizeStartUp;
            set => this.RaiseAndSetIfChanged(ref minimizeStartUp, value);
        }

        private bool disableExplorer = false;
        public bool DisableExplorer
        {
            get => disableExplorer;
            set => this.RaiseAndSetIfChanged(ref disableExplorer, value);
        }

        private bool disableTouchScreen = false;
        public bool DisableTouchScreen
        {
            get => disableTouchScreen;
            set => this.RaiseAndSetIfChanged(ref disableTouchScreen, value);
        }

        private int startUpDelay = 0;
        public int StartUpDelay
        {
            get => startUpDelay;
            set => this.RaiseAndSetIfChanged(ref startUpDelay, Math.Max(value, 0));
        }

        private int delayDaemon = 500;
        public int DelayDaemon
        {
            get => delayDaemon;
            set => this.RaiseAndSetIfChanged(ref delayDaemon, Math.Max(value, 100));
        }

        private int daemonInterval = 5000;
        public int DaemonInterval
        {
            get => daemonInterval;
            set => this.RaiseAndSetIfChanged(ref daemonInterval, Math.Max(value, 100));
        }

        private int errorCount = 1;
        public int ErrorCount
        {
            get => errorCount;
            set => this.RaiseAndSetIfChanged(ref errorCount, value);
        }

        private string crashWindows = string.Empty;
        public string CrashWindows
        {
            get => crashWindows;
            set => this.RaiseAndSetIfChanged(ref crashWindows, value);
        }

        private bool safeKillProcess = false;
        public bool SafeKillProcess
        {
            get => safeKillProcess;
            set => this.RaiseAndSetIfChanged(ref safeKillProcess, value);
        }

        private int safeKillTimeout = 5000;
        public int SafeKillTimeout
        {
            get => safeKillTimeout;
            set => this.RaiseAndSetIfChanged(ref safeKillTimeout, Math.Max(value, 1000));
        }

        private bool enableCountdownConfirm = true;
        public bool EnableCountdownConfirm
        {
            get => enableCountdownConfirm;
            set => this.RaiseAndSetIfChanged(ref enableCountdownConfirm, value);
        }

        public ReactiveCommand<Unit, AppSettings> Confirm { get; protected set; }
        public ReactiveCommand<Unit, Unit> Cancel { get; protected set; }
    }
}
