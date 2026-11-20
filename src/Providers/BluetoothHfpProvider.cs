using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// 蓝牙耳机／手柄／鼠标的电量（HFP 上报，Windows 未公开 PnP 属性）。
    ///
    /// 耳机连上后通过 HFP（免提协议）向主机上报电量，Windows 蓝牙驱动收到后
    /// 会写入该设备 **Hands-Free AG 节点** 上的一个未公开 PnP 属性：
    ///
    ///   PROPERTYKEY {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2   (DEVPROP_TYPE_BYTE)
    ///   值 = 电量百分比 0-100
    ///
    /// 关键：这个属性**不在设备主节点上**，只在 Hands-Free / 免提 AG 子节点上，
    /// 所以在主节点或注册表里翻是找不到的 —— 这是很多"读不到蓝牙电量"的根因。
    ///
    /// 与 2.4G 接收器不同，这里没有 HID 通道可用，全靠 SetupAPI 读设备属性，
    /// 因此 Discover 直接忽略传入的 HID 目录自己枚举 BTHENUM 树。
    /// </summary>
    public class BluetoothHfpProvider : IDeviceProvider
    {
        // {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2
        private const uint BatteryFormatId1 = 0x104EA319;
        private const ushort BatteryFormatId2 = 0x6EE2;
        private const ushort BatteryFormatId3 = 0x4701;
        private const uint BatteryPropertyId = 2;

        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_ALLCLASSES = 0x04;
        private const uint SPDRP_FRIENDLYNAME = 0x0C;
        private const uint SPDRP_DEVICEDESC = 0x00;
        private const int ERROR_NOT_FOUND = 1168;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int ERROR_NO_MORE_ITEMS = 259;

        public string Name { get { return "蓝牙 HFP"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            foreach (BtNode node in Enumerate())
            {
                // 只有真正带电量属性的节点才显示；其它蓝牙节点（A2DP / AVRCP / 串口）
                // 只会变成一张永远"等待读取"的空卡片，没有意义。
                if (!node.HasBattery) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "bt-hfp-" + node.InstanceId;
                d.Brand = "蓝牙";
                d.Model = node.DisplayName;
                d.Name = node.DisplayName;
                d.ShortModel = Shorten(node.DisplayName);
                d.Kind = node.IsHandsFree ? DeviceKind.Headset : DeviceKind.Other;
                d.VendorId = 0;
                d.ProductId = 0;
                d.HidPath = node.InstanceId;   // 没有 HID 句柄，用实例 id 当身份
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
            r.Detail = "蓝牙 · HFP 电量";

            if (string.IsNullOrEmpty(device.HidPath))
            {
                r.Error = "设备标识缺失";
                return r;
            }

            BtNode node = null;
            foreach (BtNode n in Enumerate())
            {
                if (n.InstanceId == device.HidPath) { node = n; break; }
            }

            if (node == null)
            {
                // 耳机收进充电仓 / 关机时，HFP 节点会消失
                r.Sleeping = true;
                r.Error = "耳机未连接（可能已关机或放回充电仓）";
                return r;
            }

            if (!node.HasBattery)
            {
                r.Sleeping = true;
                r.Error = "耳机尚未上报电量";
                return r;
            }

            if (node.Battery <= 0)
            {
                // 属性存在但值为 0：驱动还没收到有效上报
                r.Sleeping = true;
                r.Error = "暂未收到电量上报";
                return r;
            }

            int percent = node.Battery > 100 ? 100 : node.Battery;
            r.Percent = percent;
            r.Level = BatteryMath.FromPercent(percent);
            r.Online = true;
            r.ChargingKnown = false;   // HFP 只给电量，不给充电状态

            return r;
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "蓝牙设备";
            return s.Length <= 14 ? s : s.Substring(0, 13) + "…";
        }

        // ------------------------------------------------------------------
        // SetupAPI 枚举
        // ------------------------------------------------------------------

        private sealed class BtNode
        {
            public string InstanceId;
            public string FriendlyName;
            public bool HasBattery;
            public int Battery;
            public bool IsHandsFree;

            public string DisplayName
            {
                get
                {
                    string n = FriendlyName;
                    if (string.IsNullOrEmpty(n)) return "蓝牙设备";
                    // "觅声-Polaris Hands-Free AG" -> "觅声-Polaris"
                    if (n.EndsWith(" Hands-Free AG", StringComparison.OrdinalIgnoreCase))
                        n = n.Substring(0, n.Length - " Hands-Free AG".Length);
                    return n.Trim();
                }
            }
        }

        private static List<BtNode> Enumerate()
        {
            List<BtNode> nodes = new List<BtNode>();
            IntPtr hdev = SetupDiGetClassDevsW(IntPtr.Zero, "BTHENUM", IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (hdev == new IntPtr(-1)) return nodes;

            try
            {
                for (uint i = 0; ; i++)
                {
                    SP_DEVINFO_DATA info = new SP_DEVINFO_DATA();
                    info.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                    if (!SetupDiEnumDeviceInfo(hdev, i, ref info))
                    {
                        if (Marshal.GetLastWin32Error() != ERROR_NO_MORE_ITEMS) break;
                        break;
                    }

                    BtNode node = new BtNode();
                    node.InstanceId = GetInstanceId(hdev, ref info);
                    if (string.IsNullOrEmpty(node.InstanceId)) continue;

                    node.FriendlyName = GetString(hdev, ref info, SPDRP_FRIENDLYNAME);
                    if (string.IsNullOrEmpty(node.FriendlyName))
                        node.FriendlyName = GetString(hdev, ref info, SPDRP_DEVICEDESC);

                    node.IsHandsFree =
                        node.FriendlyName.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        node.FriendlyName.IndexOf("免提", StringComparison.Ordinal) >= 0;

                    int? level = ReadBatteryProperty(hdev, ref info, out node.HasBattery);
                    node.Battery = level.HasValue ? level.Value : -1;

                    nodes.Add(node);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hdev);
            }

            return nodes;
        }

        private static string GetString(IntPtr hdev, ref SP_DEVINFO_DATA info, uint property)
        {
            for (int size = 512; size <= 8192; size *= 2)
            {
                byte[] buffer = new byte[size];
                uint required;
                if (SetupDiGetDeviceRegistryPropertyW(hdev, ref info, property, IntPtr.Zero,
                        buffer, (uint)size, out required))
                {
                    return Encoding.Unicode.GetString(buffer).Split('\0')[0].Trim();
                }
                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER) break;
            }
            return "";
        }

        private static string GetInstanceId(IntPtr hdev, ref SP_DEVINFO_DATA info)
        {
            StringBuilder sb = new StringBuilder(1024);
            uint required;
            if (SetupDiGetDeviceInstanceIdW(hdev, ref info, sb, 1024, out required))
                return sb.ToString();
            return "";
        }

        private static int? ReadBatteryProperty(IntPtr hdev, ref SP_DEVINFO_DATA info, out bool exists)
        {
            exists = false;

            DEVPROPKEY key = new DEVPROPKEY();
            key.fmtid = new GUID();
            key.fmtid.Data1 = BatteryFormatId1;
            key.fmtid.Data2 = BatteryFormatId2;
            key.fmtid.Data3 = BatteryFormatId3;
            key.fmtid.Data4 = new byte[] { 0xBD, 0x47, 0x8D, 0xDB, 0xF4, 0x25, 0xBB, 0xE5 };
            key.pid = BatteryPropertyId;

            byte[] value = new byte[8];
            uint propertyType;
            uint required;
            bool ok = SetupDiGetDevicePropertyW(hdev, ref info, ref key, out propertyType,
                value, (uint)value.Length, out required, 0);

            if (!ok)
            {
                // 属性不存在是这个 API 的正常返回，不是错误
                return null;
            }

            exists = true;
            return value[0];
        }

        // ------------------------------------------------------------------
        // P/Invoke
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct GUID
        {
            public uint Data1;
            public ushort Data2;
            public ushort Data3;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Data4;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public GUID fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public GUID ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator,
            IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr hdev, uint index,
            ref SP_DEVINFO_DATA info);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr hdev,
            ref SP_DEVINFO_DATA info, uint property, IntPtr propertyRegDataType,
            byte[] buffer, uint bufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr hdev,
            ref SP_DEVINFO_DATA info, StringBuilder buffer, uint bufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(IntPtr hdev,
            ref SP_DEVINFO_DATA info, ref DEVPROPKEY key, out uint propertyType,
            byte[] buffer, uint bufferSize, out uint requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr hdev);
    }
}
