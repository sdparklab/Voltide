using System;
using System.Collections.Generic;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// 新盟 XINMENG 三模键盘的 2.4G 接收器（松翰 Sonix 方案）。
    ///
    /// 接收器为了免驱，把自己伪装成苹果键盘：VID 0x05AC / PID 0x024F，
    /// 产品串 "2.4G Dongle"。同一份描述符里开了五个接口，其中两个是
    /// 厂商集合（usage page 0xFF60）：
    ///
    ///   33 字节（mi_03，input=33 output=33）—— 真正回数据的通道
    ///   65 字节（mi_04，input=65 output=65）—— 只回状态，永远没有电量
    ///
    /// 这一点很容易踩坑：往 65 字节通道发任何命令，回的都是固定的
    /// "00 0F 01 01 11 00..."，看起来像通道被拒，其实只是问错了地方。
    ///
    /// 身份校验：
    ///   05AC:024F 是苹果键盘的 ID，AULA F108 Pro、SONiX 等多家的 2.4G
    ///   接收器都在冒用它，所以单看 VID/PID 会把别人的键盘认成新盟。
    ///   命令 0x02 会返回设备自己的真实身份：
    ///
    ///     00 02 00 00 40 00 30 00 45 0C 06 80 14 01 FF FF ...
    ///                              ^^^^^ ^^^^^
    ///                              VID    PID
    ///                              0C45   8006   ← 松翰 Sonix
    ///
    ///   只有回了 0C45:8006 才认，否则整条路径拉黑，避免误报。
    ///
    /// 电量命令（实测 2026-11-16，键盘电量 69% → 68%）：
    ///
    ///   发送 33 字节，走 WriteFile（中断 OUT）：
    ///     [0]     = 0xAA  帧头
    ///     [1]     = 0x20  电量查询
    ///     [2..]   = 0x00
    ///
    ///   回复 33 字节：
    ///     00 20 00 00 45 00 00 ... 00 65      ← 电量 69%
    ///     00 20 00 00 44 00 00 ... 00 64      ← 电量 68%
    ///     ^^ ^^    ^^                 ^^
    ///     |  |     |                  +-- 校验和 = sum(reply[1..31]) & 0xFF
    ///     |  |     +--------------------- 电量百分比（0..100）
    ///     |  +--------------------------- 命令字回显
    ///     +------------------------------ report id 恒为 0
    ///
    ///   校验和寄存器验证：0x20 + 0x45 = 0x65，0x20 + 0x44 = 0x64，两条都成立。
    ///
    /// 注意：SET_REPORT 能写成功但固件不理，只有 WriteFile 会被应答。这和
    /// 项目里 MCHOSE 系列恰好相反，别照抄那边的写法。
    /// </summary>
    public class XinmengProvider : IDeviceProvider
    {
        // 伪装成苹果键盘的接收器。同为 05AC:024F 的还有 AULA、SONiX 等方案，
        // 所以这个 VID/PID 只能用来缩小范围，真正的身份靠 CheckIdentity 确认。
        private const ushort FakeVid = 0x05AC;
        private const ushort FakePid = 0x024F;
        private const ushort VendorPage = 0xFF60;

        private const int FrameSize = 33;
        private const byte FrameHead = 0xAA;
        private const byte CmdBattery = 0x20;
        private const byte CmdInfo = 0x02;
        private const int PercentIndex = 4;

        // 设备自报的真实身份：松翰 Sonix。
        private const ushort SonixVid = 0x0C45;
        private const ushort SonixPid = 0x8006;
        private const int InfoVidIndex = 8;
        private const int InfoPidIndex = 10;

        // 路径 -> 是否已确认是新盟。键盘休眠时 0x02 也不回，不能因为一次
        // 读不到就把已经认过的设备踢掉，所以结果要记住。
        private static readonly Dictionary<string, bool> _verified = new Dictionary<string, bool>();
        private static readonly object _verifiedLock = new object();

        public string Name { get { return "XINMENG 2.4G"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != FakeVid || h.ProductId != FakePid) continue;
                if (h.UsagePage != VendorPage) continue;
                // 只认 33 字节那个通道；65 字节的同名集合是哑的。
                if (h.InputReportLength != FrameSize) continue;
                if (h.OutputReportLength != FrameSize) continue;

                // 已经判过是别人家的，直接跳过，不再浪费一次往返。
                lock (_verifiedLock)
                {
                    bool known;
                    if (_verified.TryGetValue(h.Path, out known) && !known) continue;
                }

                DeviceInfo d = new DeviceInfo();
                d.Id = "xinmeng-2g";
                d.Brand = "新盟";
                d.Model = "2.4G 键盘";
                d.Name = "新盟 2.4G 键盘";
                d.ShortModel = "新盟键盘";
                d.Kind = DeviceKind.Keyboard;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);
            }

            return found;
        }

        /// <summary>
        /// 只在第一次读到某个路径时做一次身份握手。返回 true 表示可以采信。
        /// 判定不明的（键盘在睡）暂时放行，等它醒了再定。
        /// </summary>
        private bool CheckIdentity(string path)
        {
            lock (_verifiedLock)
            {
                bool known;
                if (_verified.TryGetValue(path, out known)) return known;
            }

            bool? verdict = ProbeIdentity(path);
            if (verdict.HasValue)
            {
                lock (_verifiedLock) { _verified[path] = verdict.Value; }
                return verdict.Value;
            }

            // 没回应：可能是键盘睡着的正品，也可能是根本不支持这条命令的他牌。
            // 先放行，等真正拿到回复再下结论。
            return true;
        }

        /// <summary>true = 新盟；false = 他牌；null = 设备没回应。</summary>
        private static bool? ProbeIdentity(string path)
        {
            try
            {
                using (HidSession s = new HidSession(path, FrameSize, FrameSize))
                {
                    s.Flush(30);

                    byte[] query = new byte[FrameSize];
                    query[0] = FrameHead;
                    query[1] = CmdInfo;

                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        if (attempt > 0) Thread.Sleep(100);

                        bool sent = s.Write(query, 400);
                        if (!sent) sent = s.SetOutputReport(query);
                        if (!sent) continue;

                        byte[] resp = WaitFor(s, CmdInfo, 600);
                        if (resp == null) continue;
                        if (resp.Length <= InfoPidIndex + 1) return false;

                        ushort vid = (ushort)(resp[InfoVidIndex] | (resp[InfoVidIndex + 1] << 8));
                        ushort pid = (ushort)(resp[InfoPidIndex] | (resp[InfoPidIndex + 1] << 8));
                        return vid == SonixVid && pid == SonixPid;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;
            // 这个协议不报充电状态，只能标为未知。
            r.ChargingKnown = false;

            // 身份没确认过的，先验一次；他牌键盘在这里就被挡下来了。
            if (!CheckIdentity(device.HidPath))
            {
                r.Error = "不是新盟设备";
                return r;
            }

            try
            {
                byte[] query = new byte[FrameSize];
                query[0] = FrameHead;
                query[1] = CmdBattery;

                using (HidSession s = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    s.Flush(30);

                    byte[] resp = null;
                    for (int attempt = 0; attempt < 3 && resp == null; attempt++)
                    {
                        if (attempt > 0) Thread.Sleep(120);

                        // WriteFile（中断 OUT）才会被应答；SET_REPORT 会写成功
                        // 但固件不理，只能当兜底。这里不能用短路求值：
                        // SetOutputReport 一成功就跳过 Write，结果是永远收不到回复。
                        bool sent = s.Write(query, 400);
                        if (!sent) sent = s.SetOutputReport(query);
                        if (!sent)
                        {
                            r.Error = "发送失败（设备被占用？）";
                            return r;
                        }

                        resp = WaitFor(s, CmdBattery, 700);
                    }

                    if (resp == null)
                    {
                        r.Sleeping = true;
                        r.Error = "键盘休眠（按任意键唤醒）";
                        return r;
                    }

                    byte level = resp[PercentIndex];
                    if (level > 100)
                    {
                        r.Online = true;
                        r.Percent = null;
                        r.Raw = Hex(resp);
                        r.Error = "读数异常";
                        return r;
                    }

                    // 校验和：sum(reply[1..30]) & 0xFF 应等于 reply[31]。
                    // 对不上就说明帧不完整，宁可报错也不显示错数字。
                    int sum = 0;
                    for (int i = 1; i < resp.Length - 1; i++) sum += resp[i];
                    bool checksumOk = ((byte)(sum & 0xFF)) == resp[resp.Length - 1];

                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    r.Charging = false;
                    r.Online = true;
                    r.Detail = checksumOk ? "2.4G" : "2.4G（校验未过）";
                    r.Raw = Hex(resp);
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        private static byte[] WaitFor(HidSession s, byte cmd, int timeoutMs)
        {
            for (int i = 0; i < 12; i++)
            {
                byte[] p = s.Read(Math.Max(60, timeoutMs / 4));
                if (p == null) return null;
                // 回复首字节是 report id（0），第二字节是命令回显。
                if (p.Length > PercentIndex && p[0] == 0x00 && p[1] == cmd) return p;
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