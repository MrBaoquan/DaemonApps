using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using ReactiveUI;

namespace DaemonKit
{
    public class MachineInfo
    {
        public string ID = string.Empty;
        public string Name { get; set; }
        public ObservableCollection<string> GPUs { get; set; }
        public ObservableCollection<string> CPUs { get; set; }
        public ObservableCollection<string> IPs { get; set; }
        public ObservableCollection<string> Memories { get; set; }

        /// <summary>
        /// 获取硬件信息摘要（CPU/GPU/内存合并显示）
        /// </summary>
        public virtual string HardwareInfo
        {
            get
            {
                var parts = new List<string>();

                if (CPUs != null && CPUs.Count > 0)
                    parts.Add($"CPU: {string.Join(", ", CPUs)}");

                if (GPUs != null && GPUs.Count > 0)
                    parts.Add($"GPU: {string.Join(", ", GPUs)}");

                if (Memories != null && Memories.Count > 0)
                    parts.Add($"内存: {string.Join(", ", Memories)}");

                return parts.Count > 0 ? string.Join(" | ", parts) : "未知";
            }
        }

        /// <summary>
        /// 获取硬件信息简要摘要（用于列表展示，仅显示关键型号信息）
        /// </summary>
        public virtual string HardwareInfoSummary
        {
            get
            {
                var parts = new List<string>();

                if (CPUs != null && CPUs.Count > 0)
                    parts.Add(ExtractCpuModel(CPUs[0]));

                if (GPUs != null && GPUs.Count > 0)
                    parts.Add(ExtractGpuModel(GPUs[0]));

                if (Memories != null && Memories.Count > 0)
                    parts.Add(ExtractMemorySize(Memories));

                return parts.Count > 0 ? string.Join(" | ", parts) : "未知";
            }
        }

        /// <summary>提取CPU关键型号（如 "I9-14900K"、"R9 7950X"）</summary>
        private static string ExtractCpuModel(string cpuFullName)
        {
            if (string.IsNullOrWhiteSpace(cpuFullName))
                return "CPU: ?";

            // 匹配 Intel: i9-14900K, i7-13700K 等
            var intelMatch = System.Text.RegularExpressions.Regex.Match(
                cpuFullName,
                @"(i[3579])-?(\d{4,5}\w*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            if (intelMatch.Success)
                return intelMatch.Value.ToUpper();

            // 匹配 AMD: R9 7950X, R7 5800X 等
            var amdMatch = System.Text.RegularExpressions.Regex.Match(
                cpuFullName,
                @"(R[3579])\s*(\d{4}\w*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            if (amdMatch.Success)
                return amdMatch.Value.ToUpper();

            // 无法识别时截取前20字符
            return cpuFullName.Length > 20 ? cpuFullName.Substring(0, 20) + "…" : cpuFullName;
        }

        /// <summary>提取GPU关键型号（如 "RTX 4080 Super"、"RX 7900 XTX"）</summary>
        private static string ExtractGpuModel(string gpuFullName)
        {
            if (string.IsNullOrWhiteSpace(gpuFullName))
                return "GPU: ?";

            // 匹配 NVIDIA: RTX 4080 Super, GTX 1660 Ti 等
            var nvidiaMatch = System.Text.RegularExpressions.Regex.Match(
                gpuFullName,
                @"(RTX|GTX)\s*\d{3,4}(\s*(Super|Ti|SUPER|TI))?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            if (nvidiaMatch.Success)
                return nvidiaMatch.Value.ToUpper();

            // 匹配 AMD: RX 7900 XTX, RX 6800 XT 等
            var amdMatch = System.Text.RegularExpressions.Regex.Match(
                gpuFullName,
                @"RX\s*\d{4}(\s*(XTX|XT|X))?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            if (amdMatch.Success)
                return amdMatch.Value.ToUpper();

            // 无法识别时截取前20字符
            return gpuFullName.Length > 20 ? gpuFullName.Substring(0, 20) + "…" : gpuFullName;
        }

        /// <summary>提取内存总容量（如 "32G"、"64G"）</summary>
        private static string ExtractMemorySize(ObservableCollection<string> memories)
        {
            long totalMB = 0;
            foreach (var mem in memories)
            {
                // 尝试提取数字 + 单位（GB/MB/G/M）
                var match = System.Text.RegularExpressions.Regex.Match(
                    mem,
                    @"(\d+\.?\d*)\s*(GB|G|MB|M)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                if (match.Success)
                {
                    var val = double.Parse(match.Groups[1].Value);
                    var unit = match.Groups[2].Value.ToUpper();
                    totalMB += (long)(unit.StartsWith("G") ? val * 1024 : val);
                }
                else if (long.TryParse(mem.Trim(), out var raw))
                {
                    // 纯数字，猜测为MB
                    totalMB += raw;
                }
            }

            if (totalMB <= 0)
                return string.Join("+", memories);

            var totalGB = totalMB / 1024.0;
            return totalGB >= 1 ? $"{(int)System.Math.Round(totalGB)}G" : $"{totalMB}M";
        }
    }

    public class DaemonTableViewModel : ReactiveObject
    {
        public ObservableCollection<MachineInfo> Machines { get; set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryConnectCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryShutdownCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryRestartCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryBootCommand { get; protected set; }
        public ReactiveCommand<MachineInfo, MachineInfo> TryRestartNodeTree { get; protected set; }

        public DaemonTableViewModel()
        {
            Machines = new ObservableCollection<MachineInfo>();
            TryConnectCommand = ReactiveCommand.Create<MachineInfo, MachineInfo>(
                _machine =>
                {
                    _machine.Name = "连接中...";
                    return _machine;
                },
                outputScheduler: RxApp.MainThreadScheduler
            );
            TryShutdownCommand = ReactiveCommand.Create<MachineInfo, MachineInfo>(
                _machine =>
                {
                    _machine.Name = "关机中...";
                    return _machine;
                },
                outputScheduler: RxApp.MainThreadScheduler
            );
            TryRestartCommand = ReactiveCommand.Create<MachineInfo, MachineInfo>(
                _machine =>
                {
                    _machine.Name = "重启中...";
                    return _machine;
                },
                outputScheduler: RxApp.MainThreadScheduler
            );
            TryBootCommand = ReactiveCommand.Create<MachineInfo, MachineInfo>(
                _machine =>
                {
                    _machine.Name = "开机中...";
                    return _machine;
                },
                outputScheduler: RxApp.MainThreadScheduler
            );

            TryRestartNodeTree = ReactiveCommand.Create<MachineInfo, MachineInfo>(
                _machine =>
                {
                    return _machine;
                },
                outputScheduler: RxApp.MainThreadScheduler
            );
        }

        public void AddMachine(MachineInfo machine)
        {
            var _machineInfo = Machines.Where(m => m.ID == machine.ID).FirstOrDefault();
            if (_machineInfo == default(MachineInfo))
            {
                Machines.Add(machine);
            }
            else
            {
                _machineInfo.Name = machine.Name;
                _machineInfo.CPUs = machine.CPUs;
                _machineInfo.GPUs = machine.GPUs;
                _machineInfo.IPs = machine.IPs;
                _machineInfo.Memories = machine.Memories;
            }
        }
    }
}
