using System;
using System.Collections.Generic;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// ZOWIE DW-series wireless mouse behind a DW receiver (4K enhanced or 1K).
    ///
    /// Receiver: VID 0x04A5, PID 0x800A. The wired mouse enumerates separately as
    /// 0x800D. The receiver exposes two vendor collections:
    ///
    ///   commands   usage page 0xFF03, output report length 16
    ///   replies    usage page 0xFF04, input report length 16
    ///
    /// Commands must leave through the interrupt OUT pipe (WriteFile); this
    /// firmware never answers a HIDD_SetOutputReport.
    ///
    ///   request  16 bytes: 08 B4 CC 00 ...          CC = command, rest zero
    ///   reply    16 bytes: 09 HH LL xx C8 ...      success marker 0xC8
    ///
    /// Battery command 0x15 returns the cached pack voltage as a big-endian
    /// 16-bit value in 0.01 V steps: mV = (reply[1] << 8 | reply[2]) * 10.
    /// There is no native percentage and no charge flag in this protocol, so the
    /// percentage is interpolated from the voltage using the thresholds the
    /// mouse itself prints through its LED.
    ///
    /// Two read-only helper commands decide whether the mouse is really linked.
    /// After the mouse is switched off the receiver keeps answering with stale
    /// values, so one voltage read cannot tell "3.88 V" from "3.88 V an hour
    /// ago". The mouse firmware version is the observable that does clear: it
    /// reads 0000 while the mouse is away.
    ///
    ///   0x40 receiver version   reply 09 85 11 00 C8 ...  (BCD, e.g. 8511)
    ///   0x25 mouse firmware     reply 09 25 26 C8 00 ...  (BCD, e.g. 2526)
    ///
    /// Only these three commands are ever sent. Other ZOWIE commands make the
    /// receiver reboot and drop its pairing.
    ///
    /// Verified on real hardware (2026-11-24), receiver 8511 + mouse firmware
    /// 2526:
    ///
    ///   0x40 -> 09 85 11 00 C8 00 ...
    ///   0x25 -> 09 25 26 C8 00 ...
    ///   0x15 -> 09 01 AF 00 C8 00 ...      -> 4310 mV = 100 %
    /// </summary>
    public class ZowieProvider : IDeviceProvider
    {
        private const ushort ZowieVid = 0x04A5;
        private const ushort ReceiverPid = 0x800A;
        private const ushort WiredMousePid = 0x800D;
        private const ushort CommandPage = 0xFF03;
        private const ushort ReplyPage = 0xFF04;
        private const int FrameLength = 16;

        private const byte ReportId = 0x08;
        private const byte ReplyId = 0x09;
        private const byte CommandPrefix = 0xB4;
        private const byte CmdReceiverVersion = 0x40;
        private const byte CmdMouseVersion = 0x25;
        private const byte CmdVoltage = 0x15;
        private const byte SuccessMarker = 0xC8;

        // The reply never echoes the command, so a query has to be followed by a
        // read window of its own. The receiver answers in a few milliseconds when
        // it can answer at all; the window only covers a busy USB stack.
        private const int ReadTimeoutMs = 250;
        private const int ReplyWindowMs = 1200;

        private static readonly int[] VoltageAnchors = { 4200, 4000, 3890, 3760, 3680, 3500 };
        private static readonly int[] PercentAnchors = { 100, 75, 50, 25, 10, 0 };

        private readonly object _mapLock = new object();
        private readonly Dictionary<string, string> _replyPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Name { get { return "ZOWIE"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            // Plugging the mouse in over USB switches it to its own PID. That is
            // the only reliable "on the charger" signal: the wireless reply
            // carries no charge state at all.
            bool wiredPresent = false;

            Dictionary<string, HidDeviceInfo> commands = new Dictionary<string, HidDeviceInfo>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HidDeviceInfo> replies = new Dictionary<string, HidDeviceInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != ZowieVid) continue;

                if (h.ProductId == WiredMousePid)
                {
                    wiredPresent = true;
                    continue;
                }
                if (h.ProductId != ReceiverPid) continue;

                string key = PairKey(h.Path);

                if (h.UsagePage == CommandPage && h.OutputReportLength == FrameLength && h.InputReportLength == 0)
                {
                    if (!commands.ContainsKey(key)) commands.Add(key, h);
                }
                else if (h.UsagePage == ReplyPage && h.InputReportLength == FrameLength && h.OutputReportLength == 0)
                {
                    if (!replies.ContainsKey(key)) replies.Add(key, h);
                }
            }

            // A machine can expose more than one DW receiver (a 1K one plus a 4K
            // enhanced one). Pick the first pair that actually answers, so a dead
            // instance does not shadow the live mouse.
            string chosen = null;
            foreach (string k in commands.Keys)
            {
                if (!replies.ContainsKey(k)) continue;
                if (chosen == null) chosen = k;
                if (commands.Count == 1) break;
                if (Answers(commands[k].Path, replies[k].Path)) { chosen = k; break; }
            }
            if (chosen == null) return found;

            HidDeviceInfo cmdInfo = commands[chosen];
            HidDeviceInfo replyInfo = replies[chosen];

            DeviceInfo d = new DeviceInfo();
            d.Id = "zowie-dw";
            d.Brand = "ZOWIE";
            d.Model = "DW 系列";
            d.Name = "ZOWIE 无线鼠标";
            d.ShortModel = "ZOWIE DW";
            d.Kind = DeviceKind.Mouse;
            d.VendorId = cmdInfo.VendorId;
            d.ProductId = cmdInfo.ProductId;
            d.HidPath = cmdInfo.Path;
            d.ProviderName = Name;
            d.IsCharging = wiredPresent;

            lock (_mapLock)
            {
                _replyPaths.Clear();
                _replyPaths[d.Id] = replyInfo.Path;
            }

            found.Add(d);
            return found;
        }

        public DeviceReading Read(DeviceInfo d)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;
            r.Charging = d.IsCharging;
            // Without the cable there is no charge bit at all, and charging on a
            // dock cannot be told apart from sitting on the desk.
            r.ChargingKnown = d.IsCharging;
            r.Detail = d.IsCharging ? "USB 已连接（推定充电）" : "2.4G";

            string replyPath;
            lock (_mapLock)
            {
                if (!_replyPaths.TryGetValue(d.Id, out replyPath)) replyPath = null;
            }
            if (replyPath == null)
            {
                r.Error = "未找到接收器回复接口";
                return r;
            }

            try
            {
                using (HidSession cmd = new HidSession(d.HidPath, 0, FrameLength))
                using (HidSession reply = new HidSession(replyPath, FrameLength, 0, true))
                {
                    reply.Flush(20);

                    byte[] receiver = Query(cmd, reply, CmdReceiverVersion, true);
                    if (receiver == null)
                    {
                        r.Sleeping = true;
                        r.Error = "接收器未应答";
                        return r;
                    }
                    string receiverVersion = Version(receiver);

                    byte[] before = Query(cmd, reply, CmdMouseVersion, false);
                    if (before == null)
                    {
                        r.Sleeping = true;
                        r.Error = "鼠标未应答";
                        return r;
                    }
                    string beforeVersion = Version(before);
                    if (beforeVersion == "0000")
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        r.Detail = "接收器 " + receiverVersion;
                        return r;
                    }

                    byte[] voltage = Query(cmd, reply, CmdVoltage, true);
                    if (voltage == null)
                    {
                        r.Sleeping = true;
                        r.Error = "电压读取超时";
                        return r;
                    }
                    int millivolts = ((voltage[1] << 8) | voltage[2]) * 10;
                    if (millivolts == 0)
                    {
                        r.Sleeping = true;
                        r.Error = "电压缓存为空";
                        return r;
                    }
                    if (millivolts < 3000 || millivolts > 5000)
                    {
                        r.Online = true;
                        r.Raw = Hex(voltage);
                        r.Error = "电压读数超出范围";
                        return r;
                    }

                    // Read the firmware version again: the receiver happily serves
                    // the stale voltage of a mouse that shut down mid-query.
                    byte[] after = Query(cmd, reply, CmdMouseVersion, false);
                    if (after == null || Version(after) == "0000" || Version(after) != beforeVersion)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        r.Detail = "接收器 " + receiverVersion;
                        return r;
                    }

                    int percent = PercentFromMillivolts(millivolts);
                    r.Raw = Hex(voltage) + " | fw " + beforeVersion + " rx " + receiverVersion;
                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);
                    r.Online = true;
                    r.Detail = "2.4G · " + millivolts + " mV";
                }
            }
            catch (Exception ex)
            {
                r.Sleeping = true;
                r.Error = ex.Message;
            }

            return r;
        }

        /// <summary>
        /// Sends one 16-byte command over the interrupt OUT pipe and returns the
        /// first matching reply frame, or null. Only an allow-listed set of
        /// read-only commands may be sent.
        /// </summary>
        private static byte[] Query(HidSession cmd, HidSession reply, byte command, bool markerOnByteFour)
        {
            if (command != CmdReceiverVersion && command != CmdMouseVersion && command != CmdVoltage)
                throw new ArgumentOutOfRangeException("command");

            byte[] request = new byte[FrameLength];
            request[0] = ReportId;
            request[1] = CommandPrefix;
            request[2] = command;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) Thread.Sleep(80);

                if (!cmd.Write(request, 400)) continue;

                long deadline = NowMs() + ReplyWindowMs;
                while (NowMs() < deadline)
                {
                    byte[] p = reply.Read(ReadTimeoutMs);
                    if (p == null) continue;
                    if (p.Length < 5) continue;
                    if (p[0] != ReplyId) continue;
                    if (p[markerOnByteFour ? 4 : 3] != SuccessMarker) continue;
                    if (command == CmdVoltage && p[1] == 0xE6) continue;
                    return p;
                }
            }

            return null;
        }

        /// <summary>
        /// True when the pair answers the receiver-version query. Used only to
        /// choose between several receiver instances during discovery.
        /// </summary>
        private static bool Answers(string commandPath, string replyPath)
        {
            try
            {
                using (HidSession cmd = new HidSession(commandPath, 0, FrameLength))
                using (HidSession reply = new HidSession(replyPath, FrameLength, 0, true))
                {
                    reply.Flush(20);
                    return Query(cmd, reply, CmdReceiverVersion, true) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        /// <summary>Pair key: the interface instance without the collection suffix.</summary>
        private static string PairKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int cut = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
            return cut < 0 ? path.ToLowerInvariant() : path.Substring(0, cut).ToLowerInvariant();
        }

        private static string Version(byte[] frame)
        {
            return frame[1].ToString("X2") + frame[2].ToString("X2");
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

        /// <summary>
        /// Voltage -> percentage, linearly interpolated between the anchors the
        /// mouse firmware uses for its own LED steps. The pack is between these
        /// points about 99 % of the time; the curve is an estimate, not a reading.
        /// </summary>
        private static int PercentFromMillivolts(int mv)
        {
            if (mv >= VoltageAnchors[0]) return 100;
            if (mv <= VoltageAnchors[VoltageAnchors.Length - 1]) return 0;

            for (int i = 0; i < VoltageAnchors.Length - 1; i++)
            {
                int high = VoltageAnchors[i];
                int low = VoltageAnchors[i + 1];
                if (mv < low || mv > high) continue;

                double t = (double)(mv - low) / (double)(high - low);
                int value = (int)Math.Round(PercentAnchors[i + 1] + t * (PercentAnchors[i] - PercentAnchors[i + 1]));
                if (value < 0) value = 0;
                if (value > 100) value = 100;
                return value;
            }

            return 0;
        }
    }
}
