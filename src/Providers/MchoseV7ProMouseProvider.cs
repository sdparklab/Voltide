using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// 查询 MCHOSE V7 Pro 接收器的电量与充电状态，仅发送只读设备信息命令 0x0900。
    /// 协议与 K7 V2 同族（64 字节 report 0x4D，帧结构见
    /// https://github.com/Rapou7/offline-mchose/blob/master/REVERSE_ENGINEERING_MCHOSE.md §3.4），
    /// 但接收器为 3837:101D（MI_02、用途页 FF01），鼠标侧 PID 为 0x403E，
    /// 且固件字段语义与 K7 文档有两处差异，均经实机验证：
    /// 1. 连接状态（回复第 18 字节）为 0x01 时代表 2.4G 数传链路在线；
    ///    鼠标切换为有线模式后接收器不再应答 0x0900，查询超时即视为离线。
    /// 2. 充电状态（回复第 19 字节）使用 0/1/2：0=放电、1=充电中、2=已充满。
    /// 实测回复报文（2026-11，2.4G 连接、满电）：
    /// 4D 01 01 10 00 09 00 00 37 38 3E 40 03 00 00 50 00 00 01 02 64 00 00 00 5D 后续为 00。
    /// </summary>
    public class MchoseV7ProMouseProvider : IDeviceProvider
    {
        private const ushort ReceiverVid = 0x3837;
        private const ushort ReceiverPid = 0x101D;
        private const ushort UsagePage = 0xFF01;
        private const ushort Usage = 0x0001;
        private const int ReportLength = 64;
        private const int ReplyTimeoutMs = 800;

        public string Name { get { return "MCHOSE V7 Pro Mouse"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, bool> seenPaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo hid in catalog)
            {
                if (!MatchesReceiver(hid)) continue;
                if (seenPaths.ContainsKey(hid.Path)) continue;
                seenPaths.Add(hid.Path, true);

                DeviceInfo device = new DeviceInfo();
                device.Id = "mchose-mouse-" + hid.Path;
                device.Brand = "MCHOSE";
                device.Model = "V7 Pro";
                device.Name = "MCHOSE V7 Pro";
                device.ShortModel = "V7 Pro";
                device.Kind = DeviceKind.Mouse;
                device.VendorId = hid.VendorId;
                device.ProductId = hid.ProductId;
                device.HidPath = hid.Path;
                device.ProviderName = Name;
                found.Add(device);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Level = BatteryLevel.Unknown;

            if (device == null || device.VendorId != ReceiverVid || device.ProductId != ReceiverPid ||
                string.IsNullOrEmpty(device.HidPath) || !IsMi02Path(device.HidPath))
            {
                reading.Error = "不支持的 MCHOSE 鼠标接收器";
                return reading;
            }

            try
            {
                using (HidSession session = new HidSession(device.HidPath, ReportLength, ReportLength))
                {
                    byte[] request = MchoseV7ProProtocol.CreateBatteryRequest();
                    if (!session.Write(request, 1000))
                    {
                        reading.Error = "发送电量查询失败";
                        return reading;
                    }

                    Stopwatch timer = Stopwatch.StartNew();
                    while (timer.ElapsedMilliseconds < ReplyTimeoutMs)
                    {
                        int remaining = ReplyTimeoutMs - (int)timer.ElapsedMilliseconds;
                        byte[] report = session.Read(Math.Min(100, Math.Max(1, remaining)));
                        if (report == null) continue;

                        int percent;
                        bool charging;
                        bool chargingKnown;
                        MchoseV7ProReplyKind result = MchoseV7ProProtocol.ParseResponse(report, out percent, out charging, out chargingKnown);
                        if (result == MchoseV7ProReplyKind.Unrelated) continue;

                        reading.Raw = ToHex(report);
                        if (result == MchoseV7ProReplyKind.Invalid)
                        {
                            reading.Error = "MCHOSE 电量应答格式或校验无效";
                            return reading;
                        }
                        if (result == MchoseV7ProReplyKind.Unavailable)
                        {
                            reading.Sleeping = true;
                            reading.Error = "鼠标休眠或未连接";
                            return reading;
                        }

                        reading.Percent = percent;
                        reading.Level = BatteryMath.FromPercent(percent);
                        reading.Charging = charging;
                        reading.ChargingKnown = chargingKnown;
                        reading.Online = true;
                        reading.Detail = "2.4G";
                        return reading;
                    }

                    reading.Sleeping = true;
                    reading.Error = "设备休眠";
                }
            }
            catch (Exception ex)
            {
                reading.Error = ex.Message;
            }

            return reading;
        }

        private static bool MatchesReceiver(HidDeviceInfo hid)
        {
            return hid != null &&
                hid.VendorId == ReceiverVid &&
                hid.ProductId == ReceiverPid &&
                hid.UsagePage == UsagePage &&
                hid.Usage == Usage &&
                hid.InputReportLength == ReportLength &&
                hid.OutputReportLength == ReportLength &&
                !string.IsNullOrEmpty(hid.Path) &&
                IsMi02Path(hid.Path);
        }

        private static bool IsMi02Path(string path)
        {
            return path != null && path.IndexOf("&mi_02&", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ToHex(byte[] report)
        {
            StringBuilder text = new StringBuilder(report.Length * 3);
            for (int i = 0; i < report.Length; i++)
            {
                if (i != 0) text.Append(' ');
                text.Append(report[i].ToString("X2"));
            }
            return text.ToString();
        }
    }

    internal enum MchoseV7ProReplyKind
    {
        Unrelated,
        Invalid,
        Unavailable,
        Battery
    }

    /// <summary>
    /// 编解码 V7 Pro 的设备信息报文，拒绝不完整、校验错误或其他设备的回复。
    /// </summary>
    internal static class MchoseV7ProProtocol
    {
        private const byte ReportId = 0x4D;
        private const byte ProtocolVersion = 0x01;
        private const byte Flags = 0x01;
        private const byte PayloadLength = 0x10;
        private const ushort BatteryQuery = 0x0900;
        private const ushort MouseVid = 0x3837;
        // 实机应答中的鼠标 PID；接收器 PID 是 0x101D。
        private const ushort MousePid = 0x403E;
        private const int ChecksumOffset = 24;

        internal static byte[] CreateBatteryRequest()
        {
            byte[] request = new byte[64];
            request[0] = ReportId;
            request[1] = ProtocolVersion;
            request[2] = Flags;
            request[4] = (byte)(BatteryQuery & 0xFF);
            request[5] = (byte)(BatteryQuery >> 8);
            request[8] = (byte)(request[2] ^ request[3] ^ request[4] ^ request[5] ^ request[6] ^ request[7]);
            return request;
        }

        internal static MchoseV7ProReplyKind ParseResponse(byte[] report, out int percent, out bool charging, out bool chargingKnown)
        {
            percent = 0;
            charging = false;
            chargingKnown = false;
            if (report == null || report.Length < 6 || report[4] != 0x00 || report[5] != 0x09)
                return MchoseV7ProReplyKind.Unrelated;

            if (report.Length != 64 ||
                report[0] != ReportId ||
                report[1] != ProtocolVersion ||
                report[2] != Flags ||
                report[3] != PayloadLength ||
                report[6] != 0x00)
                return MchoseV7ProReplyKind.Invalid;

            int checksum = 0;
            for (int i = 2; i <= 7 + PayloadLength; i++) checksum ^= report[i];
            if (report[ChecksumOffset] != (byte)checksum) return MchoseV7ProReplyKind.Invalid;

            for (int i = ChecksumOffset + 1; i < report.Length; i++)
            {
                if (report[i] != 0) return MchoseV7ProReplyKind.Invalid;
            }

            ushort mouseVid = (ushort)(report[8] | (report[9] << 8));
            ushort mousePid = (ushort)(report[10] | (report[11] << 8));
            if (mouseVid != MouseVid || mousePid != MousePid) return MchoseV7ProReplyKind.Invalid;

            // V7 Pro 固件：第 18 字节 0x01 表示 2.4G 数传链路在线（与 K7 文档的 0x02 不同），
            // 有线模式下接收器不应答，因此 0x00 视为离线。
            byte connection = report[18];
            byte charge = report[19];
            byte level = report[20];
            if (charge > 2 || level > 100) return MchoseV7ProReplyKind.Invalid;
            if (connection == 0) return MchoseV7ProReplyKind.Unavailable;
            if (connection != 1) return MchoseV7ProReplyKind.Invalid;

            percent = level;
            charging = charge == 1;
            chargingKnown = true;
            return MchoseV7ProReplyKind.Battery;
        }
    }
}
