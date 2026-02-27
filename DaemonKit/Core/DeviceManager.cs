using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DaemonKit.Models;
using DaemonKit.Utilities;
using DNHper;

namespace DaemonKit.Core
{
    /// <summary>
    /// 设备管理器 - 用于启用/禁用硬件设备
    /// </summary>
    public static class DeviceManager
    {
        #region P/Invoke Declarations

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            IntPtr enumerator,
            IntPtr hwndParent,
            uint flags
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet,
            uint memberIndex,
            ref SP_DEVINFO_DATA deviceInfoData
        );

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParams(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            ref SP_PROPCHANGE_PARAMS classInstallParams,
            int classInstallParamsSize
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(
            uint installFunction,
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData
        );

        #endregion

        #region Structures

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER ClassInstallHeader;
            public uint StateChange;
            public uint Scope;
            public uint HwProfile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_CLASSINSTALL_HEADER
        {
            public uint cbSize;
            public uint InstallFunction;
        }

        #endregion

        #region Constants

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_ALLCLASSES = 0x00000004;

        private const uint SPDRP_DEVICEDESC = 0x00000000;
        private const uint SPDRP_HARDWAREID = 0x00000001;

        private const uint DIF_PROPERTYCHANGE = 0x00000012;
        private const uint DICS_ENABLE = 0x00000001;
        private const uint DICS_DISABLE = 0x00000002;
        private const uint DICS_FLAG_GLOBAL = 0x00000001;

        #endregion

        /// <summary>
        /// 检查当前进程是否以管理员身份运行
        /// </summary>
        private static bool IsRunAsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 启用或禁用触摸屏设备
        /// </summary>
        /// <param name="enable">true=启用, false=禁用</param>
        /// <returns>操作是否成功</returns>
        public static bool SetTouchScreenEnabled(bool enable)
        {
            if (!IsRunAsAdministrator())
            {
                NLogger.Error("需要管理员权限才能启用/禁用硬件设备");
                return false;
            }

            try
            {
                bool result = false;
                int successCount = 0;

                // 使用 Guid.Empty 获取所有设备类
                Guid emptyGuid = Guid.Empty;
                IntPtr deviceInfoSet = SetupDiGetClassDevs(
                    ref emptyGuid,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    DIGCF_PRESENT | DIGCF_ALLCLASSES
                );

                if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
                {
                    NLogger.Error("无法获取设备列表");
                    return false;
                }

                try
                {
                    SP_DEVINFO_DATA deviceInfoData = new SP_DEVINFO_DATA();
                    deviceInfoData.cbSize = (uint)Marshal.SizeOf(deviceInfoData);

                    uint index = 0;
                    while (SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfoData))
                    {
                        // 获取设备描述
                        byte[] descBuffer = new byte[512];
                        uint propertyType;
                        uint requiredSize;

                        bool hasDesc = SetupDiGetDeviceRegistryProperty(
                            deviceInfoSet,
                            ref deviceInfoData,
                            SPDRP_DEVICEDESC,
                            out propertyType,
                            descBuffer,
                            (uint)descBuffer.Length,
                            out requiredSize
                        );

                        if (!hasDesc || requiredSize <= 2)
                        {
                            index++;
                            continue;
                        }

                        string deviceDesc = Encoding.Unicode.GetString(
                            descBuffer,
                            0,
                            (int)requiredSize - 2
                        );
                        string descLower = deviceDesc.ToLower();

                        // 严格筛选：只匹配明确的触摸屏设备描述
                        bool isTouchScreen =
                            descLower.Contains("触摸屏")
                            || descLower == "hid-compliant touch screen"
                            || (descLower.Contains("touch screen") && descLower.Contains("hid"));

                        if (isTouchScreen)
                        {
                            // 设置设备状态
                            if (SetDeviceState(deviceInfoSet, ref deviceInfoData, enable))
                            {
                                successCount++;
                                result = true;
                            }
                        }

                        index++;
                    }

                    if (successCount > 0)
                    {
                        NLogger.Info(
                            "触摸屏已{Status}（{SuccessCount} 个设备）",
                            enable ? "启用" : "禁用",
                            successCount
                        );
                    }
                    else
                    {
                        NLogger.Warn("未找到触摸屏设备");
                    }
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(deviceInfoSet);
                }

                return result;
            }
            catch (Exception ex)
            {
                NLogger.Error("设置触摸屏状态异常: {ErrorMessage}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 设置设备启用/禁用状态
        /// </summary>
        private static bool SetDeviceState(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            bool enable
        )
        {
            try
            {
                SP_PROPCHANGE_PARAMS propChangeParams = new SP_PROPCHANGE_PARAMS
                {
                    ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                    {
                        cbSize = (uint)Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER)),
                        InstallFunction = DIF_PROPERTYCHANGE
                    },
                    StateChange = enable ? DICS_ENABLE : DICS_DISABLE,
                    Scope = DICS_FLAG_GLOBAL,
                    HwProfile = 0
                };

                if (
                    !SetupDiSetClassInstallParams(
                        deviceInfoSet,
                        ref deviceInfoData,
                        ref propChangeParams,
                        Marshal.SizeOf(propChangeParams)
                    )
                )
                {
                    return false;
                }

                return SetupDiCallClassInstaller(
                    DIF_PROPERTYCHANGE,
                    deviceInfoSet,
                    ref deviceInfoData
                );
            }
            catch
            {
                return false;
            }
        }
    }
}
