using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DaemonKit.Models;
using DaemonKit.Utilities;
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
        public bool EnableToggleWindow { get; set; } = true;
        public bool EnableStartTree { get; set; } = true;
        public bool EnableStopTree { get; set; } = true;
        public bool EnableDesktopOn { get; set; } = true;
        public bool EnableDesktopOff { get; set; } = true;
        public bool EnableScreenshot { get; set; } = true;
        public bool EnableScheduleToggleHotKey { get; set; } = true;
        public int StartUpDelay { get; set; } = 0;
        public int DelayDaemon { get; set; } = 500;
        public int DaemonInterval { get; set; } = 5000;
        public int ErrorCount { get; set; } = 5;
        public string CrashWindows { get; set; } = string.Empty;
        public bool SafeKillProcess { get; set; } = false;
        public int SafeKillTimeout { get; set; } = 5000;
        public bool EnableCpuStallDetection { get; set; } = false;
        public bool DisableTouchScreen { get; set; } = false;
        public bool EnableCountdownConfirm { get; set; } = true; // 启用重启/关机倒计时确认
        public bool EnableIdleAutoAction { get; set; } = false;
        public int IdleAutoActionThresholdMinutes { get; set; } = 5;
        public bool EnableIdleAutoPowerSaving { get; set; } = false; // 启用空闲自动省电
        public int IdleAutoPowerSavingThresholdMinutes { get; set; } = 5; // 空闲省电阈值(分钟)

        // 传输设置
        public int MaxConcurrentTransfers { get; set; } = 4;

        // 系统状态监控设置
        public bool EnableSystemStatusMonitoring { get; set; } = true;
        public int SystemStatusIntervalMs { get; set; } = 2000;
        public bool EnableGpuUsageMonitoring { get; set; } = true;
        public int CriticalMemoryUsagePercent { get; set; } = 90;
        public int CriticalCpuUsagePercent { get; set; } = 95;
        public int CriticalGpuUsagePercent { get; set; } = 95;

        // 网络端口设置（0 表示使用默认值）
        public int CustomMetaPort { get; set; } = 0;
        public int CustomControlPort { get; set; } = 0;
        public int CustomFileTransferPort { get; set; } = 0;

        // 认证设置（空字符串表示不启用认证）
        public string AuthToken { get; set; } = string.Empty;

        // 节能模式设置
        public bool PowerSavingModeEnabled { get; set; } = false;
        public byte PowerSavingNormalBrightness { get; set; } = 100;
        public byte PowerSavingLowBrightness { get; set; } = 56;
        public List<DisplayConfig> PowerSavingDisplayConfigs { get; set; } = new();

        /// <summary>
        /// 前台窗口焦点挂起快捷键：当前台进程名在此列表时自动注销全局快捷键，
        /// 离开后自动重新注册，解决向日葵/VNC 远程嵌套场景下快捷键穿透问题。
        /// </summary>
        public List<string> SuspendHotkeyOnProcessNames { get; set; } =
            new List<string>
            {
                "vncviewer",
                "tvnviewer",
                "SunloginClient",
                "SunloginRemote",
                "mstsc"
            };
    }

    public class DisplayConfig
    {
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// 显示器索引，用于区分同一型号的多个显示器
        /// </summary>
        public int DisplayIndex { get; set; } = -1;
        public bool OverrideEnabled { get; set; }
        public byte TargetBrightness { get; set; }
        public string Protocol { get; set; } = "Auto"; // 协议类型持久化
        public string SerialPort { get; set; } = "COM1";
        public int SerialBaudRate { get; set; } = 115200;
        public string TcpAddress { get; set; } = "192.168.1.100";
        public int TcpPort { get; set; } = 18100;
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
                        EnableToggleWindow = EnableToggleWindow,
                        EnableStartTree = EnableStartTree,
                        EnableStopTree = EnableStopTree,
                        EnableDesktopOn = EnableDesktopOn,
                        EnableDesktopOff = EnableDesktopOff,
                        EnableScreenshot = EnableScreenshot,
                        EnableScheduleToggleHotKey = EnableScheduleToggleHotKey,
                        MinimizeStartUp = MinimizeStartUp,
                        DisableExplorer = DisableExplorer,
                        StartUpDelay = StartUpDelay,
                        DelayDaemon = DelayDaemon,
                        DaemonInterval = DaemonInterval,
                        ErrorCount = ErrorCount,
                        CrashWindows = CrashWindows,
                        SafeKillProcess = SafeKillProcess,
                        SafeKillTimeout = SafeKillTimeout,
                        EnableCpuStallDetection = EnableCpuStallDetection,
                        DisableTouchScreen = DisableTouchScreen,
                        EnableCountdownConfirm = EnableCountdownConfirm,
                        EnableIdleAutoAction = EnableIdleAutoAction,
                        IdleAutoActionThresholdMinutes = IdleAutoActionThresholdMinutes,
                        EnableIdleAutoPowerSaving = EnableIdleAutoPowerSaving,
                        IdleAutoPowerSavingThresholdMinutes = IdleAutoPowerSavingThresholdMinutes,
                        MaxConcurrentTransfers = MaxConcurrentTransfers,
                        EnableSystemStatusMonitoring = EnableSystemStatusMonitoring,
                        SystemStatusIntervalMs = SystemStatusIntervalMs,
                        EnableGpuUsageMonitoring = EnableGpuUsageMonitoring,
                        CriticalMemoryUsagePercent = CriticalMemoryUsagePercent,
                        CriticalCpuUsagePercent = CriticalCpuUsagePercent,
                        CriticalGpuUsagePercent = CriticalGpuUsagePercent,
                        CustomMetaPort = CustomMetaPort,
                        CustomControlPort = CustomControlPort,
                        CustomFileTransferPort = CustomFileTransferPort,
                        AuthToken = AuthToken,
                        PowerSavingModeEnabled = PowerSavingModeEnabled,
                        PowerSavingNormalBrightness = PowerSavingNormalBrightness,
                        PowerSavingLowBrightness = PowerSavingLowBrightness,
                        PowerSavingDisplayConfigs = PowerSavingDisplayConfigs,
                        SuspendHotkeyOnProcessNames = SuspendHotkeyOnProcessNames
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
            EnableToggleWindow = settings.EnableToggleWindow;
            EnableStartTree = settings.EnableStartTree;
            EnableStopTree = settings.EnableStopTree;
            EnableDesktopOn = settings.EnableDesktopOn;
            EnableDesktopOff = settings.EnableDesktopOff;
            EnableScreenshot = settings.EnableScreenshot;
            EnableScheduleToggleHotKey = settings.EnableScheduleToggleHotKey;
            MinimizeStartUp = settings.MinimizeStartUp;
            DisableExplorer = settings.DisableExplorer;
            StartUpDelay = settings.StartUpDelay;
            DelayDaemon = settings.DelayDaemon;
            DaemonInterval = settings.DaemonInterval;
            ErrorCount = settings.ErrorCount;
            CrashWindows = settings.CrashWindows;
            SafeKillProcess = settings.SafeKillProcess;
            SafeKillTimeout = settings.SafeKillTimeout;
            EnableCpuStallDetection = settings.EnableCpuStallDetection;
            DisableTouchScreen = settings.DisableTouchScreen;
            EnableCountdownConfirm = settings.EnableCountdownConfirm;
            EnableIdleAutoAction = settings.EnableIdleAutoAction;
            IdleAutoActionThresholdMinutes = settings.IdleAutoActionThresholdMinutes;
            EnableIdleAutoPowerSaving = settings.EnableIdleAutoPowerSaving;
            IdleAutoPowerSavingThresholdMinutes = settings.IdleAutoPowerSavingThresholdMinutes;
            MaxConcurrentTransfers = settings.MaxConcurrentTransfers;
            EnableSystemStatusMonitoring = settings.EnableSystemStatusMonitoring;
            SystemStatusIntervalMs = settings.SystemStatusIntervalMs;
            EnableGpuUsageMonitoring = settings.EnableGpuUsageMonitoring;
            CriticalMemoryUsagePercent = settings.CriticalMemoryUsagePercent;
            CriticalCpuUsagePercent = settings.CriticalCpuUsagePercent;
            CriticalGpuUsagePercent = settings.CriticalGpuUsagePercent;
            CustomMetaPort = settings.CustomMetaPort;
            CustomControlPort = settings.CustomControlPort;
            CustomFileTransferPort = settings.CustomFileTransferPort;
            AuthToken = settings.AuthToken;
            PowerSavingModeEnabled = settings.PowerSavingModeEnabled;
            PowerSavingNormalBrightness = settings.PowerSavingNormalBrightness;
            PowerSavingLowBrightness = settings.PowerSavingLowBrightness;
            PowerSavingDisplayConfigs = settings.PowerSavingDisplayConfigs;
            SuspendHotkeyOnProcessNames =
                settings.SuspendHotkeyOnProcessNames
                ?? new List<string>
                {
                    "vncviewer",
                    "tvnviewer",
                    "SunloginClient",
                    "SunloginRemote",
                    "mstsc"
                };
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

        private bool enableToggleWindow = true;
        public bool EnableToggleWindow
        {
            get => enableToggleWindow;
            set => this.RaiseAndSetIfChanged(ref enableToggleWindow, value);
        }

        private bool enableStartTree = true;
        public bool EnableStartTree
        {
            get => enableStartTree;
            set => this.RaiseAndSetIfChanged(ref enableStartTree, value);
        }

        private bool enableStopTree = true;
        public bool EnableStopTree
        {
            get => enableStopTree;
            set => this.RaiseAndSetIfChanged(ref enableStopTree, value);
        }

        private bool enableDesktopOn = true;
        public bool EnableDesktopOn
        {
            get => enableDesktopOn;
            set => this.RaiseAndSetIfChanged(ref enableDesktopOn, value);
        }

        private bool enableDesktopOff = true;
        public bool EnableDesktopOff
        {
            get => enableDesktopOff;
            set => this.RaiseAndSetIfChanged(ref enableDesktopOff, value);
        }

        private bool enableScreenshot = true;
        public bool EnableScreenshot
        {
            get => enableScreenshot;
            set => this.RaiseAndSetIfChanged(ref enableScreenshot, value);
        }

        private bool enableScheduleToggleHotKey = true;
        public bool EnableScheduleToggleHotKey
        {
            get => enableScheduleToggleHotKey;
            set => this.RaiseAndSetIfChanged(ref enableScheduleToggleHotKey, value);
        }

        /// <summary>
        /// 直通字段：不在通用设置对话框编辑，由快捷键设置对话框专门管理。
        /// 保存时透传以防止被通用设置覆盖丢失。
        /// </summary>
        public List<string> SuspendHotkeyOnProcessNames { get; set; } =
            new List<string>
            {
                "vncviewer",
                "tvnviewer",
                "SunloginClient",
                "SunloginRemote",
                "mstsc"
            };

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

        private bool enableIdleAutoAction = false;
        public bool EnableIdleAutoAction
        {
            get => enableIdleAutoAction;
            set => this.RaiseAndSetIfChanged(ref enableIdleAutoAction, value);
        }

        private int idleAutoActionThresholdMinutes = 5;
        public int IdleAutoActionThresholdMinutes
        {
            get => idleAutoActionThresholdMinutes;
            set =>
                this.RaiseAndSetIfChanged(ref idleAutoActionThresholdMinutes, Math.Max(value, 1));
        }

        private bool enableIdleAutoPowerSaving = false;
        public bool EnableIdleAutoPowerSaving
        {
            get => enableIdleAutoPowerSaving;
            set => this.RaiseAndSetIfChanged(ref enableIdleAutoPowerSaving, value);
        }

        private int idleAutoPowerSavingThresholdMinutes = 5;
        public int IdleAutoPowerSavingThresholdMinutes
        {
            get => idleAutoPowerSavingThresholdMinutes;
            set =>
                this.RaiseAndSetIfChanged(
                    ref idleAutoPowerSavingThresholdMinutes,
                    Math.Max(value, 1)
                );
        }

        private int maxConcurrentTransfers = 4;
        public int MaxConcurrentTransfers
        {
            get => maxConcurrentTransfers;
            set => this.RaiseAndSetIfChanged(ref maxConcurrentTransfers, Math.Clamp(value, 1, 16));
        }

        private bool enableSystemStatusMonitoring = true;
        public bool EnableSystemStatusMonitoring
        {
            get => enableSystemStatusMonitoring;
            set => this.RaiseAndSetIfChanged(ref enableSystemStatusMonitoring, value);
        }

        private int systemStatusIntervalMs = 2000;
        public int SystemStatusIntervalMs
        {
            get => systemStatusIntervalMs;
            set =>
                this.RaiseAndSetIfChanged(
                    ref systemStatusIntervalMs,
                    Math.Clamp(value, 500, 10000)
                );
        }

        private bool enableGpuUsageMonitoring = true;
        public bool EnableGpuUsageMonitoring
        {
            get => enableGpuUsageMonitoring;
            set => this.RaiseAndSetIfChanged(ref enableGpuUsageMonitoring, value);
        }

        private int criticalMemoryUsagePercent = 90;
        public int CriticalMemoryUsagePercent
        {
            get => criticalMemoryUsagePercent;
            set =>
                this.RaiseAndSetIfChanged(
                    ref criticalMemoryUsagePercent,
                    Math.Clamp(value, 50, 100)
                );
        }

        private int criticalCpuUsagePercent = 95;
        public int CriticalCpuUsagePercent
        {
            get => criticalCpuUsagePercent;
            set =>
                this.RaiseAndSetIfChanged(ref criticalCpuUsagePercent, Math.Clamp(value, 50, 100));
        }

        private int criticalGpuUsagePercent = 95;
        public int CriticalGpuUsagePercent
        {
            get => criticalGpuUsagePercent;
            set =>
                this.RaiseAndSetIfChanged(ref criticalGpuUsagePercent, Math.Clamp(value, 50, 100));
        }

        private int customMetaPort = 0;
        public int CustomMetaPort
        {
            get => customMetaPort;
            set => this.RaiseAndSetIfChanged(ref customMetaPort, Math.Max(value, 0));
        }

        private int customControlPort = 0;
        public int CustomControlPort
        {
            get => customControlPort;
            set => this.RaiseAndSetIfChanged(ref customControlPort, Math.Max(value, 0));
        }

        private int customFileTransferPort = 0;
        public int CustomFileTransferPort
        {
            get => customFileTransferPort;
            set => this.RaiseAndSetIfChanged(ref customFileTransferPort, Math.Max(value, 0));
        }

        private string authToken = string.Empty;
        public string AuthToken
        {
            get => authToken;
            set => this.RaiseAndSetIfChanged(ref authToken, value ?? string.Empty);
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

        private bool enableCpuStallDetection = false;
        public bool EnableCpuStallDetection
        {
            get => enableCpuStallDetection;
            set => this.RaiseAndSetIfChanged(ref enableCpuStallDetection, value);
        }

        private bool enableCountdownConfirm = true;
        public bool EnableCountdownConfirm
        {
            get => enableCountdownConfirm;
            set => this.RaiseAndSetIfChanged(ref enableCountdownConfirm, value);
        }

        private bool powerSavingModeEnabled = false;
        public bool PowerSavingModeEnabled
        {
            get => powerSavingModeEnabled;
            set => this.RaiseAndSetIfChanged(ref powerSavingModeEnabled, value);
        }

        private byte powerSavingNormalBrightness = 100;
        public byte PowerSavingNormalBrightness
        {
            get => powerSavingNormalBrightness;
            set => this.RaiseAndSetIfChanged(ref powerSavingNormalBrightness, value);
        }

        private byte powerSavingLowBrightness = 56;
        public byte PowerSavingLowBrightness
        {
            get => powerSavingLowBrightness;
            set => this.RaiseAndSetIfChanged(ref powerSavingLowBrightness, value);
        }

        private List<DisplayConfig> powerSavingDisplayConfigs = new();
        public List<DisplayConfig> PowerSavingDisplayConfigs
        {
            get => powerSavingDisplayConfigs;
            set => this.RaiseAndSetIfChanged(ref powerSavingDisplayConfigs, value);
        }

        private bool ledEnabled = false;
        public bool LedEnabled
        {
            get => ledEnabled;
            set => this.RaiseAndSetIfChanged(ref ledEnabled, value);
        }

        private string ledConnectionType = "Serial";
        public string LedConnectionType
        {
            get => ledConnectionType;
            set => this.RaiseAndSetIfChanged(ref ledConnectionType, value);
        }

        private string ledSerialPort = "COM1";
        public string LedSerialPort
        {
            get => ledSerialPort;
            set => this.RaiseAndSetIfChanged(ref ledSerialPort, value);
        }

        private int ledBaudRate = 115200;
        public int LedBaudRate
        {
            get => ledBaudRate;
            set => this.RaiseAndSetIfChanged(ref ledBaudRate, Math.Max(value, 9600));
        }

        private string ledIpAddress = "192.168.1.100";
        public string LedIpAddress
        {
            get => ledIpAddress;
            set => this.RaiseAndSetIfChanged(ref ledIpAddress, value);
        }

        private int ledTcpPort = 18100;
        public int LedTcpPort
        {
            get => ledTcpPort;
            set => this.RaiseAndSetIfChanged(ref ledTcpPort, Math.Max(value, 1));
        }

        public ReactiveCommand<Unit, AppSettings> Confirm { get; protected set; }
        public ReactiveCommand<Unit, Unit> Cancel { get; protected set; }
    }
}
