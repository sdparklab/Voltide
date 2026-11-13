using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Keychron M6 on its Link dongle (3434:D026, product string "Keychron Link-KM").
    ///
    /// The mouse lives on the vendor collection usage page 0xFFC1, usage 0x0001;
    /// input and output reports are 64 bytes and carry a report id. One query:
    ///
    ///   host   -> dongle   B3 06 00 00 ...             (64 bytes, 0xB3 = command)
    ///   dongle -> host     B4 06 00 00 00 00 E8 03 ... (64 bytes, 0xB4 = reply)
    ///
    /// Byte 20 of the reply (counted with the report id, i.e. data[19]) is packed:
    /// the low seven bits are the percentage, the top bit is the charging state.
    /// Keychron Launcher parses the same byte as value = 127 &amp; data[19],
    /// state = data[19] &gt;&gt; 7 &amp; 1, which is where the layout was recovered from,
    /// and a capture confirms it:
    ///
    ///   B4 06 00 00 00 00 E8 03 E8 03 E8 03 E8 03 E8 03 1D 01 03 3C 60 00 ...
    ///                                                          ^^ 0x60 &amp; 0x7F = 96%
    ///
    /// The same dongle also exposes 0xFF60 (keyboard, 33 bytes) and 0x008C
    /// (bridge, 33 bytes); neither carries the mouse battery. Launcher additionally
    /// knows a 0xFF0A "4K mouse" collection, which this Link-KM does not enumerate
    /// and which has not been captured, so it is deliberately left unmatched.
    ///
    /// The dongle stops answering once the mouse has been idle for a while; it only
    /// replies again after the mouse is moved. Readings are therefore kept across
    /// refreshes instead of dropping to "--".
    /// </summary>
    public class KeychronProvider : IDeviceProvider
    {
        private const ushort KeychronVid = 0x3434;
        private const ushort MouseUsagePage = 0xFFC1;
        private const ushort MouseUsage = 0x0001;
        private const byte CommandReportId = 0xB3;
        private const byte ReplyReportId = 0xB4;
        private const byte DeviceInfoCommand = 0x06;
        private const int FrameSize = 64;
        private const int BatteryIndex = 20;
        private const int LevelMask = 0x7F;
        private const int ChargingBit = 0x80;
        private const int WriteTimeoutMs = 500;
        private const int ReplyTimeoutMs = 1200;
        private const int ReadSliceMs = 150;
        private const int DrainWaitMs = 20;
        private const int AwakeSeconds = 90;

        private HidSession _session;
        private string _openPath;
        private DeviceReading _last;
        private DateTime _lastAt;

        /// <summary>The report lengths are per collection, so they travel with the device.</summary>
        private sealed class LinkDevice : DeviceInfo
        {
            public int InputLength;
            public int OutputLength;
        }

        public string Name { get { return "Keychron HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (catalog == null) return found;

            foreach (HidDeviceInfo hid in catalog)
            {
                if (!Matches(hid)) continue;
                if (seen.ContainsKey(hid.Path)) continue;
                seen.Add(hid.Path, true);

                LinkDevice device = new LinkDevice();
                device.Id = "keychron-" + hid.VendorId.ToString("X4") + ":" + hid.ProductId.ToString("X4");
                device.Brand = "Keychron";
                device.Model = "M6";
                device.ShortModel = "M6";
                device.Name = "Keychron M6";
                device.Kind = DeviceKind.Mouse;
                device.VendorId = hid.VendorId;
                device.ProductId = hid.ProductId;
                device.HidPath = hid.Path;
                device.ProviderName = Name;
                device.InputLength = hid.InputReportLength;
                device.OutputLength = hid.OutputReportLength;
                found.Add(device);
            }

            if (_session != null && !seen.ContainsKey(_openPath)) Close();
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Level = BatteryLevel.Unknown;

            LinkDevice link = device as LinkDevice;
            if (link == null || string.IsNullOrEmpty(link.HidPath))
            {
                reading.Error = "没有 Keychron 接收器";
                return reading;
            }

            try
            {
                byte[] reply = Query(link);
                if (reply == null)
                {
                    // After the mouse has slept, the open handle can keep swallowing
                    // reads. A fresh handle is the cheap way back.
                    Close();
                    reply = Query(link);
                }

                if (reply == null)
                {
                    if (_last != null && _last.Percent.HasValue &&
                        (DateTime.Now - _lastAt).TotalSeconds < AwakeSeconds)
                    {
                        return CopyLast();
                    }

                    if (_last != null && _last.Percent.HasValue)
                    {
                        reading.Percent = _last.Percent;
                        reading.Level = _last.Level;
                        reading.Charging = _last.Charging;
                        reading.ChargingKnown = _last.ChargingKnown;
                        reading.Raw = _last.Raw;
                        reading.Detail = "2.4G";
                    }
                    reading.Sleeping = true;
                    reading.Error = "设备休眠（移动鼠标后恢复）";
                    return reading;
                }

                int percent;
                bool charging;
                if (!TryParse(reply, out percent, out charging))
                {
                    reading.Raw = ToHex(reply);
                    reading.Error = "电量应答格式无效";
                    return reading;
                }

                reading.Percent = percent;
                reading.Level = BatteryMath.FromPercent(percent);
                reading.Charging = charging;
                reading.ChargingKnown = true;
                reading.Online = true;
                reading.Detail = "2.4G";
                reading.Raw = ToHex(reply);
                _last = reading;
                _lastAt = reading.UpdatedAt;
                return reading;
            }
            catch (Exception ex)
            {
                Close();
                reading.Error = ex.Message;
                return reading;
            }
        }

        /// <summary>
        /// Send the device-info command and return the first matching reply, or null
        /// when the dongle stays quiet for the whole window.
        /// </summary>
        private byte[] Query(LinkDevice device)
        {
            if (_session == null || !string.Equals(_openPath, device.HidPath, StringComparison.OrdinalIgnoreCase))
            {
                Close();
                _session = new HidSession(device.HidPath, device.InputLength, device.OutputLength);
                _openPath = device.HidPath;
            }

            _session.Flush(DrainWaitMs);

            byte[] request = new byte[device.OutputLength];
            request[0] = CommandReportId;
            request[1] = DeviceInfoCommand;
            if (!_session.Write(request, WriteTimeoutMs)) return null;

            Stopwatch wait = Stopwatch.StartNew();
            int left;
            while ((left = ReplyTimeoutMs - (int)wait.ElapsedMilliseconds) > 0)
            {
                byte[] reply = _session.Read(Math.Min(ReadSliceMs, left));
                if (reply == null) continue;
                if (IsReply(reply)) return reply;
            }
            return null;
        }

        /// <summary>0xB4 answers command 0x06; anything else is not our frame.</summary>
        private static bool IsReply(byte[] reply)
        {
            return reply.Length > BatteryIndex &&
                reply[0] == ReplyReportId &&
                reply[1] == DeviceInfoCommand;
        }

        internal static bool TryParse(byte[] reply, out int percent, out bool charging)
        {
            percent = 0;
            charging = false;
            if (!IsReply(reply)) return false;

            int packed = reply[BatteryIndex];
            int level = packed & LevelMask;
            if (level > 100) return false;

            percent = level;
            charging = (packed & ChargingBit) != 0;
            return true;
        }

        private DeviceReading CopyLast()
        {
            DeviceReading copy = new DeviceReading();
            copy.UpdatedAt = _last.UpdatedAt;
            copy.Percent = _last.Percent;
            copy.Level = _last.Level;
            copy.Charging = _last.Charging;
            copy.ChargingKnown = _last.ChargingKnown;
            copy.Online = true;
            copy.Detail = "2.4G";
            copy.Raw = _last.Raw;
            return copy;
        }

        private static bool Matches(HidDeviceInfo hid)
        {
            return hid != null &&
                hid.VendorId == KeychronVid &&
                hid.UsagePage == MouseUsagePage &&
                hid.Usage == MouseUsage &&
                hid.InputReportLength >= FrameSize &&
                hid.OutputReportLength >= FrameSize &&
                !string.IsNullOrEmpty(hid.Path);
        }

        private void Close()
        {
            if (_session != null) _session.Dispose();
            _session = null;
            _openPath = null;
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
