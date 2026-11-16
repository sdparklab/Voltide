using System;
using System.Collections.Generic;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    public class EweadnA5ProProvider : IDeviceProvider
    {
        private byte _sequence;
        public string Name { get { return "EWEADN A5 Pro"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            bool usbConnected = false;
            foreach (HidDeviceInfo h in catalog)
            {
                // 鼠标协议没有独立的充电标识位，USB 有线连接按充电状态处理。
                if (h.VendorId == 0x258A && h.ProductId == 0x0136 &&
                    h.UsagePage == 0xFF02 && h.Usage == 2 &&
                    h.FeatureReportLength == 520 &&
                    string.Equals(h.ProductName, "EWEADN A5Pro", StringComparison.OrdinalIgnoreCase))
                    usbConnected = true;
            }
            foreach (HidDeviceInfo h in catalog)
            {
                // 官方 A5 Pro 驱动使用 0138 作为无线接收器 PID。
                if (h.VendorId != 0x258A || h.ProductId != 0x0138 ||
                    h.UsagePage != 0xFF02 || h.Usage != 2 ||
                    h.FeatureReportLength != 520) continue;
                DeviceInfo d = new DeviceInfo();
                d.Id = "eweadn-a5pro-" + h.Path;
                d.Brand = "EWEADN";
                d.Model = "A5 Pro";
                d.Name = "前行者 A5 Pro";
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                d.IsCharging = usbConnected;
                found.Add(d);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo d)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Level = BatteryLevel.Unknown;
            // 先根据 USB 连接初始化充电状态是否已知，再根据有效回复修正。
            r.ChargingKnown = d.IsCharging;
            try
            {
                using (HidSession s = new HidSession(d.HidPath, 8, 0))
                {
                    // 对应 OemDrv.exe 的 GetPower（0x496C20）：报告 ID 9、命令 9，读取 1 字节。
                    // 第 1 字节为第 2..519 字节之和的低 8 位。
                    byte sequence = _sequence++;
                    byte[] query = new byte[520];
                    query[0] = 9;
                    query[1] = (byte)((12 + sequence) & 255);
                    query[2] = 9;
                    query[3] = 1;
                    query[4] = 1;
                    query[5] = sequence;
                    query[6] = 1;
                    if (!HidNative.HidD_SetFeature(s.Handle, query, query.Length))
                        throw new InvalidOperationException("电量查询发送失败");
                    Thread.Sleep(100);
                    byte[] reply = new byte[520];
                    reply[0] = 9;
                    if (!HidNative.HidD_GetFeature(s.Handle, reply, reply.Length) ||
                        reply[0] != 9 || reply[2] != 9 || reply[3] != 1 ||
                        reply[4] != 1 || reply[5] != sequence ||
                        reply[6] != 1 || reply[7] != 0)
                        throw new InvalidOperationException("未收到有效电量回复");
                    r.Raw = BitConverter.ToString(reply, 0, 16);
                    // 鼠标没有独立的充电标识位，充电时不返回电量，电量字段改为 0xFF。
                    // 将 0xFF 或 USB 有线连接视为充电，百分比留空，不采用接收器缓存的旧值。
                    if (d.IsCharging || reply[8] == 0xFF)
                    {
                        // 充电期间无有效百分比，由界面以灰色显示已有历史读数。
                        // 拔线后电量恢复存在延迟，继续按接收器当前回复判断状态。
                        r.Online = true;
                        r.Charging = true;
                        r.ChargingKnown = true;
                        r.Detail = d.IsCharging ? "有线" : "2.4G";
                        return r;
                    }
                    if (reply[8] > 100)
                        throw new InvalidOperationException("未收到有效电量回复");
                    r.Percent = reply[8];
                    r.Level = BatteryMath.FromPercent(reply[8]);
                    // 按已测状态，将非 USB 连接且返回有效电量的情况视为放电。
                    // 插拔电源后，接收器上报状态可能滞后一轮刷新。
                    r.ChargingKnown = true;
                    r.Charging = d.IsCharging;
                    r.Detail = d.IsCharging ? "有线" : "2.4G";
                    r.Online = true;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }
    }
}
