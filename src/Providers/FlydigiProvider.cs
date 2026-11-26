using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// 飞智 Vader 5 Pro（dongle 37D7:2401）2.4G 手柄。
    ///
    /// 电量取自设备信息应答（cmd 0x01）里的原始值，换算沿用 flydigictl 对 Vader 3 Pro
    /// 公开的记录：原始值 98 ≈ 0%，114 ≈ 100%（每格 6.25%）。
    ///
    ///   请求（33 字节，首字节是报告 ID）：00 5A A5 01 02 03 06 00…
    ///   应答：00 5A A5 01 01 00 | 82 <state> 00 00 00 00 <x> 45 01 00 [BAT] 54 <c> <c> 35 17 …
    ///                                        ^^^^^^                          ^^^
    ///                                        state: 01 放电 / 02 充电          原始电量
    ///
    /// 注意：这个 dongle 对 XInput 恒定回 BatteryType=1 / Level=3（"有线、满"），
    /// 所以那个通道拿不到任何电量信息，只能走厂商 HID 集合。
    /// </summary>
    public class FlydigiProvider : IDeviceProvider
    {
        private const ushort Vid = 0x37D7;
        private const ushort Pid = 0x2401;
        private const ushort Page = 0xFFA0;
        private const int FrameLength = 33;
        private const int BatteryIndex = 12;
        private const int StateIndex = 7;
        private const int LevelMin = 0;
        private const int LevelMax = 6;

        public string Name { get { return "Flydigi HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != Vid || h.ProductId != Pid) continue;
                if (h.UsagePage != Page || h.InputReportLength < FrameLength) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "flydigi-" + h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                d.Brand = "FLYDIGI";
                d.Model = "Vader 5 Pro";
                d.ShortModel = "Vader 5 Pro";
                d.Name = "FLYDIGI Vader 5 Pro";
                d.Kind = DeviceKind.Other;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);
                break;
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Level = BatteryLevel.Unknown;

            if (device == null || string.IsNullOrEmpty(device.HidPath))
            {
                r.Error = "没有接收器";
                return r;
            }

            try
            {
                using (HidSession s = new HidSession(device.HidPath, FrameLength, FrameLength))
                {
                    s.Flush(30);
                    byte[] frame = BuildInfoRequest();

                    byte[] reply = null;
                    for (int attempt = 0; attempt < 3 && reply == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);
                        if (!s.Write(frame, 800)) continue;
                        reply = WaitReply(s, 700);
                    }

                    if (reply == null)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }

                    r.Raw = ToHex(reply);
                    int level;
                    bool charging;
                    if (!TryParse(reply, out level, out charging))
                    {
                        r.Error = "电量应答无效";
                        return r;
                    }

                    r.Percent = PercentFromLevel(level);
                    r.Level = BatteryMath.FromPercent(r.Percent.Value);
                    r.Charging = charging;
                    r.ChargingKnown = true;
                    r.Online = true;
                    r.Detail = "2.4G " + level + "/6 格";
                    ReadingStore.Save(device.Id, r.Percent.Value, charging, true, r.Raw);
                    return r;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        internal static byte[] BuildInfoRequest()
        {
            byte[] f = new byte[FrameLength];
            f[1] = 0x5A;
            f[2] = 0xA5;
            f[3] = 0x01;   // cmd: device info
            f[4] = 0x02;   // length
            f[5] = 0x03;
            f[6] = (byte)((0x01 + 0x02 + 0x03) & 0xFF);
            return f;
        }

        /// <summary>
        /// 反编译 Space Station 的 ControllerSDK 得到的真规则（HeartBeatCommandFactory.ParseAckData）：
        ///   Battery = (data[11] >> 4 == 1) ? 6 : (data[11] &amp; 0xF)
        /// 其中 data[] 从帧的第 2 个字节开始（data[2] 才是命令号），所以 data[11] = 整帧 [12]。
        /// 也就是说手柄只上报 0..6 的档位，没有百分比；高半字节 == 1 时按满格处理。
        /// </summary>
        internal static bool TryParse(byte[] f, out int level, out bool charging)
        {
            level = 0;
            charging = false;
            if (f == null || f.Length <= BatteryIndex) return false;
            if (f[1] != 0x5A || f[2] != 0xA5 || f[3] != 0x01) return false;

            byte b = f[BatteryIndex];
            int hi = b >> 4;
            int lo = b & 0x0F;

            charging = hi == 1;
            level = charging ? LevelMax : lo;
            if (level < LevelMin || level > LevelMax) return false;
            return true;
        }

        /// <summary>0..6 档按满格折算成百分比（0/17/33/50/67/83/100）。</summary>
        internal static int PercentFromLevel(int level)
        {
            if (level < LevelMin) level = LevelMin;
            if (level > LevelMax) level = LevelMax;
            return (int)Math.Round(100.0 * level / LevelMax);
        }

        private static byte[] WaitReply(HidSession s, int totalMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < totalMs)
            {
                int remaining = totalMs - (int)clock.ElapsedMilliseconds;
                byte[] p = s.Read(Math.Max(1, Math.Min(100, remaining)));
                if (p == null) continue;
                if (p.Length > 3 && p[1] == 0x5A && p[2] == 0xA5 && p[3] == 0x01) return p;
            }
            return null;
        }

        private static string ToHex(byte[] b)
        {
            if (b == null) return "";
            StringBuilder sb = new StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length && i < 24; i++)
            {
                if (i != 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
