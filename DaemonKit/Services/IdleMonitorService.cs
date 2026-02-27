using System;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using DaemonKit.Models;
using DNHper;
using ReactiveUI;

namespace DaemonKit.Services
{
    /// <summary>
    /// 空闲监控服务 - 负责检测用户空闲状态并触发相应操作
    /// </summary>
    public class IdleMonitorService : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        private readonly PowerSavingService _powerSavingService;
        private readonly AppSettings _appSettings;
        private IDisposable? _monitorSubscription;
        private bool _idleActionTriggered;
        private bool _idleAutoPowerSavingTriggered;

        public IdleMonitorService(PowerSavingService powerSavingService, AppSettings appSettings)
        {
            _powerSavingService =
                powerSavingService ?? throw new ArgumentNullException(nameof(powerSavingService));
            _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        }

        /// <summary>
        /// 启动空闲监控
        /// </summary>
        public void StartMonitoring()
        {
            _monitorSubscription?.Dispose();

            _monitorSubscription = Observable
                .Interval(TimeSpan.FromSeconds(10))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => CheckIdleState());

            NLogger.Info("[IdleMonitor] 空闲监控已启动");
        }

        /// <summary>
        /// 停止空闲监控
        /// </summary>
        public void StopMonitoring()
        {
            _monitorSubscription?.Dispose();
            _monitorSubscription = null;
            NLogger.Info("[IdleMonitor] 空闲监控已停止");
        }

        /// <summary>
        /// 检查空闲状态
        /// </summary>
        private async void CheckIdleState()
        {
            try
            {
                var idleDuration = GetIdleDuration();

                // 处理空闲自动关闭桌面
                if (_appSettings.EnableIdleAutoAction)
                {
                    var threshold = TimeSpan.FromMinutes(
                        Math.Max(1, _appSettings.IdleAutoActionThresholdMinutes)
                    );

                    if (idleDuration >= threshold)
                    {
                        if (!_idleActionTriggered)
                        {
                            HandleIdleTimeout();
                            _idleActionTriggered = true;
                        }
                    }
                    else
                    {
                        _idleActionTriggered = false;
                    }
                }
                else
                {
                    _idleActionTriggered = false;
                }

                // 处理空闲自动省电
                if (_appSettings.EnableIdleAutoPowerSaving)
                {
                    var powerSavingThreshold = TimeSpan.FromMinutes(
                        Math.Max(1, _appSettings.IdleAutoPowerSavingThresholdMinutes)
                    );

                    if (idleDuration >= powerSavingThreshold)
                    {
                        if (
                            !_idleAutoPowerSavingTriggered && !_powerSavingService.IsPowerSavingMode
                        )
                        {
                            // 进入省电模式
                            await _powerSavingService.ApplyPowerSavingAsync();
                            _idleAutoPowerSavingTriggered = true;
                            NLogger.Info(
                                $"[IdleMonitor] 空闲{powerSavingThreshold.TotalMinutes}分钟，自动进入省电模式"
                            );
                        }
                    }
                    else
                    {
                        if (_idleAutoPowerSavingTriggered && _powerSavingService.IsPowerSavingMode)
                        {
                            // 检测到用户活动，退出省电模式
                            await _powerSavingService.RestoreNormalAsync();
                            NLogger.Info("[IdleMonitor] 检测到用户活动，退出省电模式");
                        }
                        _idleAutoPowerSavingTriggered = false;
                    }
                }
                else
                {
                    _idleAutoPowerSavingTriggered = false;
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("[IdleMonitor] 检查空闲状态异常: {ErrorMessage}", ex.Message);
            }
        }

        /// <summary>
        /// 获取空闲时长
        /// </summary>
        private TimeSpan GetIdleDuration()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            var lastInputTick = info.dwTime;
            var currentTick = (uint)Environment.TickCount;
            var idleMilliseconds =
                currentTick >= lastInputTick
                    ? currentTick - lastInputTick
                    : uint.MaxValue - lastInputTick + currentTick + 1;

            return TimeSpan.FromMilliseconds(idleMilliseconds);
        }

        /// <summary>
        /// 处理空闲超时（关闭桌面）
        /// </summary>
        private void HandleIdleTimeout()
        {
            try
            {
                var desktopRunning =
                    System.Diagnostics.Process.GetProcessesByName("explorer").Length > 0;
                if (desktopRunning)
                {
                    DNHper.WinAPI.OpenProcess("taskkill.exe", "/f /im explorer.exe");
                    NLogger.Info("[IdleMonitor] 空闲超时，已关闭桌面进程");
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("[IdleMonitor] 执行空闲超时操作失败: {ErrorMessage}", ex.Message);
            }
        }

        public void Dispose()
        {
            StopMonitoring();
        }
    }
}
