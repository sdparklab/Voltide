using System;
using System.Collections.Generic;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    public class EweadnA5ProProvider : IDeviceProvider
    {
        private byte _sequence;
        public string Name { get { return "EWEADN A5 Pro"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            bool usbConnected = false;
            foreach (HidDeviceInfo h in catalog)
            {
                // OemDrv's battery UI treats wired mode as charging. This is
                // USB connection detection, not a charge-controller status bit.
                if (h.VendorId == 0x258A && h.ProductId == 0x0136 &&
                    h.UsagePage == 0xFF02 && h.Usage == 2 &&
                    h.FeatureReportLength == 520 &&
                    string.Equals(h.ProductName, "EWEADN A5Pro", StringComparison.OrdinalIgnoreCase))
                    usbConnected = true;
            }
            foreach (HidDeviceInfo h in catalog)
            {
                // The installed A5 Pro driver identifies 0138 as its wireless PID.
                if (h.VendorId != 0x258A || h.ProductId != 0x0138 ||
                    h.UsagePage != 0xFF02 || h.Usage != 2 ||
                    h.FeatureReportLength != 520) continue;
                DeviceInfo d = new DeviceInfo();
                d.Id = "eweadn-a5pro-" + h.Path;
                d.Brand = "EWEADN";
                d.Model = "A5 Pro";
                d.Name = "前行者 A5 Pro";
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                d.IsCharging = usbConnected;
                found.Add(d);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo d)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Level = BatteryLevel.Unknown;
            // Without a USB data connection, charging from a wall charger
            // cannot be distinguished from discharge by this protocol.
            r.ChargingKnown = d.IsCharging;
            try
            {
                using (HidSession s = new HidSession(d.HidPath, 8, 0))
                {
                    // OemDrv.exe GetPower (0x496C20): report 9, command 9,
                    // one-byte read; sum bytes 2..519 into byte 1.
                    byte sequence = _sequence++;
                    byte[] query = new byte[520];
                    query[0] = 9;
                    query[1] = (byte)((12 + sequence) & 255);
                    query[2] = 9;
                    query[3] = 1;
                    query[4] = 1;
                    query[5] = sequence;
                    query[6] = 1;
                    if (!HidNative.HidD_SetFeature(s.Handle, query, query.Length))
                        throw new InvalidOperationException("电量查询发送失败");
                    Thread.Sleep(100);
                    byte[] reply = new byte[520];
                    reply[0] = 9;
                    if (!HidNative.HidD_GetFeature(s.Handle, reply, reply.Length) ||
                        reply[0] != 9 || reply[2] != 9 || reply[3] != 1 ||
                        reply[4] != 1 || reply[5] != sequence ||
                        reply[6] != 1 || reply[7] != 0 || reply[8] > 100)
                        throw new InvalidOperationException("未收到有效电量回复");
                    r.Raw = BitConverter.ToString(reply, 0, 16);
                    r.Percent = reply[8];
                    r.Level = BatteryMath.FromPercent(reply[8]);
                    r.Charging = d.IsCharging;
                    r.Detail = d.IsCharging ? "USB 已连接（推定充电）" : "2.4G";
                    r.Online = true;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }
    }
}
