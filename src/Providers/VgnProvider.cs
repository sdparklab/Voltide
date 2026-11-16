using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// VGN V98 Pro 键盘电量适配。
    ///
    /// 匹配：2.4G 接收器 320F:5088；有线接口 320F:5055。
    /// 接口：UsagePage 0xFF1C；Usage 0x92；报告 ID 0x04；收发长度均为 64 字节。
    /// 协议来源：官方网页驱动 drive.vgnlab.com.cn 的 WebHID 实现。
    /// 设备类型为 weisheng，对应所分析版本中的 Wc 类。
    ///
    /// 电量请求：04 00 00 1A 06 00 00 00 ...。
    /// 回复示例：04 00 00 1A 06 00 00 00 5D 00 ...。
    /// reply[8] 为电量百分比，示例 0x5D 表示 93%。
    /// reply[9] 为充电标志，已测值 0 表示未充电、1 表示接入电源。
    /// 官方驱动读取电量字段；充电标志的含义来自有线与无线状态的实测。
    ///
    /// 插线后接收器可能仍被枚举但不再应答，由有线路径提供读数。
    /// 当前按规范化型号合并有线与接收器路径，优先采用有线路径的有效读数。
    /// 该合并方式尚不区分多台同型号键盘。
    ///
    /// 型号取自 HID 产品字符串，移除 RDR、Telink、VGN 前缀和接收器后缀，
    /// 并将 V98PRO 与 V98 PRO 统一为 V98 PRO。
    /// 已在 V98 Pro 验证；共用 VID/PID 的其他型号尚待分别验证。
    /// </summary>
    public class VgnProvider : IDeviceProvider
    {
        private const ushort VgnVid = 0x320F;
        private const ushort DonglePid = 0x5088;
        private const ushort WiredPid = 0x5055;
        private const ushort VendorPage = 0xFF1C;
        private const ushort VendorUsage = 0x92;

        private const int ReportLength = 64;
        private const byte ReportId = 0x04;
        private const byte CmdGetBattery = 0x1A;
        private const int BatteryIndex = 8;
        private const int ChargingIndex = 9;

        private static readonly Regex Prefix = new Regex(@"^(RDR|Telink)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DongleSuffix = new Regex(@"\s*2\.4G\s*Dongle\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex VgnPrefix = new Regex(@"^VGN\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DigitLetter = new Regex(@"(\d)([A-Z])", RegexOptions.CultureInvariant);

        // 按卡片 ID 保存规范化型号对应的路径，读取时逐条查询。
        private Dictionary<string, List<HidDeviceInfo>> _paths = new Dictionary<string, List<HidDeviceInfo>>();

        public string Name { get { return "VGN"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            Dictionary<string, List<HidDeviceInfo>> groups = new Dictionary<string, List<HidDeviceInfo>>();
            List<string> order = new List<string>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != VgnVid) continue;
                if (h.ProductId != DonglePid && h.ProductId != WiredPid) continue;
                if (h.UsagePage != VendorPage || h.Usage != VendorUsage) continue;
                if (h.InputReportLength < ReportLength || h.OutputReportLength < ReportLength) continue;

                string model = ModelFrom(h.ProductName);
                List<HidDeviceInfo> list;
                if (!groups.TryGetValue(model, out list))
                {
                    list = new List<HidDeviceInfo>();
                    groups[model] = list;
                    order.Add(model);
                }
                // 有线路径优先；插线时接收器路径可能停止应答。
                if (h.ProductId == WiredPid) list.Insert(0, h);
                else list.Add(h);
            }

            Dictionary<string, List<HidDeviceInfo>> paths = new Dictionary<string, List<HidDeviceInfo>>();
            foreach (string model in order)
            {
                HidDeviceInfo h = groups[model][0];

                DeviceInfo d = new DeviceInfo();
                d.Id = "vgn-" + model;
                d.Brand = "VGN";
                d.Model = model;
                d.ShortModel = model;
                d.Name = "VGN " + model;
                d.Kind = DeviceKind.Keyboard;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);

                paths[d.Id] = groups[model];
            }

            _paths = paths;
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;

            List<HidDeviceInfo> paths;
            if (!_paths.TryGetValue(device.Id, out paths) || paths.Count == 0)
            {
                r.Sleeping = true;
                r.Error = "键盘未应答（可能已关机或休眠）";
                return r;
            }

            bool wired = false, dongle = false;
            List<string> raws = new List<string>();

            foreach (HidDeviceInfo h in paths)
            {
                byte[] reply = ReadPath(h.Path);
                if (reply == null) continue;

                raws.Add(BitConverter.ToString(reply, 0, 12));
                if (h.ProductId == WiredPid) wired = true;
                else dongle = true;

                if (r.Percent.HasValue) continue;           // 使用首个有效电量读数。

                int percent = reply[BatteryIndex];
                if (percent > 100) continue;

                r.Online = true;
                r.Percent = percent;
                r.Level = BatteryMath.FromPercent(percent);
                r.Charging = reply[ChargingIndex] != 0;
            }

            r.Raw = string.Join(" | ", raws.ToArray());
            r.Detail = LinkText(wired, dongle);

            if (!r.Percent.HasValue)
            {
                r.Online = raws.Count > 0;
                r.Sleeping = raws.Count == 0;
                r.Error = raws.Count > 0 ? "读数异常" : "键盘未应答（可能已关机或休眠）";
            }

            return r;
        }

        private static string LinkText(bool wired, bool dongle)
        {
            if (wired && dongle) return "有线 + 2.4G";
            if (wired) return "有线";
            return "2.4G";
        }

        private static byte[] ReadPath(string path)
        {
            try
            {
                using (HidSession session = new HidSession(path, ReportLength, ReportLength))
                {
                    session.Flush(20);
                    return Query(session);
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Query(HidSession session)
        {
            byte[] request = new byte[ReportLength];
            request[0] = ReportId;
            request[3] = CmdGetBattery;
            request[4] = 0x06;

            // 限制重试次数和单次等待时间，避免无应答路径长时间阻塞刷新。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!session.Write(request, 200)) continue;

                for (int i = 0; i < 8; i++)
                {
                    byte[] reply = session.Read(200);
                    if (reply == null) break;
                    if (reply.Length <= ChargingIndex) continue;
                    if (reply[0] != ReportId) continue;
                    if (reply[1] != 0 || reply[2] != 0 || reply[3] != CmdGetBattery) continue;
                    return reply;
                }
            }

            return null;
        }

        private static string ModelFrom(string productName)
        {
            string name = productName == null ? "" : productName.Trim();
            name = Prefix.Replace(name, "");
            name = DongleSuffix.Replace(name, "");
            name = VgnPrefix.Replace(name, "").Trim().ToUpperInvariant();
            name = DigitLetter.Replace(name, "$1 $2");      // 将 V98PRO 规范化为 V98 PRO。
            return name.Length == 0 ? "V98 PRO" : name;
        }
    }
}
