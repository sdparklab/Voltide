using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// DAREU（达尔优）A950 无线鼠标 —— **有线（USB）模式**。
    ///
    /// 插上数据线后鼠标以 `260D:1084` 枚举（产品名 "2.4G Dual Mode Mouse"，
    /// 正是驱动 Cfg.ini 里写的 PID=0x1084），它的 HID 集合与 2.4G 接收器**完全同构**：
    ///
    ///   command : FF02 / Usage 2，17 字节 FEATURE（只写）
    ///   reply   : FF01 / Usage 0，17 字节中断 IN（只读）
    ///   （另有 FF04/feature 8 与鼠标/键盘/消费者三个标准集合，本 provider 不用）
    ///
    /// 真机实测（2026-11-25，插线充电中）：
    ///   tx 08 04 … 49  →  rx 09 04 00 00 00 02 32 01 00 … 13     50%，[7]=1 充电中
    ///   tx 08 03 … 4A  →  rx 09 03 00 00 00 01 01 00 … 47         ping 通
    /// 校验：341-(9+4+2+50+1)=275=0x113 → 0x13，与 2.4G 完全一致。
    ///
    /// 因此这里就是"同一套帧、换一个 PID"：帧格式、opcode、校验、字段位置全部沿用
    /// Dareu1074Provider；区别只有 VID/PID、卡片标签（有线）、以及它天然带充电位
    /// —— 有线是三条链路里唯一既能读电量、又能确认在充电的形态。
    ///
    /// 只发两条只读查询（ping + 查电量），不碰 DPI / 灯效 / 宏。
    /// </summary>
    public class DareuA950WiredProvider : IDeviceProvider
    {
        private const ushort DareuVid = 0x260D;
        private const ushort WiredPid = 0x1084;

        private const ushort CommandPage = 0xFF02;
        private const ushort CommandUsage = 0x0002;
        private const ushort ReplyPage = 0xFF01;
        private const ushort ReplyUsage = 0x0000;

        private const int FrameSize = 17;
        private const byte RequestReportId = 0x08;
        private const byte ReplyReportId = 0x09;
        private const byte CmdActive = 0x03;
        private const byte CmdBattery = 0x04;

        private const int PayloadIndex = 5;
        private const int PercentIndex = 6;
        private const int ChargeIndex = 7;
        private const int MilliVoltIndex = 8;

        private const byte ChargeStatusCharging = 0x01;
        private const byte ChargeStatusFull = 0x02;

        private const string LinkLabel = "有线";

        private static readonly object ReplyLock = new object();
        private static readonly Dictionary<string, string> ReplyPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Name { get { return "DAREU USB"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, string> replies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<HidDeviceInfo> commands = new List<HidDeviceInfo>();

            foreach (HidDeviceInfo hid in catalog)
            {
                if (hid == null) continue;
                if (hid.VendorId != DareuVid || hid.ProductId != WiredPid) continue;

                if (hid.UsagePage == ReplyPage && hid.Usage == ReplyUsage && hid.InputReportLength == FrameSize)
                {
                    replies[InstanceKey(hid.Path)] = hid.Path;
                }
                else if (hid.UsagePage == CommandPage && hid.Usage == CommandUsage && hid.FeatureReportLength == FrameSize)
                {
                    commands.Add(hid);
                }
            }

            foreach (HidDeviceInfo command in commands)
            {
                string replyPath;
                // 两个通道都在这只鼠标上才认领，避免半条链路被误认成在线。
                if (!replies.TryGetValue(InstanceKey(command.Path), out replyPath)) continue;

                DeviceInfo device = new DeviceInfo();
                device.Id = "dareu-a950-260D1084";
                device.Brand = "DAREU";
                device.Model = "A950";
                device.Name = "DAREU A950 (USB)";
                device.ShortModel = "A950 USB";
                device.Kind = DeviceKind.Mouse;
                device.VendorId = command.VendorId;
                device.ProductId = command.ProductId;
                device.HidPath = command.Path;
                device.ProviderName = Name;

                lock (ReplyLock) { ReplyPaths[device.Id] = replyPath; }
                found.Add(device);
            }

            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading reading = new DeviceReading();
            reading.UpdatedAt = DateTime.Now;
            reading.Online = false;
            reading.Level = BatteryLevel.Unknown;

            try
            {
                string replyPath = ResolveReplyPath(device);
                if (string.IsNullOrEmpty(replyPath))
                {
                    reading.Sleeping = true;
                    reading.Error = "未找到有线回传通道";
                    return reading;
                }

                // 先开回传句柄再发命令：只有已打开的句柄才会收到排队的输入报表。
                using (HidSession reply = new HidSession(replyPath, FrameSize, FrameSize, true))
                using (HidSession command = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    reply.Flush(20);

                    if (Send(command, reply, CmdActive, 1, 900) == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "有线链路无应答";
                        return reading;
                    }

                    byte[] answer = Send(command, reply, CmdBattery, 2, 1200);
                    if (answer == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "有线链路无应答";
                        return reading;
                    }

                    reading.Raw = ToHex(answer);

                    int percent = answer[PercentIndex];
                    if (percent > 100)
                    {
                        reading.Online = true;
                        reading.Error = "电量读数异常";
                        return reading;
                    }

                    byte charge = answer[ChargeIndex];
                    reading.Percent = percent;
                    reading.Level = BatteryMath.FromPercent(percent);
                    reading.Online = true;
                    reading.Charging = charge == ChargeStatusCharging;
                    reading.ChargingKnown = charge <= ChargeStatusFull;

                    int millivolts = (answer[MilliVoltIndex] << 8) | answer[MilliVoltIndex + 1];
                    reading.Detail = millivolts > 0 ? LinkLabel + " " + millivolts + " mV" : LinkLabel;
                    if (charge == ChargeStatusCharging) reading.Detail += " 充电中";
                    else if (charge == ChargeStatusFull) reading.Detail += " 已充满";
                }
            }
            catch (Exception ex)
            {
                reading.Error = ex.Message;
            }

            return reading;
        }

        private static byte[] Send(HidSession command, HidSession reply, byte opcode, byte payloadWords, int timeoutMs)
        {
            byte[] request = new byte[FrameSize];
            request[0] = RequestReportId;
            request[1] = opcode;
            request[FrameSize - 1] = Checksum(request);

            if (!HidNative.HidD_SetFeature(command.Handle, request, FrameSize)) return null;

            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                byte[] answer = reply.Read(120);
                if (answer == null) continue;
                if (answer.Length != FrameSize) continue;
                if (answer[0] != ReplyReportId || answer[1] != opcode) continue;
                if (answer[PayloadIndex] != payloadWords) continue;
                return answer;
            }
            return null;
        }

        private static string ResolveReplyPath(DeviceInfo device)
        {
            lock (ReplyLock)
            {
                string cached;
                if (ReplyPaths.TryGetValue(device.Id, out cached)) return cached;
            }

            foreach (HidDeviceInfo hid in HidNative.Enumerate())
            {
                if (hid.VendorId != DareuVid || hid.ProductId != WiredPid) continue;
                if (hid.UsagePage != ReplyPage || hid.Usage != ReplyUsage) continue;
                if (hid.InputReportLength != FrameSize) continue;
                return hid.Path;
            }
            return null;
        }

        /// <summary>同一只鼠标的集合共享 "...&amp;col0N#7&amp;1C1351AD&amp;0&amp;000N" 的实例前缀。</summary>
        private static string InstanceKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int first = path.IndexOf('#');
            if (first < 0) return path;
            int second = path.IndexOf('#', first + 1);
            if (second < 0) return path;
            string instance = path.Substring(first + 1, second - first - 1);
            int lastAmp = instance.LastIndexOf('&');
            if (lastAmp > 0) instance = instance.Substring(0, lastAmp);
            return instance.ToUpperInvariant();
        }

        private static byte Checksum(byte[] buffer)
        {
            int sum = 0;
            for (int i = 0; i < buffer.Length - 1; i++) sum += buffer[i];
            return (byte)((sum < 55 ? 85 - sum : 341 - sum) & 0xFF);
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
}
