using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    public class DualSenseProvider : IDeviceProvider
    {
        private const ushort SonyVid = 0x054C;
        private const ushort DualSensePid = 0x0CE6;
        private const ushort DualSenseEdgePid = 0x0DF2;

        public string Name { get { return "Sony DualSense"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != SonyVid) continue;
                if (h.ProductId != DualSensePid && h.ProductId != DualSenseEdgePid) continue;

                // Match Generic Desktop (0x01) / Game Pad (0x05)
                if (h.UsagePage != 0x0001 || h.Usage != 0x0005) continue;

                bool isEdge = (h.ProductId == DualSenseEdgePid);
                bool isBluetooth = (h.InputReportLength >= 78);

                DeviceInfo d = new DeviceInfo();
                d.Id = "sony-ds-" + Math.Abs(h.Path.GetHashCode()).ToString("X8");
                d.Brand = "SONY";
                d.Model = isEdge ? "DualSense Edge" : "DualSense";
                d.ShortModel = isBluetooth ? (isEdge ? "DSE BT" : "DS BT") : (isEdge ? "DSE USB" : "DS USB");
                d.Name = d.Model + (isBluetooth ? " (蓝牙)" : " (有线)");
                d.Kind = DeviceKind.Other;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
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

            bool isBluetooth = device.ShortModel.Contains("BT");
            int inLen = isBluetooth ? 78 : 64;

            try
            {
                using (HidSession session = new HidSession(device.HidPath, inLen, inLen, true))
                {
                    if (isBluetooth)
                    {
                        // Request Feature Report 0x05 (Calibration data) to kick Bluetooth into sending extended 0x31 reports
                        byte[] feature05 = new byte[41];
                        feature05[0] = 0x05;
                        HidNative.HidD_GetFeature(session.Handle, feature05, feature05.Length);
                    }

                    byte[] buffer = session.Read(300);
                    if (buffer == null || buffer.Length == 0)
                    {
                        r.Sleeping = true;
                        r.Error = "手柄休眠或未响应";
                        return r;
                    }

                    r.Online = true;
                    byte reportId = buffer[0];
                    int statusIndex = -1;

                    if (reportId == 0x01 && buffer.Length >= 54)
                    {
                        // USB input report 0x01: battery status byte is at offset 53
                        statusIndex = 53;
                    }
                    else if (reportId == 0x31 && buffer.Length >= 55)
                    {
                        // Bluetooth input report 0x31: battery status byte is at offset 54
                        statusIndex = 54;
                    }

                    if (statusIndex < 0)
                    {
                        r.Error = "报文格式未知 (ID " + reportId.ToString("X2") + ")";
                        r.Raw = "ID=" + reportId.ToString("X2") + " Len=" + buffer.Length;
                        return r;
                    }

                    byte status = buffer[statusIndex];
                    int capacityLevel = status & 0x0F;
                    int chargingStatus = (status & 0xF0) >> 4;

                    // Each level represents 10%
                    int percent = Math.Min(capacityLevel * 10, 100);
                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);

                    if (chargingStatus == 0x01)
                    {
                        r.Charging = true;
                        r.Detail = "充电中";
                    }
                    else if (chargingStatus == 0x02)
                    {
                        r.Charging = false;
                        r.Percent = 100;
                        r.Level = BatteryLevel.Full;
                        r.Detail = "已充满";
                    }
                    else if (chargingStatus == 0x0A || chargingStatus == 0x0B || chargingStatus == 0x0F)
                    {
                        r.Charging = false;
                        r.Detail = "电池异常";
                    }
                    else
                    {
                        r.Charging = false;
                        r.Detail = isBluetooth ? "蓝牙运行" : "有线运行";
                    }

                    r.Raw = "ID=" + reportId.ToString("X2") + " Status=" + status.ToString("X2");
                    return r;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                return r;
            }
        }
    }
}
