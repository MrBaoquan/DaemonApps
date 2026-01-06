using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DaemonKit.PowerSaving
{
    /// <summary>
    /// 省电模式入口，封装亮度发现、应用与恢复。
    /// </summary>
    public sealed class PowerSavingManager
    {
        private readonly BrightnessCoordinator _coordinator;
        private readonly Dictionary<string, BrightnessInfo> _baseline =
            new(StringComparer.OrdinalIgnoreCase);

        public PowerSavingManager(BrightnessCoordinator? coordinator = null)
        {
            _coordinator = coordinator ?? new BrightnessCoordinator();
        }

        internal BrightnessCoordinator Coordinator => _coordinator;

        /// <summary>
        /// 枚举所有可用显示设备。
        /// </summary>
        public Task<IReadOnlyList<DisplayIdentity>> DiscoverDisplaysAsync(
            CancellationToken cancellationToken = default
        )
        {
            return _coordinator.DiscoverDisplaysAsync(cancellationToken);
        }

        /// <summary>
        /// 应用省电亮度配置并记录原亮度，便于恢复。
        /// </summary>
        public async Task<PowerSavingResult> ApplyAsync(
            PowerSavingProfile profile,
            CancellationToken cancellationToken = default
        )
        {
            var displays = await _coordinator
                .DiscoverDisplaysAsync(cancellationToken)
                .ConfigureAwait(false);
            var results = new List<DisplayBrightnessResult>();

            foreach (var display in displays)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = await _coordinator
                    .GetBrightnessAsync(display, cancellationToken)
                    .ConfigureAwait(false);
                if (current != null)
                {
                    _baseline[display.DevicePath] = current;
                }

                var target = profile.ResolveTarget(display);
                var ok = await _coordinator
                    .SetBrightnessAsync(display, target, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(new DisplayBrightnessResult(display, ok, ok ? null : "DDC/CI 调节失败"));
            }

            return new PowerSavingResult(new ReadOnlyCollection<DisplayBrightnessResult>(results));
        }

        /// <summary>
        /// 尝试恢复到应用省电前记录的亮度。
        /// </summary>
        public async Task<PowerSavingResult> RestoreAsync(
            CancellationToken cancellationToken = default
        )
        {
            var displays = await _coordinator
                .DiscoverDisplaysAsync(cancellationToken)
                .ConfigureAwait(false);
            var results = new List<DisplayBrightnessResult>();

            foreach (var display in displays)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_baseline.TryGetValue(display.DevicePath, out var original))
                {
                    var ok = await _coordinator
                        .SetBrightnessAsync(display, original.Current, cancellationToken)
                        .ConfigureAwait(false);
                    results.Add(new DisplayBrightnessResult(display, ok, ok ? null : "恢复亮度失败"));
                }
                else
                {
                    results.Add(new DisplayBrightnessResult(display, false, "未记录基线亮度"));
                }
            }

            return new PowerSavingResult(new ReadOnlyCollection<DisplayBrightnessResult>(results));
        }
    }
}
