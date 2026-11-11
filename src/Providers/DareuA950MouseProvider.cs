using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// DAREU (达尔优) A950 AIR V2 wireless mouse.
    ///
    /// Receiver 260D:1175 exposes a 17-byte FF02/Usage 2 command channel.
    /// The protocol was recovered from the installed DAREU Driver 1.5.7:
    ///
    ///   active : 08 03 00 00 00 00 00 ... 4A
    ///   query  : 08 04 00 00 00 00 00 ... 49
    ///   reply  : 08 04 00 00 00 02 PP CS VV VV ...
    ///                            ^^ ^^ ^^ ^^^^^
    ///                            |  |  |  +-- battery voltage, big-endian mV
    ///                            |  |  +----- charge status: 1 charging, 2 full
    ///                            |  +-------- percentage
    ///                            +----------- payload length: 2 data words
    ///
    /// Every command ends with the driver's checksum:
    ///   sum(bytes[0..15]) &lt; 55 ? 85 - sum : 341 - sum.
    ///
    /// Replies arrive on the same collection through interrupt IN; WriteFile is
    /// used by the official driver, not HID SET_REPORT.
    /// </summary>
    public class DareuA950MouseProvider : IDeviceProvider
    {
        private const ushort DareuVid = 0x260D;
        private const ushort A950AirV2ReceiverPid = 0x1175;
        private const ushort VendorPage = 0xFF02;
        private const ushort VendorUsage = 0x0002;
        private const int FrameSize = 17;
        private const byte ReportId = 0x08;
        private const byte CmdActive = 0x03;
        private const byte CmdBattery = 0x04;
        // JmDeviceBase calls SetDevType(0) for mice. Bit 7 is the keyboard
        // target and makes a mouse answer with an empty acknowledgement.
        private const byte MouseTarget = 0x00;

        private const int PercentIndex = 6;
        private const int ChargeStatusIndex = 7;
        private const byte ChargeStatusCharging = 0x01;
        private const byte ChargeStatusFull = 0x02;

        public string Name { get { return "DAREU HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo hid in catalog)
            {
                if (hid == null) continue;
                if (hid.VendorId != DareuVid || hid.ProductId != A950AirV2ReceiverPid) continue;
                if (hid.UsagePage != VendorPage || hid.Usage != VendorUsage) continue;
                if (hid.InputReportLength != FrameSize || hid.OutputReportLength != FrameSize) continue;
                if (string.IsNullOrEmpty(hid.Path) || !seen.Add(hid.Path)) continue;

                DeviceInfo device = new DeviceInfo();
                device.Id = "dareu-a950-air-v2-260D1175";
                device.Brand = "DAREU";
                device.Model = "A950 AIR V2";
                device.Name = "DAREU A950 AIR V2";
                device.ShortModel = "A950 AIR V2";
                device.Kind = DeviceKind.Mouse;
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

                    // The official driver first checks that the paired mouse is
                    // awake, then asks for the power state.
                    if (SendCommand(session, CmdActive, 900) == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "鼠标休眠或未连接";
                        return reading;
                    }

                    byte[] reply = SendCommand(session, CmdBattery, 1000);
                    if (reply == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "设备休眠";
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

                    byte chargeStatus = reply[ChargeStatusIndex];
                    if (chargeStatus > ChargeStatusFull)
                    {
                        reading.Error = "充电状态无效";
                        return reading;
                    }

                    reading.Percent = percent;
                    reading.Level = BatteryMath.FromPercent(percent);
                    reading.Charging = chargeStatus == ChargeStatusCharging;
                    reading.ChargingKnown = true;
                    reading.Online = true;
                    int millivolts = (reply[8] << 8) | reply[9];
                    reading.Detail = "2.4G " + millivolts + " mV";
                    if (chargeStatus == ChargeStatusCharging) reading.Detail += " 充电中";
                }
            }
            catch (Exception ex)
            {
                reading.Error = ex.Message;
            }

            return reading;
        }

        private static byte[] SendCommand(HidSession session, byte command, int timeoutMs)
        {
            byte[] request = new byte[FrameSize];
            request[0] = ReportId;
            request[1] = command;
            request[5] = MouseTarget;
            request[FrameSize - 1] = Checksum(request);

            if (!session.Write(request, 300)) return null;

            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                byte[] reply = session.Read(120);
                if (reply == null) continue;
                if (reply.Length != FrameSize) continue;
                if (reply[0] != ReportId || reply[1] != command) continue;

                // Ignore an exact echo of the request before the real response.
                bool echo = true;
                for (int i = 0; i < FrameSize; i++)
                {
                    if (reply[i] != request[i]) { echo = false; break; }
                }
                if (echo) continue;

                if (command == CmdActive && reply[PercentIndex] != 1) continue;
                // A real battery result carries payload length 2 at byte 5.
                // Other command acknowledgements and periodic status packets
                // can share report id 0x08 and must not be parsed as a level.
                if (command == CmdBattery && reply[5] != 0x02) continue;
                return reply;
            }
            return null;
        }

        private static byte Checksum(byte[] buffer)
        {
            int sum = 0;
            for (int i = 0; i < buffer.Length - 1; i++) sum += buffer[i];
            return (byte)((sum < 55 ? 85 - sum : 341 - sum) & 0xFF);
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
