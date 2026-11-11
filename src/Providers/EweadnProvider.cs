using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// EWEADN (前行者) 2.4G mice — verified on the S9 Pro, 372E:103D.
    ///
    /// The receiver exposes a 64-byte Feature Report on usage page 0xFF00,
    /// report id 0x03. Battery query, captured from the vendor's own web driver
    /// (E-HUB, new-hub.eweadn.cn) and replayed on hardware:
    ///
    ///   request : 03 20 50 00 00 4F 81 00 ...
    ///             ^^ ^^ ^^          ^^
    ///             |  |  |           +-- sub-command: battery
    ///             |  |  +-------------- sequence, ignored by the firmware
    ///             |  +----------------- marker byte, constant
    ///             +-------------------- command: battery
    ///
    ///   reply   : 03 50 00 0B 00 01 35 21 04 01 90 65 04 A5 5A 00 ...
    ///                       ^^    ^^ ^^          ^^^^^
    ///                       |     |  |           +-- footer, constant
    ///                       |     |  +-------------- status bitmap
    ///                       |     +----------------- percent (0x35 = 53)
    ///                       +----------------------- payload length 0x0B
    ///
    /// Verified against E-HUB on hardware: the web driver showed 53% while
    /// byte 6 read 0x35, and repeated reads walked 53, 53, 53, 52 as the mouse
    /// discharged.
    ///
    /// Two things are deliberately not claimed:
    ///   - Charging. Only the discharging value 0x21 has been observed in byte
    ///     7; which bit means "on cable" is unknown, so ChargingKnown stays
    ///     false instead of showing a guess.
    ///   - Any PID other than 0x103D. The vendor table lists S9 Max/Ultra/Ultra+
    ///     under different ids, but none of them were available to test, so they
    ///     are left to other providers rather than matched blindly.
    /// </summary>
    public class EweadnProvider : IDeviceProvider
    {
        private const ushort EweadnVid = 0x372E;
        private const ushort S9ProPid = 0x103D;

        private const ushort VendorPage = 0xFF00;   // the battery channel, not 0xFF06
        private const int FrameSize = 64;
        private const byte ReportId = 0x03;
        private const byte CmdBattery = 0x20;
        private const byte SubBattery = 0x81;
        private const byte Marker = 0x50;           // echoed in byte 1 of every reply
        private const byte PayloadLength = 0x0B;    // echoed in byte 3 of a real reply
        private const byte FooterA = 0xA5;
        private const byte FooterB = 0x5A;

        public string Name { get { return "EWEADN HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != EweadnVid) continue;
                if (h.ProductId != S9ProPid) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.FeatureReportLength != FrameSize) continue;

                // Key by the interface instance, not by VID:PID. Two receivers of
                // the same model plug in as identical product ids and would
                // otherwise collapse into one card.
                string key = InstanceOf(h);
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;

                DeviceInfo d = new DeviceInfo();
                d.Id = "eweadn-" + kv.Key;
                d.Brand = "EWEADN";
                d.Model = "S9 Pro";
                d.Name = "EWEADN S9 Pro";
                d.ShortModel = "S9 PRO";
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);
            }

            return found;
        }

        public DeviceReading Read(DeviceInfo d)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;
            r.ChargingKnown = false;

            // Read+write: the battery collection accepts both rights, unlike the
            // mice/pad collections on the same receiver which refuse reads.
            IntPtr handle = HidNative.CreateFile(
                d.HidPath,
                HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
                HidNative.FILE_SHARE_RW,
                IntPtr.Zero,
                HidNative.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle == new IntPtr(-1))
            {
                r.Error = "打开失败（错误 " + Marshal.GetLastWin32Error() + "）";
                return r;
            }

            try
            {
                // Four attempts with a real pause. A sleeping 2.4G mouse does
                // not answer at all (HidD_GetFeature reports 87 while it is
                // asleep), and the receiver needs a moment after each command
                // before the answer is staged.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(140);

                    byte[] request = BuildQuery();

                    if (!HidNative.HidD_SetFeature(handle, request, FrameSize))
                    {
                        continue;
                    }

                    // The receiver needs roughly 50 ms to stage the answer.
                    // Polling sooner only collects "not ready" (error 87).
                    Thread.Sleep(60);

                    byte[] reply = WaitFor(handle, 150);
                    if (reply == null) continue;

                    int percent;
                    bool charging;
                    if (!TryParse(reply, out percent, out charging)) continue;

                    r.Raw = Hex(reply, 16);
                    r.Online = true;
                    r.Sleeping = false;
                    r.Error = null;
                    r.Charging = charging;
                    r.ChargingKnown = true;

                    if (charging)
                    {
                        // No trustworthy level on cable, so the card shows the
                        // state alone instead of a number that is not real.
                        r.Percent = null;
                        r.Level = BatteryLevel.Unknown;
                        r.Detail = "2.4G · 充电中";
                    }
                    else
                    {
                        r.Percent = percent;
                        r.Level = BatteryMath.FromPercent(percent);
                        r.Detail = "2.4G";
                    }
                    return r;
                }

                // The dongle is present but the mouse stayed silent: it sleeps
                // between movements. The card keeps its last value on screen.
                r.Sleeping = true;
                r.Error = "设备休眠";
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            finally
            {
                HidNative.CloseHandle(handle);
            }

            return r;
        }

        private static byte[] BuildQuery()
        {
            byte[] buf = new byte[FrameSize];
            buf[0] = ReportId;
            buf[1] = CmdBattery;
            buf[2] = Marker;
            buf[3] = 0x00;
            buf[4] = 0x00;
            buf[5] = 0x4F;
            buf[6] = SubBattery;
            return buf;
        }

        /// <summary>
        /// Byte 6 packs two fields: bit 7 is the charging flag, the low seven bits
        /// are the level. On cable the level is derived from the charging voltage
        /// and races to 100 no matter how full the battery really is — a 52% mouse
        /// read 100 within two minutes of being plugged in — so while charging the
        /// level is discarded rather than shown.
        /// </summary>
        internal static bool TryParse(byte[] frame, out int percent, out bool charging)
        {
            percent = 0;
            charging = false;

            if (frame == null || frame.Length != FrameSize) return false;
            if (frame[0] != ReportId) return false;
            if (frame[1] != Marker) return false;
            if (frame[3] != PayloadLength) return false;
            if (frame[13] != FooterA || frame[14] != FooterB) return false;

            charging = (frame[6] & 0x80) != 0;
            int level = frame[6] & 0x7F;
            if (level > 100) return false;

            percent = level;
            return true;
        }

        private static byte[] WaitFor(IntPtr handle, int timeoutMs)
        {
            // A monotonic clock, not Environment.TickCount: the 32-bit counter
            // wraps negative after 49.7 days of uptime and would skip the loop.
            Stopwatch clock = Stopwatch.StartNew();
            byte[] buf = new byte[FrameSize];

            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                // The first byte of the buffer names the report to read. Leaving
                // it zero asks for report 0, which this device does not have and
                // rejects with ERROR_INVALID_PARAMETER.
                buf[0] = ReportId;

                if (HidNative.HidD_GetFeature(handle, buf, FrameSize))
                {
                    if (buf[0] == ReportId && buf[1] == Marker && buf[3] == PayloadLength)
                    {
                        byte[] reply = new byte[FrameSize];
                        Array.Copy(buf, reply, FrameSize);
                        return reply;
                    }
                }
                Thread.Sleep(8);
            }

            return null;
        }

        /// <summary>
        /// Only the last component of the interface instance changes between
        /// collections (&amp;0000 / &amp;0001), so the prefix identifies one
        /// physical receiver.
        /// </summary>
        private static string InstanceOf(HidDeviceInfo h)
        {
            if (!string.IsNullOrEmpty(h.Path))
            {
                string[] parts = h.Path.Split('#');
                if (parts.Length >= 3)
                {
                    int cut = parts[2].LastIndexOf('&');
                    if (cut > 0) return parts[2].Substring(0, cut).ToUpperInvariant();
                }
            }
            return h.ProductId.ToString("X4");
        }

        private static string Hex(byte[] b, int count)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count && i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
    }
}
