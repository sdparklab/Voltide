using System;
using System.Collections.Generic;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Sony INZONE H5 headset (VID 0x054C, PID 0x0EBF).
    ///
    /// The dongle exposes several HID collections; the one that carries the
    /// headset's control protocol is usage page 0xFF04, 64-byte input and output
    /// reports (on this machine the "INZONE H5 - Chat" collection). The Airoha
    /// vendor channel on 0xFF13 is what INZONE Hub uses for audio tuning and is
    /// not needed here.
    ///
    /// Frames are Sony HCI (opcode 0xFC00) carried over that report:
    ///
    ///   byte 0        0x02              HID report id
    ///   byte 1        frame length      index of the last byte before checksum
    ///   byte 2        0x01              HCI command
    ///   byte 3..4     00 FC             opcode 0xFC00, little endian
    ///   byte 5        8 + payload len
    ///   byte 6..7     96 C3             protocol marker
    ///   byte 8        address           0x41 headset, 0x21 dongle
    ///   byte 9        event id
    ///   byte 10       type              0x01 GET, 0x10 RETURN, 0xA0 NOTIFY
    ///   byte 11..12   transaction id    little endian
    ///   byte 13..     payload
    ///   next          checksum           sum(bytes 6..frame length) & 0xFF
    ///
    /// Battery query: event 0x04, type GET, address 0x41, payload empty.
    ///
    ///   request  02 0C 01 00 FC 08 96 C3 41 04 01 01 00 A0, padded to 64
    ///
    /// The reply comes back as a 0x04 / 0xFF response:
    ///
    ///   byte 8        0x14   host address
    ///   byte 9        0x04   event id echo
    ///   byte 10       0x10   RETURN (0xA0 for a notification)
    ///   byte 13..14   payload: [0] != 0 while charging, [1] percentage
    ///                 (0xFF = unknown / headset off, >100 = bad frame)
    ///   checksum          sum(bytes 5..frame length) & 0xFF
    ///
    /// Verified on real hardware (2026-11-24):
    ///
    ///   reply 02 0E 04 FF 0B 00 96 C3 14 04 10 01 00 00 64 E6 ...
    ///                                              ^^ ^^ = not charging, 100 %
    /// </summary>
    public class InzoneH5Provider : IDeviceProvider
    {
        private const ushort SonyVid = 0x054C;
        private const ushort InzoneH5Pid = 0x0EBF;
        private const ushort SonyPage = 0xFF04;
        private const int ReportLength = 64;

        private const byte ReportId = 0x02;
        private const byte HciCommand = 0x01;
        private const byte OpcodeLow = 0x00;
        private const byte OpcodeHigh = 0xFC;
        private const byte ProtocolLow = 0x96;
        private const byte ProtocolHigh = 0xC3;
        private const byte AddressHeadset = 0x41;
        private const byte AddressHost = 0x14;

        private const byte EventBattery = 0x04;
        private const byte TypeGet = 0x01;
        private const byte TypeReturn = 0x10;
        private const byte TypeNotify = 0xA0;

        private const int ReadTimeoutMs = 250;
        private const int ReplyWindowMs = 900;

        public string Name { get { return "Sony INZONE"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != SonyVid) continue;
                if (h.ProductId != InzoneH5Pid) continue;
                if (h.UsagePage != SonyPage) continue;
                if (h.InputReportLength != ReportLength) continue;
                if (h.OutputReportLength != ReportLength) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "sony-inzone-h5";
                d.Brand = "Sony";
                d.Model = "INZONE H5";
                d.Name = "Sony INZONE H5";
                d.ShortModel = "INZONE H5";
                d.Kind = DeviceKind.Headset;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;

                found.Add(d);
                break; // one control collection per dongle
            }

            return found;
        }

        public DeviceReading Read(DeviceInfo d)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;
            r.Detail = "2.4G · 索尼音频";

            try
            {
                using (HidSession s = new HidSession(d.HidPath, ReportLength, ReportLength))
                {
                    s.Flush(30);

                    byte[] request = BuildBatteryRequest();
                    byte[] reply = null;

                    for (int attempt = 0; attempt < 3 && reply == null; attempt++)
                    {
                        if (attempt > 0) Thread.Sleep(120);
                        if (!s.Write(request, 400)) continue;
                        reply = WaitForBattery(s);
                    }

                    if (reply == null)
                    {
                        r.Sleeping = true;
                        r.Error = "耳机未应答（可能已关机）";
                        return r;
                    }

                    r.Raw = Hex(reply, 16);

                    int charging = reply[13];
                    int percent = reply[14];

                    if (percent == 0xFF)
                    {
                        r.Sleeping = true;
                        r.Error = "耳机未连接";
                        return r;
                    }
                    if (percent > 100)
                    {
                        r.Online = true;
                        r.Error = "读数异常";
                        return r;
                    }

                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);
                    r.Charging = charging != 0;
                    r.ChargingKnown = true;
                    r.Online = true;
                }
            }
            catch (Exception ex)
            {
                r.Sleeping = true;
                r.Error = ex.Message;
            }

            return r;
        }

        private static byte[] BuildBatteryRequest()
        {
            byte[] frame = new byte[ReportLength];
            frame[0] = ReportId;
            frame[1] = 0x0C;              // last byte before the checksum
            frame[2] = HciCommand;
            frame[3] = OpcodeLow;
            frame[4] = OpcodeHigh;
            frame[5] = 0x08;              // 8 + empty payload
            frame[6] = ProtocolLow;
            frame[7] = ProtocolHigh;
            frame[8] = AddressHeadset;
            frame[9] = EventBattery;
            frame[10] = TypeGet;
            frame[11] = 0x01;             // transaction id 1, little endian
            frame[12] = 0x00;
            frame[13] = Checksum(frame, 6);
            return frame;
        }

        private static byte Checksum(byte[] frame, int from)
        {
            int sum = 0;
            for (int i = from; i <= frame[1]; i++) sum += frame[i];
            return (byte)(sum & 0xFF);
        }

        private static byte[] WaitForBattery(HidSession s)
        {
            long deadline = NowMs() + ReplyWindowMs;
            while (NowMs() < deadline)
            {
                byte[] p = s.Read(ReadTimeoutMs);
                if (p == null) continue;
                if (!IsBatteryReply(p)) continue;
                return p;
            }
            return null;
        }

        private static bool IsBatteryReply(byte[] p)
        {
            if (p.Length < 16) return false;
            if (p[0] != ReportId) return false;
            if (p[2] != 0x04 || p[3] != 0xFF) return false;   // HCI response opcode
            if (p[8] != AddressHost) return false;
            if (p[9] != EventBattery) return false;
            if (p[10] != TypeReturn && p[10] != TypeNotify) return false;

            int headerLength = p[1];
            if (headerLength < 14) return false;              // two payload bytes
            if (headerLength + 1 >= p.Length) return false;
            if (Checksum(p, 5) != p[headerLength + 1]) return false;
            return true;
        }

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private static string Hex(byte[] b, int count)
        {
            if (b == null) return "";
            int n = Math.Min(count, b.Length);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
    }
}
