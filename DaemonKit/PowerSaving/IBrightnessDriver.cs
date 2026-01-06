using System.Threading;
using System.Threading.Tasks;

namespace DaemonKit.PowerSaving
{
    /// <summary>
    /// 亮度控制驱动接口，便于未来扩展不同品牌/协议。
    /// </summary>
    public interface IBrightnessDriver
    {
        /// <summary>
        /// 判断当前驱动是否可以处理指定的显示设备。
        /// </summary>
        bool CanHandle(DisplayIdentity display);

        /// <summary>
        /// 获取亮度信息。
        /// </summary>
        Task<BrightnessInfo?> GetBrightnessAsync(
            DisplayIdentity display,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 设置亮度（0-100）。
        /// </summary>
        Task<bool> SetBrightnessAsync(
            DisplayIdentity display,
            byte brightness,
            CancellationToken cancellationToken = default
        );
    }
}
