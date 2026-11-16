using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// 兼容 Xbox 的手柄电量适配，通过 XInput 查询四个槽位。
    ///
    /// XInputGetBatteryInformation 返回电池类型和四档电量：
    /// BatteryType：0 未连接，1 有线，2 碱性电池，3 镍氢电池。
    /// BatteryLevel：0 空，1 低，2 中，3 满。
    /// 界面将四档映射为 5%、20%、50%、100%，并在详情标注“XInput 四档”。
    ///
    /// 有线类型缺少真实电量和充电状态，本适配器沿用 100% 充电的显示方式。
    /// 具有专用 HID 电量适配器的已知型号通过 VID/PID 排除，避免重复卡片。
    /// 已有适配记录覆盖 8BitDo Ultimate 3E Xbox（2DC8:202F）的 2.4G 电量读取。
    /// </summary>
    public class XInputProvider : IDeviceProvider
    {
        private const int MaxSlots = 4;

        public string Name { get { return "XInput"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            for (int slot = 0; slot < MaxSlots; slot++)
            {
                if (!XInputNative.Connected(slot)) continue;

                // 已有专用 HID 电量适配的型号跳过 XInput 卡片，
                // 避免同一手柄额外显示“有线 100%”的读数。
                ushort vid, pid;
                if (XInputNative.Ids(slot, out vid, out pid) && LeadjoyProvider.Owns(vid, pid)) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "xinput-" + slot;
                d.Brand = "XBOX";
                d.Model = "CONTROLLER";
                d.ShortModel = "XBOX " + (slot + 1);
                d.Name = "Xbox 手柄";
                d.Kind = DeviceKind.Other;

                // 将槽位编码为查询地址，读取时无需保存额外的设备状态。
                d.HidPath = "@xinput:" + slot;
                d.ProviderName = Name;
                found.Add(d);
            }

            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;

            int slot = SlotOf(device);
            if (slot < 0)
            {
                r.Error = "手柄编号无效";
                return r;
            }

            if (!XInputNative.Connected(slot))
            {
                r.Sleeping = true;
                r.Error = "设备休眠";
                return r;
            }

            XInputNative.BatteryInfo info;
            if (!XInputNative.Battery(slot, out info))
            {
                r.Online = true;
                r.Error = "读数失败";
                return r;
            }

            r.Online = true;

            if (info.BatteryType == 1)
            {
                // XInput 的有线类型没有独立充电标志，此处沿用有线充电的显示方式。
                r.Percent = 100;
                r.Level = BatteryLevel.Full;
                r.Charging = true;
                r.Detail = "XInput 有线";
                r.Raw = "type=1 level=" + info.BatteryLevel;
                return r;
            }

            int? percent = PercentFor(info.BatteryLevel);
            if (!percent.HasValue)
            {
                r.Error = "读数异常";
                r.Raw = "type=" + info.BatteryType + " level=" + info.BatteryLevel;
                return r;
            }

            r.Percent = percent;
            r.Level = BatteryMath.FromPercent(percent.Value);
            r.Detail = "XInput 四档";
            r.Raw = "type=" + info.BatteryType + " level=" + info.BatteryLevel;
            return r;
        }

        private static int SlotOf(DeviceInfo device)
        {
            string path = device.HidPath;
            if (path == null) return -1;
            const string prefix = "@xinput:";
            if (path.Length <= prefix.Length) return -1;
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return -1;

            int slot;
            if (!int.TryParse(path.Substring(prefix.Length), out slot)) return -1;
            if (slot < 0 || slot >= MaxSlots) return -1;
            return slot;
        }

        /// <summary>
        /// 将四档电量转换为界面百分比；数值仅表示档位，不代表精确测量。
        /// </summary>
        private static int? PercentFor(byte level)
        {
            if (level == 3) return 100;
            if (level == 2) return 50;
            if (level == 1) return 20;
            if (level == 0) return 5;
            return null;
        }
    }

    /// <summary>
    /// 按需调用 XInput 1.4，状态和电量查询失败时回退到 1.3。
    /// 缺少 DLL 或入口时返回失败，避免影响应用启动。
    /// </summary>
    internal static class XInputNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BatteryInfo
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("XInput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState14(uint index, byte[] state);

        [DllImport("XInput1_3.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState13(uint index, byte[] state);

        [DllImport("XInput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern uint GetBattery14(uint index, byte devType, out BatteryInfo info);

        [DllImport("XInput1_3.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern uint GetBattery13(uint index, byte devType, out BatteryInfo info);

        // 通过序号 108 调用未公开的 XInputGetCapabilitiesEx。
        // 返回结构以前 20 字节的 XINPUT_CAPABILITIES 开头，随后为 VID、PID 和版本号。
        [DllImport("XInput1_4.dll", EntryPoint = "#108")]
        private static extern uint GetCapabilitiesEx14(uint one, uint index, uint flags, byte[] caps);

        /// <summary>读取 XInput 槽位对应设备的 VID/PID；调用不可用或失败时返回 false。</summary>
        internal static bool Ids(int slot, out ushort vid, out ushort pid)
        {
            vid = 0;
            pid = 0;
            byte[] caps = new byte[32];
            try
            {
                if (GetCapabilitiesEx14(1, (uint)slot, 0, caps) != 0) return false;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }

            vid = BitConverter.ToUInt16(caps, 20);
            pid = BitConverter.ToUInt16(caps, 22);
            return true;
        }

        internal static bool Connected(int slot)
        {
            byte[] state = new byte[16];
            try
            {
                if (GetState14((uint)slot, state) == 0) return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            try
            {
                if (GetState13((uint)slot, state) == 0) return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            return false;
        }

        internal static bool Battery(int slot, out BatteryInfo info)
        {
            info = new BatteryInfo();
            BatteryInfo scratch;

            try
            {
                if (GetBattery14((uint)slot, 0, out scratch) == 0)
                {
                    info = scratch;
                    return true;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            try
            {
                if (GetBattery13((uint)slot, 0, out scratch) == 0)
                {
                    info = scratch;
                    return true;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            return false;
        }
    }
}
