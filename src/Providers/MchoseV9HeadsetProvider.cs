using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// MCHOSE (迈丛) V9 Turbo+ 2.4G headset.
    ///
    /// The receiver exposes several standard and vendor HID collections. Battery
    /// commands are answered only by the 64-byte FF90 collection:
    ///
    ///   query : 55 65 01 00 ...
    ///   reply : 55 65 50 02 ...
    ///                 ^^ ^^
    ///                 |  +-- 0x02 discharging, 0x03 charging, 0x04 full
    ///                 +----- battery percentage
    ///
    /// Verified on 3837:600A by the V9 Turbo+ capture documented in
    /// myfinder-device-battery#61. The companion 3837:6008 lighting interface is
    /// intentionally ignored.
    /// </summary>
    public class MchoseV9HeadsetProvider : IDeviceProvider
    {
        private const ushort MchoseVid = 0x3837;
        private const ushort V9TurboPlusPid = 0x600A;
        private const ushort VendorPage = 0xFF90;
        private const int FrameSize = 64;
        private const byte ReportId = 0x55;
        private const byte CmdDeviceInfo = 0x65;
        private const byte TargetHeadset = 0x01;

        private const int PercentIndex = 2;
        private const int StateIndex = 3;
        private const byte StateDischarging = 0x02;
        private const byte StateCharging = 0x03;
        private const byte StateFull = 0x04;

        public string Name { get { return "MCHOSE V9 Audio"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo hid in catalog)
            {
                if (hid == null) continue;
                if (hid.VendorId != MchoseVid || hid.ProductId != V9TurboPlusPid) continue;
                if (hid.UsagePage != VendorPage) continue;
                if (hid.InputReportLength != FrameSize || hid.OutputReportLength != FrameSize) continue;
                if (string.IsNullOrEmpty(hid.Path) || !seen.Add(hid.Path)) continue;

                DeviceInfo device = new DeviceInfo();
                device.Id = "mchose-v9-headset-" + V9TurboPlusPid.ToString("X4");
                device.Brand = "MCHOSE";
                device.Model = "V9 Turbo+";
                device.Name = "MCHOSE V9 Turbo+";
                device.ShortModel = "V9 Turbo+";
                device.Kind = DeviceKind.Headset;
                device.VendorId = hid.VendorId;
                device.ProductId = hid.ProductId;
                device.HidPath = hid.Path;
                device.ProviderName = Name;
                found.Add(device);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Online = false;
            reading.Level = BatteryLevel.Unknown;

            try
            {
                using (HidSession session = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    session.Flush(30);
                    byte[] reply = null;

                    for (int attempt = 0; attempt < 3 && reply == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);
                        if (!session.Write(CreateBatteryQuery(), 250))
                        {
                            reading.Error = "发送电量查询失败";
                            return reading;
                        }

                        reply = WaitForBattery(session, attempt == 0 ? 1200 : 650);
                    }

                    if (reply == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "耳机休眠或未连接";
                        return reading;
                    }

                    reading.Raw = ToHex(reply);
                    int percent = reply[PercentIndex];
                    if (percent > 100)
                    {
                        reading.Online = true;
                        reading.Error = "电量读数异常";
                        return reading;
                    }

                    byte state = reply[StateIndex];
                    if (state == StateFull)
                    {
                        percent = 100;
                    }
                    else if (state != StateDischarging && state != StateCharging)
                    {
                        reading.Online = true;
                        reading.Error = "充电状态无效";
                        return reading;
                    }

                    reading.Percent = percent;
                    reading.Level = BatteryMath.FromPercent(percent);
                    reading.Charging = state == StateCharging;
                    reading.ChargingKnown = true;
                    reading.Online = true;
                    reading.Detail = state == StateCharging ? "2.4G 充电中" : "2.4G";
                }
            }
            catch (Exception ex)
            {
                reading.Error = ex.Message;
            }

            return reading;
        }

        private static byte[] CreateBatteryQuery()
        {
            byte[] query = new byte[FrameSize];
            query[0] = ReportId;
            query[1] = CmdDeviceInfo;
            query[2] = TargetHeadset;
            return query;
        }

        private static byte[] WaitForBattery(HidSession session, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                byte[] report = session.Read(150);
                if (report == null) continue;
                if (report.Length >= 4 && report[0] == ReportId && report[1] == CmdDeviceInfo)
                {
                    return report;
                }
            }
            return null;
        }

        private static string ToHex(byte[] report)
        {
            StringBuilder text = new StringBuilder(report.Length * 3);
            for (int i = 0; i < report.Length; i++)
            {
                if (i != 0) text.Append(' ');
                text.Append(report[i].ToString("X2"));
            }
            return text.ToString();
        }
    }
}
