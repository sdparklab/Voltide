using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// CROCIRIS X7 Receiver 8K 无线鼠标接收器（2.4G）。
    ///
    /// 协议来自对官方 Crociris Hub KM（Electron/WebHID 应用，app.asar 渲染层）
    /// 的逆向：接收器适配器 DS_MOUSE_HID_ADAPTER 通过 WebHID 发送电量命令，
    /// 命令码 0x12，主机前缀 0xAA，设备回复前缀 0x55。
    ///
    /// WebHID 层 64 字节 data 布局：
    ///   data[0]=前缀, data[1]=cmd, data[2]=payload 长度, data[3]=结果, data[4..]=payload
    /// 映射到 HID 帧（report id 0 + 64 data，共 65 字节）：
    ///   请求 65B : 00 AA 12 00 00 00 ...
    ///                |  |  |  |  |
    ///                |  |  |  |  +-- 保留
    ///                |  |  |  +----- payload 长度 0
    ///                |  |  +-------- 命令 0x12 = 查询电量
    ///                |  +----------- 主机前缀 0xAA
    ///                +-------------- report id 0
    ///   回复 65B : 00 55 12 00 00 CC LL ...
    ///                             |  |
    ///                             |  +-- 索引 6 = 电量百分比 0-100
    ///                             +----- 索引 5 = 充电状态（1=充电中）
    ///
    /// 命令接口为厂商页 0xFF0A 的 65 字节可写集合（mi_03）；接收器同时暴露
    /// 多个标准键盘/鼠标 HID 接口，均不应答本命令。
    /// 读取超时 200ms；鼠标休眠/关机时接收器不回帧，按未应答处理。
    /// </summary>
    public class X7MouseProvider : IDeviceProvider
    {
        private const ushort X7Vid = 0x36F9;
        private const ushort X7Pid = 0xAA20;
        private const ushort VendorPage = 0xFF0A;
        private const int FrameLength = 65;

        private const byte HostPrefix = 0xAA;
        private const byte DevicePrefix = 0x55;
        private const byte CmdBattery = 0x12;

        private const int ChargingIndex = 5;
        private const int BatteryIndex = 6;

        public string Name { get { return "CROCIRIS X7"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != X7Vid) continue;
                if (h.ProductId != X7Pid) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.InputReportLength < FrameLength) continue;
                if (h.OutputReportLength < FrameLength) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "x7-mouse-" + h.ProductId.ToString("X4");
                d.Brand = "CROCIRIS";
                d.Model = "X7 Receiver 8K";
                d.Name = "X7 Receiver 8K";
                d.ShortModel = "X7";
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;

                found.Add(d);
                break; // 命令接口唯一，命中即止。
            }

            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;
            r.Detail = "2.4G · X7 Receiver 8K";

            try
            {
                using (HidSession session = new HidSession(device.HidPath, FrameLength, FrameLength))
                {
                    session.Flush(40);

                    byte[] reply = Query(session);
                    if (reply == null)
                    {
                        r.Sleeping = true;
                        r.Error = "设备未应答（可能已关机或休眠）";
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

                    r.Charging = reply[ChargingIndex] != 0;
                    r.ChargingKnown = true;
                }
            }
            catch (Exception ex)
            {
                r.Sleeping = true;
                r.Error = "设备未应答（可能已关机或休眠）";
                r.Detail = ex.Message;
            }

            return r;
        }

        private static byte[] Query(HidSession session)
        {
            byte[] request = new byte[FrameLength];
            request[0] = 0x00;       // report id
            request[1] = HostPrefix;
            request[2] = CmdBattery;
            request[3] = 0x00;       // payload 长度
            request[4] = 0x00;       // 保留

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!session.Write(request, 200)) continue;

                for (int i = 0; i < 10; i++)
                {
                    byte[] reply = session.Read(200);
                    if (reply == null) break;
                    if (reply.Length < BatteryIndex + 1) continue;
                    if (reply[1] != DevicePrefix) continue;
                    if (reply[2] != CmdBattery) continue;

                    return reply;
                }
            }

            return null;
        }
    }
}
