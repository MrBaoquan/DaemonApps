using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DNHper;

namespace DaemonKit.PowerSaving
{
    /// <summary>
    /// 从注册表EDID解析显示器友好名称（制造商 + 型号）
    /// </summary>
    public static class MonitorNameResolver
    {
        /// <summary>
        /// 获取显示器的友好名称（如 "PHL 271V8"）
        /// </summary>
        /// <param name="devicePath">设备路径，如 \\?\DISPLAY#PHLC21B#...</param>
        /// <returns>友好名称，失败时返回null</returns>
        public static string? GetFriendlyName(string devicePath)
        {
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                return null;
            }

            try
            {
                // 从设备路径提取硬件ID
                // 例如: \\?\DISPLAY#PHLC21B#5&2a8b5bd2&0&UID4352#{...}
                // 提取: PHLC21B
                var match = Regex.Match(devicePath, @"DISPLAY#([^#]+)#", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    NLogger.Debug("[MonitorName] 无法从路径提取硬件ID: {DevicePath}", devicePath);
                    return null;
                }

                var hardwareId = match.Groups[1].Value;
                NLogger.Debug("[MonitorName] 硬件ID: {HardwareId}", hardwareId);

                // 在注册表中查找对应的EDID
                // HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareId}
                var displayKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareId}"
                );

                if (displayKey == null)
                {
                    NLogger.Debug("[MonitorName] 未找到注册表键: DISPLAY\\{HardwareId}", hardwareId);
                    return null;
                }

                // 遍历子键（通常是UID编号）
                foreach (var subKeyName in displayKey.GetSubKeyNames())
                {
                    using var subKey = displayKey.OpenSubKey(subKeyName);
                    if (subKey == null)
                        continue;

                    // 读取Device Parameters中的EDID
                    using var deviceParamsKey = subKey.OpenSubKey("Device Parameters");
                    if (deviceParamsKey == null)
                        continue;

                    var edidData = deviceParamsKey.GetValue("EDID") as byte[];
                    if (edidData == null || edidData.Length < 128)
                        continue;

                    NLogger.Debug("[MonitorName] 成功读取EDID，长度: {Length} 字节", edidData.Length);

                    // 解析EDID获取显示器名称
                    var name = ParseEdidMonitorName(edidData);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        NLogger.Info("[MonitorName] 成功解析显示器名称: {Name}", name);
                        return name;
                    }
                }

                displayKey.Close();
            }
            catch (Exception ex)
            {
                NLogger.Error("[MonitorName] 解析失败: {ErrorMessage}", ex.Message);
            }

            return null;
        }

        /// <summary>
        /// 从EDID数据解析显示器名称
        /// EDID结构参考: https://en.wikipedia.org/wiki/Extended_Display_Identification_Data
        /// </summary>
        private static string? ParseEdidMonitorName(byte[] edid)
        {
            if (edid == null || edid.Length < 128)
            {
                return null;
            }

            try
            {
                // 制造商ID位于字节8-9 (Big-endian)
                var manufacturerId = ParseManufacturerId(edid);

                // 显示器名称在描述符块中（字节54-125）
                // EDID有4个18字节的描述符块，从字节54开始
                var monitorName = string.Empty;

                for (int i = 0; i < 4; i++)
                {
                    int offset = 54 + i * 18;
                    if (offset + 18 > edid.Length)
                        break;

                    // 检查描述符类型（前5个字节为00 00 00 FC 00表示显示器名称）
                    if (
                        edid[offset] == 0x00
                        && edid[offset + 1] == 0x00
                        && edid[offset + 2] == 0x00
                        && edid[offset + 3] == 0xFC
                        && edid[offset + 4] == 0x00
                    )
                    {
                        // 接下来13个字节是ASCII字符串
                        var nameBytes = edid.Skip(offset + 5)
                            .Take(13)
                            .TakeWhile(b => b != 0x0A && b != 0x00)
                            .ToArray();
                        monitorName = Encoding.ASCII.GetString(nameBytes).Trim();
                        NLogger.Debug("[MonitorName] EDID监视器名称: {MonitorName}", monitorName);
                        break;
                    }
                }

                // 如果找到了型号名称，组合制造商ID和型号
                if (!string.IsNullOrWhiteSpace(monitorName))
                {
                    // 如果名称已包含制造商信息，直接返回
                    if (monitorName.Length > 0)
                    {
                        return monitorName;
                    }

                    // 否则组合: 制造商 + 型号
                    if (!string.IsNullOrWhiteSpace(manufacturerId))
                    {
                        return $"{manufacturerId} {monitorName}";
                    }

                    return monitorName;
                }

                // 如果没有找到型号名称，只返回制造商ID
                return manufacturerId;
            }
            catch (Exception ex)
            {
                NLogger.Error("[MonitorName] EDID解析失败: {ErrorMessage}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 解析EDID中的制造商ID（3字母代码）
        /// 位于字节8-9，使用压缩编码
        /// </summary>
        private static string? ParseManufacturerId(byte[] edid)
        {
            if (edid.Length < 10)
                return null;

            try
            {
                // 制造商ID是16位，编码为3个5位字符（A-Z）
                ushort id = (ushort)((edid[8] << 8) | edid[9]);

                // 提取3个字符（每个5位）
                char c1 = (char)('A' + ((id >> 10) & 0x1F) - 1);
                char c2 = (char)('A' + ((id >> 5) & 0x1F) - 1);
                char c3 = (char)('A' + (id & 0x1F) - 1);

                var manufacturerId = new string(new[] { c1, c2, c3 });
                NLogger.Debug("[MonitorName] EDID制造商ID: {ManufacturerId}", manufacturerId);
                return manufacturerId;
            }
            catch (Exception ex)
            {
                NLogger.Error("[MonitorName] 制造商ID解析失败: {ErrorMessage}", ex.Message);
                return null;
            }
        }
    }
}
