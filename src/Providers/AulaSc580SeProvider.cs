using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// AULA SC580 SE 2.4G wireless mouse.
    ///
    /// The receiver exposes a 33-byte vendor Feature Report on 248D:5B5F,
    /// usage page 0xFF00 / usage 0x0001. The command format was recovered from
    /// the official AULA SC580SE driver (GamingMS.exe):
    ///
    ///   request : 0C 01 20 00 SS 00 00 ...
    ///   response: 0C .. 20 00 .. .. .. 4D 30 35 37 ... ... ... ... ...
    ///                         ^^                   ^^^^^^^^^^^^^^^^^^^
    ///                         |                    +-- status marker "M057"
    ///                         +-- response type 0x20
    ///
    ///   response[0x0F] = sensor id (0x25 on SC580 SE)
    ///   response[0x12] = battery percentage
    ///   response[0x13] = 0 discharging, 1 charging, 2 full
    ///   response[0x15] = non-zero while the paired mouse is awake
    ///
    /// Only GET_FEATURE/SET_FEATURE status commands are used. Mouse settings are
    /// never modified.
    /// </summary>
    public class AulaSc580SeProvider : IDeviceProvider
    {
        private const ushort AulaVid = 0x248D;
        private const ushort Sc580SePid = 0x5B5F;
        private const ushort VendorPage = 0xFF00;
        private const ushort VendorUsage = 0x0001;
        private const int ReportLength = 33;

        private const byte ReportId = 0x0C;
        private const byte StatusType = 0x20;
        private const byte SensorSc580Se = 0x25;
        private const int SensorIndex = 0x0F;
        private const int PercentIndex = 0x12;
        private const int ChargeStateIndex = 0x13;
        private const int AwakeIndex = 0x15;

        private int _sequence = 1;

        public string Name { get { return "AULA SC580SE HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo h in catalog)
            {
                if (h == null) continue;
                if (h.VendorId != AulaVid || h.ProductId != Sc580SePid) continue;
                if (h.UsagePage != VendorPage || h.Usage != VendorUsage) continue;
                if (h.FeatureReportLength != ReportLength) continue;
                if (string.IsNullOrEmpty(h.Path) || !seen.Add(h.Path)) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "aula-sc580se-" + h.Path;
                d.Brand = "AULA";
                d.Model = "SC580SE";
                d.Name = "AULA SC580SE";
                d.ShortModel = "SC580SE";
                d.Kind = DeviceKind.Mouse;
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
            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Level = BatteryLevel.Unknown;
            reading.ChargingKnown = true;

            if (device == null || string.IsNullOrEmpty(device.HidPath))
            {
                reading.Error = "No AULA receiver";
                return reading;
            }

            try
            {
                using (HidSession session = new HidSession(device.HidPath, ReportLength, ReportLength))
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        byte[] request = NewStatusRequest();
                        if (!HidNative.HidD_SetFeature(session.Handle, request, request.Length))
                        {
                            if (attempt == 2)
                            {
                                reading.Error = "Status request failed";
                                return reading;
                            }
                            Thread.Sleep(30);
                            continue;
                        }

                        Thread.Sleep(10);
                        for (int poll = 0; poll < 5; poll++)
                        {
                            byte[] response = new byte[ReportLength];
                            response[0] = ReportId;
                            if (!HidNative.HidD_GetFeature(session.Handle, response, response.Length))
                            {
                                Thread.Sleep(10);
                                continue;
                            }

                            int percent;
                            bool charging;
                            bool full;
                            bool awake;
                            if (!TryParseStatus(response, out percent, out charging, out full, out awake))
                            {
                                Thread.Sleep(10);
                                continue;
                            }

                            reading.Raw = ToHex(response);
                            reading.Percent = full ? 100 : percent;
                            reading.Level = BatteryMath.FromPercent(reading.Percent.Value);
                            reading.Charging = charging;
                            reading.Online = awake;
                            reading.Sleeping = !awake;
                            reading.Detail = "2.4G";
                            return reading;
                        }
                    }

                    reading.Sleeping = true;
                    reading.Error = "Mouse offline";
                }
            }
            catch (Exception ex)
            {
                reading.Error = ex.Message;
            }

            return reading;
        }

        private byte[] NewStatusRequest()
        {
            byte[] request = new byte[ReportLength];
            request[0] = ReportId;
            request[1] = 0x01;
            request[2] = StatusType;
            request[3] = 0x00;
            request[4] = (byte)(_sequence & 0xFF);

            _sequence++;
            if (_sequence > 0xFF) _sequence = 1;
            return request;
        }

        internal static bool TryParseStatus(byte[] report, out int percent, out bool charging,
                                            out bool full, out bool awake)
        {
            percent = 0;
            charging = false;
            full = false;
            awake = false;

            if (report == null || report.Length < AwakeIndex + 1) return false;
            if (report[0] != ReportId || report[2] != StatusType || report[3] != 0x00) return false;
            if (report[8] != 0x4D || report[9] != 0x30 || report[10] != 0x35 || report[11] != 0x37)
                return false;
            if (report[SensorIndex] != SensorSc580Se) return false;
            if (report[PercentIndex] > 100) return false;
            if (report[ChargeStateIndex] > 2) return false;

            percent = report[PercentIndex];
            charging = report[ChargeStateIndex] == 1;
            full = report[ChargeStateIndex] == 2;
            awake = report[AwakeIndex] != 0;
            return true;
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
