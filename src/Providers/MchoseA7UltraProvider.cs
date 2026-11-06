using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// MCHOSE (迈丛) A7 Ultra 无线鼠标。
    ///
    /// A7 Ultra 使用独立的 RealTek 接收器 VID 0x5253（区别于 A5 V3 的 0x3837）。
    /// 注意 HID 枚举随连接方式切换，同一时刻只会暴露一个 FF01 集合：
    ///   - 2.4G 无线：接收器 0x5253:0x1020
    ///   - USB 有线：鼠标本体 0x5253:0x0011
    ///   - 蓝牙：走 BLE GATT（Battery Service 0x180F/0x2A19），不经过 USB HID，
    ///     本适配器不处理，故此处仅覆盖 USB 有线与 2.4G 两种模式。
    ///
    /// 协议与 A5 V3 相同（0x11 查询 / 0x06 ost / XOR 0xFF 编码），仅 VID 与唤醒
    /// 序列不同。先发 bond 命令(0x03)唤醒厂商通道，再发 ost 命令(0x06)查询；
    /// A7 Ultra 不产生 A5 那样的中断-IN ACK，改为在 ost 后等待约 50ms 让固件
    /// 刷新 feature bank，再 GET_FEATURE 读取（立即读会拿到全 0）。
    ///
    ///   解码后（bytes[2..]，去除 reportId 与命令回显）：
    ///     payload[0..1] = VID（0x5253 小端）
    ///     payload[2..3] = 鼠标内部 PID（0x0011 = A7 Ultra）
    ///     payload[4..7] = 固件版本
    ///     payload[8]    = 连接位字段，mode = flags & 7：
    ///                        0 = USB 有线，1 = 2.4G，2 = 蓝牙
    ///     payload[9]    = 电量百分比（0–100）
    ///     payload[10]   = 充电状态（0=放电, 1=充电中）
    ///
    /// 实机验证（2026-11-07）：
    ///   无线 1020：flags=0x09 mode=1(2.4G) battery=95 charge=0
    ///   有线 0011：flags=0x00 mode=0(USB)  battery=95 charge=1
    /// </summary>
    public class MchoseA7UltraProvider : IDeviceProvider
    {
        private const ushort MchoseVid = 0x5253;   // A7 Ultra 专用 VID（接收器与本体同 VID）
        private const ushort FeatureLen = 65;
        private const ushort PageVendor = 0xFF01;
        private const byte RidQuery = 0x11;
        private const byte CmdBond = 0x03;
        private const byte CmdOst = 0x06;

        public string Name { get { return "MCHOSE A7 Ultra"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            HashSet<string> seen = new HashSet<string>();

            List<HidDeviceInfo> list = catalog;
            if (list == null) list = HidNative.Enumerate();

            foreach (HidDeviceInfo h in list)
            {
                if (h.VendorId != MchoseVid) continue;
                if (h.UsagePage != PageVendor) continue;
                if (h.FeatureReportLength != FeatureLen) continue;

                string key = h.Path;
                if (!seen.Add(key)) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "mchosea7ultra-" + h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                d.Brand = "MCHOSE";
                d.Model = ModelFromName(h.ProductName, h.ProductId);
                d.Name = "MCHOSE " + d.Model;
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

            try
            {
                using (HidSession s = new HidSession(d.HidPath, FeatureLen, FeatureLen))
                {
                    s.Flush(30);

                    // 唤醒厂商通道：A7 Ultra 必须先发 bond 才会响应 ost 查询。
                    SendFeature(s, CmdBond);
                    System.Threading.Thread.Sleep(120);
                    s.Flush(30);

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(200);

                        SendFeature(s, CmdOst);
                        // A7 Ultra 在 ost 命令后需要一段时间让固件刷新 feature bank，
                        // 立即读会拿到全 0。实测 50ms 起稳定返回真实电量。
                        System.Threading.Thread.Sleep(80);

                        byte[] payload;
                        if (!ReadOst(s, out payload)) continue;

                        int vid = payload[0] | (payload[1] << 8);
                        int flags = payload[8];
                        int level = payload[9];
                        int charge = payload[10];

                        r.Raw = Hex(payload);

                        if (vid != MchoseVid || level <= 0 || level > 100)
                        {
                            r.Sleeping = true;
                            r.Error = "设备休眠";
                            continue;
                        }

                        r.Percent = level;
                        r.Level = BatteryMath.FromPercent(level);
                        r.Charging = charge != 0;
                        r.Detail = ModeLabel(flags);
                        r.Online = true;
                        return r;
                    }

                    // 三轮都没读到有效数据，视为休眠。
                    if (string.IsNullOrEmpty(r.Error))
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        /// <summary>
        /// 连接方式取自 payload[8] 的低 3 位（mode = flags &amp; 7）：
        /// 0 = USB 有线，1 = 2.4G。蓝牙(mode=2)不会经过本适配器——蓝牙连接时
        /// 设备走 BLE GATT，USB HID 集合挂起，Discover 根本枚举不到。
        /// </summary>
        private static string ModeLabel(int flags)
        {
            switch (flags & 7)
            {
                case 0: return "USB 有线";
                case 1: return "2.4G";
                case 2: return "蓝牙";
                default: return "模式 " + (flags & 7);
            }
        }

        /// <summary>SET_FEATURE: reportId, command XOR-inverted, 0xFF filler.</summary>
        private static void SendFeature(HidSession s, byte cmd)
        {
            byte[] buf = new byte[FeatureLen];
            for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF;
            buf[0] = RidQuery;
            buf[1] = (byte)(cmd ^ 0xFF);
            HidNative.HidD_SetFeature(s.Handle, buf, buf.Length);
        }

        /// <summary>
        /// GET_FEATURE after the ost query. The firmware alternates two banks
        /// (XOR-encoded with buf[1] == ~cmd, and plain with buf[1] == cmd).
        /// Accept either; decode accordingly.
        /// </summary>
        private static bool ReadOst(HidSession s, out byte[] payload)
        {
            payload = null;
            for (int i = 0; i < 4; i++)
            {
                byte[] gb = new byte[FeatureLen];
                gb[0] = RidQuery;
                if (!HidNative.HidD_GetFeature(s.Handle, gb, gb.Length)) return false;

                bool xorBank = ((~gb[1]) & 0xFF) == CmdOst;
                bool plainBank = gb[1] == CmdOst;
                if (!xorBank && !plainBank) continue;

                byte[] p = new byte[FeatureLen - 2];
                for (int j = 0; j < p.Length; j++)
                    p[j] = xorBank ? (byte)(gb[2 + j] ^ 0xFF) : gb[2 + j];
                payload = p;
                return true;
            }
            return false;
        }

        private static string ModelFromName(string productName, ushort pid)
        {
            if (!string.IsNullOrEmpty(productName))
            {
                string n = productName.Trim();
                if (n.StartsWith("MCHOSE", StringComparison.OrdinalIgnoreCase)) n = n.Substring(6).Trim();
                if (n.Length > 0) return n;
            }
            return "A7 Ultra";
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 16 && i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
    }
}
