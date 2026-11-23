// Contributed by yiwluoq (GitHub) - merged from PR #3.
using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// ATK 2.4G / USB wired mice using the Compx 17-byte vendor protocol.
    ///
    /// Supported models:
    ///   - ATK A9 Air 大师+ : dongle 373B:1278 ("Wireless mouse 8k dongle-L")
    ///   - ATK X1 Ultimate  : 8K dongles 373B:101B, 373B:11A7 ("ATK Mouse 8K Dongle"),
    ///                        and USB wired PIDs 373B:103D, 373B:11E7, 373B:1286, 373B:12B4
    ///
    /// Only the 0xFF02 vendor collection answers (input/output report length 17).
    /// Headset dongles (e.g. 373B:1262 "ATK N9V2 ULTIMATE") use a different collection
    /// and protocol and are ignored here.
    ///
    /// Protocol verified on real hardware and ATK HUB (Compx 17-byte frame):
    ///
    ///   query : 17-byte frame, sent with HidD_SetOutputReport (HID SET_REPORT)
    ///           or WriteFile (interrupt OUT) fallback.
    ///
    ///     query[0]      = 0x08  vendor output report id
    ///     query[1]      = opcode: 0x03 = GetWirelessMouseOnline, 0x04 = GetBatteryLevel
    ///     query[2..15]  = 0x00  body
    ///     query[16]     = checksum = (0x4D - sum(query[1..15])) &amp; 0xFF
    ///                     -> 0x4A for opcode 0x03, 0x49 for opcode 0x04
    ///
    ///   0x03 reply (dongle online check):
    ///     08 03 00 00 00 01 SS R3 R2 R1 ... CS
    ///     When reply[2] == 0x00 and reply[5] >= 0x01 and reply[6] == 0x00,
    ///     the paired mouse is asleep / offline.
    ///
    ///   0x04 reply (battery query):
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
    /// </summary>
    public class AtkMouseProvider : IDeviceProvider
    {
        private const ushort AtkVid = 0x373B;
        private const ushort A9AirPid = 0x1278;
        private const ushort X1UltimateDonglePid = 0x101B;
        private const ushort X1UltimateDongleAltPid = 0x11A7;
        private const ushort X1UltimateWiredPid = 0x103D;
        private const ushort X1UltimateV2WiredPid = 0x11E7;
        private const ushort X1UltimateV2PlusWiredPid = 0x1286;
        private const ushort X1UltimateV2SilentWiredPid = 0x12B4;
        private const ushort VendorPage = 0xFF02;

        private const int FrameSize = 17;
        private const byte ReportId = 0x08;
        private const byte CmdWirelessOnline = 0x03;
        private const byte CmdBattery = 0x04;
        private const byte FrameSum = 0x4D;

        private const int PercentIndex = 6;
        private const int ChargingIndex = 7;
        private const int MillivoltIndex = 8;

        private struct MouseModelInfo
        {
            public string Model;
            public string ShortModel;
            public string Name;
            public bool IsWired;
        }

        public string Name { get { return "ATK HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();
            foreach (HidDeviceInfo h in catalog)
            {
                if (h == null) continue;
                if (h.VendorId != AtkVid) continue;
                MouseModelInfo info;
                if (!TryGetModelInfo(h.ProductId, out info)) continue;
                if (h.UsagePage != VendorPage) continue;     // vendor collection
                if (h.InputReportLength != FrameSize) continue;
                if (h.OutputReportLength != FrameSize) continue;
                if (string.IsNullOrEmpty(h.Path)) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;
                MouseModelInfo info;
                if (!TryGetModelInfo(h.ProductId, out info)) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "atk-" + kv.Key;
                d.Brand = "ATK";
                d.Model = info.Model;
                d.Name = info.Name;
                d.ShortModel = info.ShortModel;
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

            if (device == null || string.IsNullOrEmpty(device.HidPath))
            {
                r.Error = "设备路径无效";
                return r;
            }

            MouseModelInfo info;
            bool isWired = TryGetModelInfo(device.ProductId, out info) && info.IsWired;

            try
            {
                byte[] cmd = NewQuery(CmdBattery);

                using (HidSession s = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    s.Flush(30);

                    if (!isWired)
                    {
                        byte[] onlineCmd = NewQuery(CmdWirelessOnline);
                        if (s.SetOutputReport(onlineCmd) || s.Write(onlineCmd, 300))
                        {
                            byte[] onlineResp = WaitFor(s, ReportId, CmdWirelessOnline, 200);
                            if (onlineResp != null &&
                                onlineResp[2] == 0x00 &&
                                onlineResp[5] >= 0x01 &&
                                onlineResp[6] == 0x00)
                            {
                                r.Raw = Hex(onlineResp);
                                r.Sleeping = true;
                                r.Error = "设备休眠";
                                return r;
                            }
                        }
                    }

                    byte[] resp = null;

                    for (int attempt = 0; attempt < 3 && resp == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);

                        if (!(s.SetOutputReport(cmd) || s.Write(cmd, 300)))
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
                    string prefix = isWired ? "USB 有线" : "2.4G";
                    r.Detail = millivolts > 0 ? prefix + " " + millivolts + " mV" : prefix;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        private static bool TryGetModelInfo(ushort pid, out MouseModelInfo info)
        {
            switch (pid)
            {
                case A9AirPid:
                    info = new MouseModelInfo { Model = "A9 AIR+", ShortModel = "A9 AIR+", Name = "ATK A9 Air 大师+", IsWired = false };
                    return true;
                case X1UltimateDonglePid:
                case X1UltimateDongleAltPid:
                    info = new MouseModelInfo { Model = "X1 ULTIMATE", ShortModel = "X1 ULT", Name = "ATK X1 Ultimate", IsWired = false };
                    return true;
                case X1UltimateWiredPid:
                    info = new MouseModelInfo { Model = "X1 ULTIMATE", ShortModel = "X1 ULT", Name = "ATK X1 Ultimate", IsWired = true };
                    return true;
                case X1UltimateV2WiredPid:
                    info = new MouseModelInfo { Model = "X1 ULTIMATE", ShortModel = "X1 ULT", Name = "ATK X1 Ultimate 2.0", IsWired = true };
                    return true;
                case X1UltimateV2PlusWiredPid:
                    info = new MouseModelInfo { Model = "X1 ULTIMATE", ShortModel = "X1 ULT", Name = "ATK X1 Ultimate 2.0 +", IsWired = true };
                    return true;
                case X1UltimateV2SilentWiredPid:
                    info = new MouseModelInfo { Model = "X1 ULTIMATE", ShortModel = "X1 ULT", Name = "ATK X1 Ultimate 2.0 Silent", IsWired = true };
                    return true;
                default:
                    info = default(MouseModelInfo);
                    return false;
            }
        }

        /// <summary>
        /// Builds a 17-byte query frame. The checksum covers the 16 payload bytes after
        /// the report id; the frame only checks out when they sum to 0x4D mod 256.
        /// </summary>
        private static byte[] NewQuery(byte opcode)
        {
            byte[] cmd = new byte[FrameSize];
            cmd[0] = ReportId;
            cmd[1] = opcode;
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
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                int remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
                byte[] p = s.Read(Math.Max(1, remaining));
                if (p == null) break;
                if (p.Length < FrameSize) continue;
                if (p[0] != rid || p[1] != cmd) continue;
                if (p[2] != 0x00) continue;
                if (p[FrameSize - 1] != Checksum(p)) continue;
                if (cmd == CmdBattery &&
                    p[5] == 0 && p[PercentIndex] == 0 &&
                    p[MillivoltIndex] == 0 && p[MillivoltIndex + 1] == 0)
                {
                    continue;
                }
                return p;
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
