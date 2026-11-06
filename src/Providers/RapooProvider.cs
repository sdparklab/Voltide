using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Rapoo VT3S on its 2.4G receiver. The mouse pushes battery on its own;
    /// this provider never writes to the receiver.
    ///
    /// Verified on a VT3S (receiver 24AE:1411, product "Rapoo Gaming Device"):
    /// collection usage page 0xFF00, usage 0x0002, 19-byte input reports.
    /// Report id 0x07, byte 8 is the percent, byte 7 is the charge state.
    ///
    ///   07 10 03 8C 0A 8C 0A 01 64 00 03 84 00 11 46 01 02 03 C4
    ///                        ^^ ^^
    ///                        |  +-- percent (0x64 = 100)
    ///                        +----- state 1 = on battery, 2/3 = charging
    ///
    /// The handle stays open between refreshes. Windows only queues input
    /// reports for an open handle, and the mouse may wait a few seconds
    /// between reports.
    /// </summary>
    public class RapooProvider : IDeviceProvider
    {
        private const ushort VendorId = 0x24AE;
        private const ushort ProductId = 0x1411;
        private const ushort VendorPage = 0xFF00;
        private const ushort VendorUsage = 0x0002;
        private const byte ReportId = 0x07;
        private const int PercentIndex = 8;
        private const int StateIndex = 7;
        private const int FirstWaitMs = 4500;
        private const int FollowWaitMs = 4500;
        private const int DrainWaitMs = 1;
        private const int FreshSeconds = 5;
        private const int AwakeSeconds = 90;

        private HidSession _session;
        private string _openPath;
        private DeviceReading _last;
        private DateTime _lastAt;

        public string Name { get { return "Rapoo HID"; } }

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

                DeviceInfo device = new DeviceInfo();
                device.Id = "rapoo-" + hid.VendorId.ToString("X4") + ":" + hid.ProductId.ToString("X4");
                device.Brand = "RAPOO";
                device.Model = "VT3S";
                device.ShortModel = "VT3S";
                device.Name = "RAPOO VT3S";
                device.Kind = DeviceKind.Mouse;
                device.VendorId = hid.VendorId;
                device.ProductId = hid.ProductId;
                device.HidPath = hid.Path;
                device.ProviderName = Name;
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

            if (device == null || string.IsNullOrEmpty(device.HidPath))
            {
                reading.Error = "没有雷柏接收器";
                return reading;
            }

            try
            {
                if (_session == null || !string.Equals(_openPath, device.HidPath, StringComparison.OrdinalIgnoreCase))
                {
                    Close();
                    _session = new HidSession(device.HidPath, 19, 0, true);
                    _openPath = device.HidPath;
                }

                byte[] report = NewestReport(WaitForReport());
                if (report == null)
                {
                    // A timed-out read used to leave the handle unable to see the
                    // reports still arriving. Open a fresh one before giving up.
                    Close();
                    _session = new HidSession(device.HidPath, 19, 0, true);
                    _openPath = device.HidPath;
                    report = NewestReport(FirstWaitMs);
                }
                if (report == null)
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
                    reading.Error = "设备休眠";
                    return reading;
                }

                int percent;
                bool charging;
                bool chargingKnown;
                if (!TryParse(report, out percent, out charging, out chargingKnown))
                {
                    reading.Raw = ToHex(report);
                    reading.Error = "电量上报无效";
                    return reading;
                }

                reading.Percent = percent;
                reading.Level = BatteryMath.FromPercent(percent);
                reading.Charging = charging;
                reading.ChargingKnown = chargingKnown;
                reading.Online = true;
                reading.Detail = "2.4G";
                reading.Raw = ToHex(report);
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

        private int WaitForReport()
        {
            if (_last == null) return FirstWaitMs;
            if ((DateTime.Now - _lastAt).TotalSeconds >= FreshSeconds) return FollowWaitMs;
            return DrainWaitMs;
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

        private byte[] NewestReport(int firstWaitMs)
        {
            byte[] newest = null;
            byte[] report = _session.Read(firstWaitMs);
            int spins = 0;
            while (report != null && spins < 32)
            {
                int percent;
                bool charging;
                bool chargingKnown;
                if (TryParse(report, out percent, out charging, out chargingKnown)) newest = report;
                report = _session.Read(DrainWaitMs);
                spins++;
            }
            return newest;
        }

        internal static bool TryParse(byte[] report, out int percent, out bool charging, out bool chargingKnown)
        {
            percent = 0;
            charging = false;
            chargingKnown = false;
            if (report == null || report.Length <= PercentIndex) return false;
            if (report[0] != ReportId) return false;
            if (report[PercentIndex] > 100) return false;

            percent = report[PercentIndex];
            int state = report[StateIndex];
            if (state == 1 || state == 2 || state == 3)
            {
                chargingKnown = true;
                charging = state == 2 || state == 3;
            }
            return true;
        }

        private static bool Matches(HidDeviceInfo hid)
        {
            return hid != null &&
                hid.VendorId == VendorId &&
                hid.ProductId == ProductId &&
                hid.UsagePage == VendorPage &&
                hid.Usage == VendorUsage &&
                hid.InputReportLength >= PercentIndex + 1 &&
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
