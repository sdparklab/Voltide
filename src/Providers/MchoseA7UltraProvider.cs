using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// MCHOSE (迈丛) A7 Ultra 无线鼠标。接收器 VID 0x5253 / PID 0x1020，
    /// 与 A5 V3 的 0x3837 不同品牌集合：A7 Ultra 使用独立的 0x5253 无线电
    /// 接收器（RealTek 方案），在厂商用途页 0xFF01 上以 65 字节 Feature Report
    /// 承载电池协议。
    ///
    /// 协议与 MCHOSE A5 V3 相同（0x11 查询 / 0x06 ost / XOR 0xFF 编码），
    /// 仅 VID 与唤醒序列细节不同。实机（A7 Ultra，PID 0x1020，内部鼠标 PID
    /// 0x0011）验证：必须先向 0xFF01 集合发送 bond 命令(0x03)唤醒厂商通道，
    /// 再发送 ost 命令(0x06)查询；随后 GET_FEATURE 返回 65 字节，payload =
    /// bytes[2..]，XOR 0xFF 解码。固件在两个 bank 间交替（XOR 与明文），
    /// 两者都返回相同的 payload。
    ///
    ///   解码后（bytes[2..]，去除 reportId 与命令回显）：
    ///     payload[0..1] = 接收器 VID（0x5253 小端）
    ///     payload[2..3] = 鼠标内部 PID（0x0011 = A7 Ultra）
    ///     payload[4..7] = 固件版本
    ///     payload[8]    = 连接位字段
    ///     payload[9]    = 电量百分比（0–100）
    ///     payload[10]   = 充电状态（0=电池供电, 1=充电中）
    ///
    /// 实机验证（2026-11-07）：vid=5253 pid=0011 fw=0x00050407 battery=95 charge=0。
    /// 与 A5 V3 不同，A7 Ultra 的 ACK（中断-IN "e5 06"）不出现，但 GET_FEATURE
    /// 在 bond 唤醒后直接返回新鲜数据，因此无需等待 ACK。
    /// </summary>
    public class MchoseA7UltraProvider : IDeviceProvider
    {
        private const ushort MchoseRadioVid = 0x5253;   // A7 Ultra 专用接收器
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
                if (h.VendorId != MchoseRadioVid) continue;
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
                        int level = payload[9];
                        int charge = payload[10];

                        r.Raw = Hex(payload);
                        r.Detail = "2.4G";

                        if (vid != MchoseRadioVid || level <= 0 || level > 100)
                        {
                            r.Sleeping = true;
                            r.Error = "设备休眠";
                            continue;
                        }

                        r.Percent = level;
                        r.Level = BatteryMath.FromPercent(level);
                        r.Charging = charge != 0;
                        if (r.Charging) r.Detail = "2.4G 充电中";
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
