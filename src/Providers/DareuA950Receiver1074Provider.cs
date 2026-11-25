using System;
using System.Collections.Generic;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// DAREU（达尔优）A950 三模无线游戏鼠标（2.4G / 蓝牙 / 有线），本 provider 只覆盖
    /// 2.4G 接收器模式：接收器 260D:1074（Compx 方案）。蓝牙与有线不在覆盖范围内
    /// （官网驱动 Cfg.ini 里另有 PID=0x1084，本机未枚举到、未抓包验证）。
    ///
    /// 与既有的 260D:1175（A950 AIR V2）不同，这只接收器把链路拆成两个 HID 集合：
    ///
    ///   command : FF02 / Usage 2，17 字节 FEATURE 上报（只写）
    ///             08 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 4A   active，确认鼠标醒着
    ///             08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49   battery，查电量
    ///   reply   : FF01 / Usage 0，17 字节中断 IN（只读）
    ///             09 04 00 00 00 02 PP CS VV VV 00 00 00 00 00 00 CH
    ///                            ^^ ^^ ^^^^^
    ///                            |  |  +-- 电池电压，大端毫伏（该型号恒为 0）
    ///                            |  +----- 充电状态：0 放电、1 充电中、2 已充满
    ///                            +-------- 数据字数：active 回 1、battery 回 2
    ///
    /// 与 260D:1175 的三点差异，都是这台硬件实测出来的：
    ///   - 命令走 HID SET_REPORT（HidD_SetFeature），WriteFile 在 FF02 上无任何反应；
    ///   - 回复要从 FF01 这个独立集合读，命令集合没有输入端点；
    ///   - 回复首字节是 0x09，不是请求里的 0x08，因此不会出现请求回显。
    ///
    /// 校验：sum(bytes[0..15]) &lt; 55 ? 85 - sum : 341 - sum。
    /// 设备写入只发上述两条只读查询，不触碰 DPI / 灯效 / 宏配置。
    /// </summary>
    public class DareuA950Receiver1074Provider : IDeviceProvider
    {
        private const ushort DareuVid = 0x260D;
        private const ushort ReceiverPid = 0x1074;

        // 命令通道（FEATURE，只写）与回传通道（中断 IN，只读）是两个不同的集合，
        // 必须分别打开：命令集合没有输入端点，回传集合没有输出端点。
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

        public string Name { get { return "DAREU 2.4G"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, string> replies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<HidDeviceInfo> commands = new List<HidDeviceInfo>();

            foreach (HidDeviceInfo hid in catalog)
            {
                if (hid == null) continue;
                if (hid.VendorId != DareuVid || hid.ProductId != ReceiverPid) continue;

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
                // 两个通道必须同属一只接收器才算认领，避免半条链路被报成在线。
                if (!replies.TryGetValue(InstanceKey(command.Path), out replyPath)) continue;

                DeviceInfo device = new DeviceInfo();
                device.Id = "dareu-a950-260D1074";
                device.Brand = "DAREU";
                device.Model = "A950";
                device.Name = "DAREU A950";
                device.ShortModel = "A950";
                device.Kind = DeviceKind.Mouse;
                device.VendorId = command.VendorId;
                device.ProductId = command.ProductId;
                device.HidPath = command.Path;
                device.ProviderName = Name;
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
                string replyPath = FindReplyPath(device);
                if (string.IsNullOrEmpty(replyPath))
                {
                    reading.Sleeping = true;
                    reading.Error = "未找到接收器回传通道";
                    return reading;
                }

                // 回传句柄先开：Windows 只为已经打开的句柄排队输入上报，
                // 顺序反了会漏掉这一次回复。
                using (HidSession reply = new HidSession(replyPath, FrameSize, FrameSize, true))
                using (HidSession command = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    reply.Flush(20);

                    // 官方驱动先 active 一次确认鼠标醒着；休眠时接收器不回任何帧。
                    if (Send(command, reply, CmdActive, 1, 900) == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "鼠标休眠或未连接";
                        return reading;
                    }

                    byte[] answer = Send(command, reply, CmdBattery, 2, 1200);
                    if (answer == null)
                    {
                        reading.Sleeping = true;
                        reading.Error = "设备休眠";
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
                    reading.Detail = millivolts > 0 ? "2.4G " + millivolts + " mV" : "2.4G";
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

        /// <summary>
        /// 同一只接收器的各个集合共享路径里的实例前缀
        /// (`...&amp;col0N#7&amp;367F5458&amp;0&amp;000N`)，去掉末尾序号就是配对键。
        /// 每次读取重新枚举一次，拔插或换口后无需清理任何缓存。
        /// </summary>
        private static string FindReplyPath(DeviceInfo device)
        {
            string key = InstanceKey(device.HidPath);
            string only = null;
            int matches = 0;

            foreach (HidDeviceInfo hid in HidNative.Enumerate())
            {
                if (hid == null) continue;
                if (hid.VendorId != DareuVid || hid.ProductId != ReceiverPid) continue;
                if (hid.UsagePage != ReplyPage || hid.Usage != ReplyUsage) continue;
                if (hid.InputReportLength != FrameSize) continue;
                matches++;
                only = hid.Path;
                if (string.Equals(InstanceKey(hid.Path), key, StringComparison.OrdinalIgnoreCase)) return hid.Path;
            }

            // 前缀没对上（路径格式随系统版本变化）时，单接收器场景只有一个回传集合，直接用它。
            return matches == 1 ? only : null;
        }

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
