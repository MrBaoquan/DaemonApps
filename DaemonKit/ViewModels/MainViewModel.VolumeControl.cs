using System;
using ReactiveUI;
using DNHper;

namespace DaemonKit
{
    /// <summary>
    /// MainViewModel 的音量控制部分
    /// </summary>
    public partial class MainViewModel
    {
        // ==================== 音量控制相关属性 ====================

        private double _systemVolume = 50;

        /// <summary>
        /// 系统音量（0-100）
        /// </summary>
        public double SystemVolume
        {
            get => _systemVolume;
            set
            {
                this.RaiseAndSetIfChanged(ref _systemVolume, value);
                SetSystemVolume(value);

                // 更新静音状态
                var muteState = WinAPI.GetMute();
                if (muteState.HasValue)
                {
                    IsMuted = muteState.Value;
                }
            }
        }

        private bool _isMuted = false;

        /// <summary>
        /// 是否静音
        /// </summary>
        public bool IsMuted
        {
            get => _isMuted;
            set => this.RaiseAndSetIfChanged(ref _isMuted, value);
        }

        // ==================== 音量控制初始化 ====================

        /// <summary>
        /// 初始化音量控制（在 MainViewModel 构造函数中调用）
        /// </summary>
        private void InitializeVolumeControl()
        {
            try
            {
                // 获取当前系统音量
                double currentVolume = GetSystemVolume();
                if (currentVolume >= 0)
                {
                    _systemVolume = currentVolume;
                    this.RaisePropertyChanged(nameof(SystemVolume));
                }

                // 获取静音状态
                var muteState = WinAPI.GetMute();
                if (muteState.HasValue)
                {
                    IsMuted = muteState.Value;
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("音量控制初始化失败: {Message}", ex.Message);
            }
        }

        // ==================== Windows API 音量控制 ====================

        /// <summary>
        /// 设置系统音量
        /// </summary>
        /// <param name="volume">音量值（0-100）</param>
        private void SetSystemVolume(double volume)
        {
            try
            {
                // 使用 WinAPI.Audio 接口设置音量
                int volumePercent = (int)Math.Round(volume);
                bool success = WinAPI.SetMasterVolumePercent(volumePercent);

                if (!success)
                {
                    NLogger.Warn("设置系统音量失败: {VolumePercent}%", volumePercent);
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("设置系统音量异常: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 获取系统音量
        /// </summary>
        /// <returns>音量值（0-100），失败返回-1</returns>
        private double GetSystemVolume()
        {
            try
            {
                // 使用 WinAPI.Audio 接口获取音量
                int volumePercent = WinAPI.GetMasterVolumePercent();

                if (volumePercent < 0)
                {
                    NLogger.Warn("获取系统音量失败，返回默认值50");
                    return 50; // 默认返回50%
                }

                return volumePercent;
            }
            catch (Exception ex)
            {
                NLogger.Error("获取系统音量异常: {Message}", ex.Message);
                return 50; // 默认返回50%
            }
        }

        /// <summary>
        /// 切换静音状态
        /// </summary>
        public void ToggleMute()
        {
            try
            {
                bool? newMuteState = WinAPI.ToggleMute();
                if (newMuteState.HasValue)
                {
                    IsMuted = newMuteState.Value;
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("切换静音状态失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 音量步进增加
        /// </summary>
        public void VolumeStepUp()
        {
            try
            {
                if (WinAPI.VolumeStepUp())
                {
                    // 更新UI显示的音量值
                    _systemVolume = GetSystemVolume();
                    this.RaisePropertyChanged(nameof(SystemVolume));
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("音量步进增加失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 音量步进减少
        /// </summary>
        public void VolumeStepDown()
        {
            try
            {
                if (WinAPI.VolumeStepDown())
                {
                    // 更新UI显示的音量值
                    _systemVolume = GetSystemVolume();
                    this.RaisePropertyChanged(nameof(SystemVolume));
                }
            }
            catch (Exception ex)
            {
                NLogger.Error("音量步进减少失败: {Message}", ex.Message);
            }
        }
    }
}
