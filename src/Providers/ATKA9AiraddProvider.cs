using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// ATK A9 Air 大师+ 2.4G mouse.
    ///
    /// Dongle: VID 0x373B, PID 0x1278, product string "Wireless mouse 8k dongle-L",
    /// manufacturer "Compx". Only its vendor collection answers: usage page 0xFF02,
    /// input report length 17, output report length 17. The other ATK dongle on this
    /// machine (VID 0x373B, PID 0x1262, "ATK N9V2 ULTIMATE") is a headset and is
    /// rejected here on purpose.
    ///
    /// Protocol verified on the real dongle (2026-11-01):
    ///
    ///   query : 17-byte frame, sent with HidD_SetOutputReport (HID SET_REPORT).
    ///           WriteFile on this collection is rejected by the firmware, so the
    ///           interrupt-OUT side is not an option.
    ///
    ///     query[0]      = 0x08  vendor output report id
    ///     query[1]      = 0x04  opcode: battery query
    ///     query[2..15]  = 0x00  body
    ///     query[16]     = checksum = (0x4D - sum(query[1..15])) & 0xFF
    ///                     -> 0x49 for opcode 0x04 with an all-zero body
    ///
    ///   reply : 17-byte frame read back from the same handle with ReadFile
    ///           (overlapped, ~700 ms). Verified capture:
    ///
    ///     08 04 00 00 00 02 64 00 10 DD 00 00 00 00 00 00 F6
    ///                       ^^ ^^ ^^^^^                  ^^
    ///                       |  |  |                      +-- checksum
    ///                       |  |  +------------------------- millivolts, big endian
    ///                       |  |                             (0x10DD = 4317 mV)
    ///                       |  +---------------------------- charging flag:
    ///                       |                                0x01 charging,
    ///                       |                                0x00 discharging
    ///                       +------------------------------- percent (0x64 = 100)
    ///
    ///   The checksum rule is symmetric: bytes 1..16 of either 17-byte frame sum
    ///   to 0x4D modulo 256.
    ///
    /// A second, write-free path was observed but deliberately NOT used. The 0xFF04
    /// collection exposes an 8-byte feature report id 0x06 which answered
    /// 06 00 00 64 64 64 35 40, and byte 3 (0x64) looked like the same percentage.
    /// Every capture was taken at 100%, so byte 3 cannot be told apart from the
    /// other constant 0x64 fields without running the battery down; wiring it as a
    /// fallback could silently turn a capacity constant into a reading. The 0x04
    /// query above is the verified path.
    /// </summary>
    public class AtkMouseProvider : IDeviceProvider
    {
        private const ushort AtkVid = 0x373B;
        private const ushort A9AirPid = 0x1278;
        private const ushort VendorPage = 0xFF02;

        private const int FrameSize = 17;
        private const byte ReportId = 0x08;
        private const byte CmdBattery = 0x04;
        private const byte FrameSum = 0x4D;

        private const int PercentIndex = 6;
        private const int ChargingIndex = 7;
        private const int MillivoltIndex = 8;

        public string Name { get { return "ATK HID"; } }

        public List<DeviceInfo> Discover()
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            List<HidDeviceInfo> all = HidNative.Enumerate();

            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();
            foreach (HidDeviceInfo h in all)
            {
                if (h.VendorId != AtkVid) continue;
                if (h.ProductId != A9AirPid) continue;       // 1262 is the headset dongle
                if (h.UsagePage != VendorPage) continue;     // vendor collection
                if (h.InputReportLength != FrameSize) continue;
                if (h.OutputReportLength != FrameSize) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;

                DeviceInfo d = new DeviceInfo();
                d.Id = "atk-" + kv.Key;
                d.Brand = "ATK";
                d.Model = "A9 Air 大师+";
                d.Name = "ATK A9 Air 大师+";
                d.ShortModel = "A9 AIR+";
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
            r.Connection = ConnectionProbe.ForDevice(device != null ? device.HidPath : null,
                                                     device != null ? device.VendorId : (ushort)0,
                                                     device != null ? device.ProductId : (ushort)0);

            try
            {
                byte[] cmd = NewQuery();

                using (HidSession s = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    s.Flush(30);

                    byte[] resp = null;

                    for (int attempt = 0; attempt < 3 && resp == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);

                        if (!s.SetOutputReport(cmd))
                        {
                            r.Error = "发送失败（设备被占用？）";
                            return r;
                        }

                        resp = WaitFor(s, ReportId, CmdBattery, 700);
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
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    int millivolts = (resp[MillivoltIndex] << 8) | resp[MillivoltIndex + 1];

                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    r.Charging = resp[ChargingIndex] == 0x01;
                    r.Online = true;
                    r.Detail = "2.4G " + millivolts + " mV";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        /// <summary>
        /// Builds the battery query. The checksum covers the 16 payload bytes after
        /// the report id; the frame only checks out when they sum to 0x4D mod 256.
        /// </summary>
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

        private static byte[] WaitFor(HidSession s, byte rid, byte cmd, int timeoutMs)
        {
            for (int i = 0; i < 12; i++)
            {
                byte[] p = s.Read(Math.Max(60, timeoutMs / 4));
                if (p == null) return null;
                if (p.Length > MillivoltIndex + 1 && p[0] == rid && p[1] == cmd) return p;
            }
            return null;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
    }
}
