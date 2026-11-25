using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Voltide.Interop
{
    /// <summary>
    /// One Bluetooth (BLE) device node as Windows knows it.
    /// BatteryPercent comes from the same cached device property that the
    /// Windows "Bluetooth and other devices" page reads, so the panel and the
    /// OS never disagree.
    /// </summary>
    public class BtDeviceInfo
    {
        public string DeviceId;        // BTHLE\Dev_xxxxxxxxxxxx\7&xxx&0&xxxxxxxxxxxx
        public string FriendlyName;    // "A950 BT"
        public string HardwareId;      // BTHLE\Dev_xxxxxxxxxxxx
        public string InstanceToken;   // xxxxxxxxxxxx  (the &0& segment tail)
        public int? BatteryPercent;    // null when Windows has not cached a value
        public string BatteryRaw;      // diagnostics: "type=3 val=50"
    }

    /// <summary>
    /// Read-only Bluetooth device enumeration.
    ///
    /// BLE HID devices expose no vendor report on this mouse (see
    /// docs/protocol.md), so battery has to come from the system cache:
    ///
    ///   device class GUID : {E0CBF06C-CD8B-4647-BB8A-263B43F0F974}  (Bluetooth)
    ///   property          : {104EA319-6EE2-4701-BD47-8DDBF425BBE5}  pid 2
    ///                       DEVPROP_TYPE_BYTE, 0..100
    ///
    /// The property is filled by the Bluetooth stack from the GATT Battery
    /// Level characteristic (0x180F / 0x2A19) and refreshed on notifications,
    /// so it can lag the mouse by minutes. Nothing here writes to the device.
    /// </summary>
    public static class BluetoothNative
    {
        private static readonly Guid BluetoothClassGuid = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
        private static readonly Guid BatteryFormatId = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5");
        private const uint BatteryPropertyId = 2;

        private const uint SPDRP_HARDWAREID = 0x00000001;
        private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
        private const int DIGCF_PRESENT = 0x02;

        private const uint DEVPROP_TYPE_BYTE = 3;
        private const uint DEVPROP_TYPE_UINT32 = 7;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType,
            byte[] buffer, int bufferSize, out int required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(
            IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder instanceId, int size, out int required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceProperty(
            IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint propertyType,
            byte[] buffer, int bufferSize, out int required, int flags);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        /// <summary>
        /// Every currently connected Bluetooth device node. Nodes without a
        /// cached battery still appear, so callers can tell "not connected"
        /// apart from "connected but Windows has no reading yet".
        /// </summary>
        public static List<BtDeviceInfo> Enumerate()
        {
            List<BtDeviceInfo> result = new List<BtDeviceInfo>();
            Guid classGuid = BluetoothClassGuid;   // static readonly 不能直接 ref，先取本地副本
            IntPtr set = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

            try
            {
                byte[] buffer = new byte[2048];
                for (uint i = 0; ; i++)
                {
                    SP_DEVINFO_DATA data = new SP_DEVINFO_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                    BtDeviceInfo info = new BtDeviceInfo();
                    info.FriendlyName = ReadString(set, ref data, SPDRP_FRIENDLYNAME, buffer);
                    info.HardwareId = ReadString(set, ref data, SPDRP_HARDWAREID, buffer);
                    info.DeviceId = ReadInstanceId(set, ref data);
                    info.InstanceToken = TokenOf(info.DeviceId);
                    ReadBattery(set, ref data, info);
                    result.Add(info);
                }
            }
            catch
            {
                // A partially populated list is still useful; the provider treats
                // a missing entry as "not connected".
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return result;
        }

        /// <summary>Convenience: the node whose instance id carries this token.</summary>
        public static BtDeviceInfo FindByToken(List<BtDeviceInfo> nodes, string token)
        {
            if (nodes == null || String.IsNullOrEmpty(token)) return null;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (String.Equals(nodes[i].InstanceToken, token, StringComparison.OrdinalIgnoreCase)) return nodes[i];
            }
            return null;
        }

        private static string ReadString(IntPtr set, ref SP_DEVINFO_DATA data, uint property, byte[] buffer)
        {
            try
            {
                uint regType;
                int required;
                if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out regType, buffer, buffer.Length, out required))
                    return String.Empty;
                if (required <= 2) return String.Empty;
                int bytes = Math.Min(required - 2, buffer.Length - 2);
                return Encoding.Unicode.GetString(buffer, 0, bytes).Trim(Convert.ToChar(0));
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string ReadInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            try
            {
                int required;
                StringBuilder sb = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out required)) return String.Empty;
                return sb.ToString();
            }
            catch
            {
                return String.Empty;
            }
        }

        /// <summary>
        /// "BTHLE\Dev_aaaaaaaaaaaa\7&bbb&0&aaaaaaaaaaaa" -> "aaaaaaaaaaaa".
        /// The BLE HID interface path carries the same token, which is how the
        /// provider pairs a HID collection with its device node.
        /// </summary>
        private static string TokenOf(string instanceId)
        {
            if (String.IsNullOrEmpty(instanceId)) return String.Empty;
            int marker = instanceId.LastIndexOf("&0&", StringComparison.Ordinal);
            if (marker >= 0 && marker + 3 < instanceId.Length) return instanceId.Substring(marker + 3);
            int slash = instanceId.LastIndexOf('\\');
            if (slash >= 0 && slash + 1 < instanceId.Length) return instanceId.Substring(slash + 1);
            return instanceId;
        }

        private static void ReadBattery(IntPtr set, ref SP_DEVINFO_DATA data, BtDeviceInfo info)
        {
            try
            {
                DEVPROPKEY key = new DEVPROPKEY();
                key.fmtid = BatteryFormatId;
                key.pid = BatteryPropertyId;

                byte[] raw = new byte[64];
                uint propertyType;
                int required;
                if (!SetupDiGetDeviceProperty(set, ref data, ref key, out propertyType, raw, raw.Length, out required, 0))
                {
                    info.BatteryRaw = "missing";
                    return;
                }

                int value = -1;
                if (propertyType == DEVPROP_TYPE_BYTE && required >= 1) value = raw[0];
                else if (propertyType == DEVPROP_TYPE_UINT32 && required >= 4) value = BitConverter.ToInt32(raw, 0);

                info.BatteryRaw = "type=" + propertyType + " val=" + value;
                if (value >= 0 && value <= 100) info.BatteryPercent = value;
            }
            catch
            {
                info.BatteryRaw = "error";
            }
        }
    }
}
