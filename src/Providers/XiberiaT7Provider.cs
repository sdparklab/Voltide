using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// 西伯利亚 XIBERIA T7 / T7 GT 无线耳机（2.4G 接收器）。
    ///
    /// 协议来自对官方 XIBERIA HUB（CEF/Qt 原生程序）内置 ActHID.dll 的逆向，
    /// 对应其中的 ActHID2_GetBattVal（v2 会话接口）。
    ///
    /// 官方软件不按 VID/PID 识别设备，而是读取每个 HID 集合的报告描述符，
    /// 凡是描述符里出现 Report ID 0x55（主命令接口）与 0x41（数据接口）的
    /// 接收器即自动绑定。接收器固件为德盛（DeSheng）方案，VID 为 36F9。
    ///
    /// 电量查询走 64 字节厂商集合的中断 OUT/IN（write/read，非 Feature Report）：
    ///   请求 64B : 55 65 00 00 00 ...
    ///                    |  |  |
    ///                    |  |  +-- 设备索引（单耳机固定 0）
    ///                    |  +----- 命令 0x65 = 查询电量
    ///                    +-------- Report ID 0x55
    ///   回复 64B : 55 65 00 4B ...
    ///                          |
    ///                          +-- 第 4 字节（索引 3）= 电量百分比 0-100
    ///
    /// 读取超时 200ms；耳机休眠/关机时接收器不回帧，按未应答处理。
    /// </summary>
    public class XiberiaT7Provider : IDeviceProvider
    {
        private const ushort XiberiaVid = 0x36F9;
        private const ushort VendorPage = 0xFF00;
        private const int FrameLength = 64;

        private const byte MainReportId = 0x55;
        private const byte CmdBattery = 0x65;
        private const byte DeviceIndex = 0x00;

        private const int BatteryIndex = 3;

        // 对外型号 T7 / T7 GT，固件内部 OTA 代号为 CROCIRIS T7。
        private static readonly Regex T7Name = new Regex(
            @"T7|CROCIRIS",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string Name { get { return "XIBERIA T7"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            Dictionary<ushort, HidDeviceInfo> best = new Dictionary<ushort, HidDeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != XiberiaVid) continue;
                if (h.InputReportLength < FrameLength) continue;
                if (h.OutputReportLength < FrameLength) continue;
                if (!IsT7(h)) continue;

                // 同一 PID 可能存在多个集合，保留报告最宽的那个（主命令接口）。
                HidDeviceInfo current;
                if (best.TryGetValue(h.ProductId, out current))
                {
                    if (h.InputReportLength + h.OutputReportLength <=
                        current.InputReportLength + current.OutputReportLength) continue;
                }
                best[h.ProductId] = h;
            }

            foreach (KeyValuePair<ushort, HidDeviceInfo> kv in best)
            {
                HidDeviceInfo h = kv.Value;
                string model = ResolveModel(h.ProductName);

                DeviceInfo d = new DeviceInfo();
                d.Id = "xiberia-t7-" + h.ProductId.ToString("X4");
                d.Brand = "XIBERIA";
                d.Model = model;
                d.Name = "西伯利亚 " + model;
                d.ShortModel = "T7";
                d.Kind = DeviceKind.Headset;
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
            r.Detail = "2.4G · 西伯利亚 T7";

            try
            {
                using (HidSession session = new HidSession(device.HidPath, FrameLength, FrameLength))
                {
                    session.Flush(40);

                    byte[] reply = Query(session);
                    if (reply == null)
                    {
                        r.Sleeping = true;
                        r.Error = "耳机未应答（可能已关机或休眠）";
                        return r;
                    }

                    int percent = reply[BatteryIndex];
                    if (percent < 0 || percent > 100)
                    {
                        r.Online = true;
                        r.Error = "读数异常";
                        r.Raw = BitConverter.ToString(reply, 0, 8);
                        return r;
                    }

                    r.Raw = BitConverter.ToString(reply, 0, 8);
                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);
                    r.Online = true;
                    r.ChargingKnown = false;
                }
            }
            catch (Exception ex)
            {
                r.Sleeping = true;
                r.Error = "耳机未应答（可能已关机或休眠）";
                r.Detail = ex.Message;
            }

            return r;
        }

        private static byte[] Query(HidSession session)
        {
            byte[] request = new byte[FrameLength];
            request[0] = MainReportId;
            request[1] = CmdBattery;
            request[2] = DeviceIndex;
            request[3] = 0x00;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!session.Write(request, 200)) continue;

                // 官方实现读超时 200ms，最多循环约 10 次等待同接口回帧。
                for (int i = 0; i < 10; i++)
                {
                    byte[] reply = session.Read(200);
                    if (reply == null) break;
                    if (reply.Length < BatteryIndex + 1) continue;
                    if (reply[0] != MainReportId) continue;
                    if (reply[1] != CmdBattery) continue;

                    return reply;
                }
            }

            return null;
        }

        private static bool IsT7(HidDeviceInfo h)
        {
            // 官方按 Report ID 0x55/0x41 绑定，不依赖名称；这里优先识别 T7 名称，
            // 名称缺失时接受符合形态的厂商自定义集合（UsagePage 0xFF00）。
            string name = h.ProductName;
            if (!string.IsNullOrEmpty(name) && T7Name.IsMatch(name)) return true;
            return h.UsagePage == VendorPage;
        }

        private static string ResolveModel(string productName)
        {
            string name = productName;
            if (!string.IsNullOrEmpty(name))
            {
                if (name.ToUpperInvariant().Contains("GT")) return "T7 GT";
                if (T7Name.IsMatch(name)) return "T7";
            }
            return "T7";
        }
    }
}
