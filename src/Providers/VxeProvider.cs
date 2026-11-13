// Contributed for VXE R1 (Star Flash / 星闪, 2.4G), verified on real hardware.
using System;
using System.Collections.Generic;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// VXE (VGN 旗下) R1 Star Flash — Compx-family mouse dongle.
    ///
    /// Dongle: VID 0x373B, PID 0x10C9, product string "NK mouse NANO dongle",
    /// manufacturer "Compx". The same chipset family serves ATK A9 Air, IMECOO P1
    /// and MCHOSE, so the frame shape matches ATK's adapter; only the VID/PID and
    /// the product name differ.
    ///
    /// The dongle exposes two vendor collections on usage page 0xFF02:
    /// one with 20-byte reports (the keyboard/multi-purpose side, silent) and one
    /// with 17-byte reports (the mouse). Only the 17-byte one answers the battery
    /// query, exactly like ATK A9 Air.
    ///
    ///   query : 17-byte frame, sent with HidD_SetOutputReport (HID SET_REPORT).
    ///           WriteFile is rejected by the firmware, so interrupt-OUT is not an
    ///           option — the same quirk ATK and MCHOSE have.
    ///
    ///     query[0]     = 0x08  vendor output report id
    ///     query[1]     = 0x04  opcode: battery query
    ///     query[2..15] = 0x00  body
    ///     query[16]    = checksum = (0x4D - sum(query[1..15])) &amp; 0xFF
    ///
    ///   reply : 17-byte frame, read back on the same handle.
    ///           Live capture (40%, 3664 mV, discharging):
    ///
    ///     08 04 00 00 00 02 28 00 0E 50 00 00 00 00 00 00 C1
    ///                       ^^ ^^ ^^^^^                  ^^
    ///                       |  |  |                      +-- checksum
    ///                       |  |  +------------------------- millivolts, big endian
    ///                       |  +---------------------------- charging flag
    ///                       +------------------------------- percent (0x28 = 40)
    ///
    ///   The checksum rule is symmetric: bytes 1..16 sum to 0x4D mod 256.
    ///
    /// The receiver echoes the query frame back (same report id and opcode, all
    /// zero payload) before the real reply, so the echo has to be skipped or the
    /// battery field would be read out of our own query.
    /// </summary>
    public class VxeProvider : IDeviceProvider
    {
        private const ushort CompxVid = 0x373B;
        private const ushort R1StarFlashPid = 0x10C9;
        private const ushort VendorPage = 0xFF02;

        private const int FrameSize = 17;
        private const byte ReportId = 0x08;
        private const byte CmdBattery = 0x04;
        private const byte FrameSum = 0x4D;

        private const int PercentIndex = 6;
        private const int ChargingIndex = 7;
        private const int MillivoltIndex = 8;

        public string Name { get { return "VXE HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();
            List<string> order = new List<string>();
            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != CompxVid) continue;
                if (h.ProductId != R1StarFlashPid) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.InputReportLength != FrameSize) continue;   // 20-byte sibling is silent
                if (h.OutputReportLength != FrameSize) continue;
                if (string.IsNullOrEmpty(h.Path)) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (candidates.ContainsKey(key)) continue;
                candidates.Add(key, h);
                order.Add(key);
            }

            foreach (string key in order)
            {
                HidDeviceInfo h = candidates[key];

                DeviceInfo d = new DeviceInfo();
                d.Id = "vxe-" + key;
                d.Brand = "VXE";
                d.Model = "R1";
                d.ShortModel = "R1";
                d.Name = "VXE R1 星闪版";
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
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;

            try
            {
                byte[] cmd = NewQuery();

                using (HidSession s = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    s.Flush(30);

                    byte[] resp = null;

                    for (int attempt = 0; attempt < 3 && resp == null; attempt++)
                    {
                        if (attempt > 0) Thread.Sleep(120);

                        if (!s.SetOutputReport(cmd))
                        {
                            r.Error = "发送失败（设备被占用？）";
                            return r;
                        }

                        resp = WaitFor(s, 800);
                    }

                    if (resp == null)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }

                    r.Raw = Hex(resp);

                    int level = resp[PercentIndex];
                    if (level > 100)
                    {
                        r.Online = true;
                        r.Error = "读数异常";
                        return r;
                    }

                    int millivolts = (resp[MillivoltIndex] << 8) | resp[MillivoltIndex + 1];

                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    r.Charging = resp[ChargingIndex] == 0x01;
                    r.Online = true;
                    r.Detail = millivolts > 0 ? "2.4G " + millivolts + " mV" : "2.4G";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        private static byte[] NewQuery()
        {
            byte[] cmd = new byte[FrameSize];
            cmd[0] = ReportId;
            cmd[1] = CmdBattery;
            cmd[FrameSize - 1] = Checksum(cmd);
            return cmd;
        }

        private static byte Checksum(byte[] frame)
        {
            int sum = 0;
            for (int i = 1; i < frame.Length - 1; i++) sum += frame[i];
            return (byte)((FrameSum - sum) & 0xFF);
        }

        /// <summary>
        /// Accepts only a reply that carries data. The receiver mirrors the query
        /// back first, and that echo has an all-zero body.
        /// </summary>
        private static byte[] WaitFor(HidSession s, int timeoutMs)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                int remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
                byte[] p = s.Read(Math.Max(1, remaining));
                if (p == null) break;
                if (p.Length <= MillivoltIndex + 1) continue;
                if (p[0] != ReportId || p[1] != CmdBattery) continue;
                if (p[16] != Checksum(p)) continue;      // echo and live frames both check out
                if (p[PercentIndex] > 100) continue;
                if (p[2] == 0 && p[3] == 0 && p[4] == 0 && p[5] == 0 && p[MillivoltIndex] == 0) continue;
                return p;
            }
            return null;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
    }
}
