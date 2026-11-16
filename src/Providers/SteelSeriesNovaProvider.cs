using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// SteelSeries Arctis Nova Pro Wireless 耳机电量适配。
    ///
    /// 匹配：无线基站 1038:12E0；X 版基站 1038:12E5。
    /// 接口：MI_04；UsagePage 0xFFC0；64 字节报告，首字节为报告 ID 0x06。
    /// 协议来源：HeadsetControl 的 steelseries_arctis_nova_pro_wireless 协议字段。
    ///
    /// 状态请求：06 B0 00 ...。
    /// 回复示例：06 B0 00 00 01 00 04 08 08 00 02 0A 05 01 08 08 ...。
    /// reply[6] 为电量档位 0..8；百分比按档位乘 100 / 8 后四舍五入为整数，分辨率为 12.5%。
    /// reply[15] 为耳机状态：0x01 未连接，0x02 线缆充电，0x08 在线。
    /// 未连接时回复中的 0 档不作为有效电量，交由界面保留历史读数。
    ///
    /// 耳机 USB 接口用于充电和固件下载，电量与充电状态仍通过无线基站查询。
    /// 同一接口下的 0xFF00 集合不接受该查询，需使用 0xFFC0 集合。
    /// 已在 12E0 基站验证在线、充电和休眠状态；12E5 保留协议匹配，尚待硬件验证。
    /// SteelSeries GG 同样使用该接口，读取时跳过其他命令的回复。
    /// </summary>
    public class SteelSeriesNovaProvider : IDeviceProvider
    {
        private const ushort SteelSeriesVid = 0x1038;
        private const ushort NovaProPid = 0x12E0;
        private const ushort NovaProXPid = 0x12E5;
        private const ushort VendorPage = 0xFFC0;

        private const int ReportLength = 64;
        private const byte ReportId = 0x06;
        private const byte CmdStatus = 0xB0;

        private const int LevelIndex = 6;
        private const int StatusIndex = 15;
        private const int MaxLevel = 8;

        private const byte StatusOffline = 0x01;
        private const byte StatusCableCharging = 0x02;
        private const byte StatusOnline = 0x08;

        public string Name { get { return "SteelSeries Nova"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != SteelSeriesVid) continue;
                if (h.ProductId != NovaProPid && h.ProductId != NovaProXPid) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.InputReportLength < ReportLength || h.OutputReportLength < ReportLength) continue;

                bool x = h.ProductId == NovaProXPid;

                DeviceInfo d = new DeviceInfo();
                d.Id = "steelseries-nova-" + h.ProductId.ToString("X4");
                d.Brand = "SteelSeries";
                d.Model = x ? "Nova Pro X" : "Nova Pro";
                d.ShortModel = x ? "NOVA PRO X" : "NOVA PRO";
                d.Name = "Arctis " + d.Model + " Wireless";
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
            r.Detail = "2.4G";

            try
            {
                using (HidSession session = new HidSession(device.HidPath, ReportLength, ReportLength))
                {
                    session.Flush(20);

                    byte[] reply = Query(session);
                    if (reply == null)
                    {
                        r.Sleeping = true;
                        r.Error = "基站未应答";
                        return r;
                    }

                    byte status = reply[StatusIndex];
                    byte level = reply[LevelIndex];
                    r.Raw = "level=" + level + " status=0x" + status.ToString("X2");

                    if (status == StatusOffline)
                    {
                        r.Sleeping = true;
                        r.Error = "耳机未连接（可能已关机或休眠）";
                        return r;
                    }

                    if (level > MaxLevel)
                    {
                        r.Online = true;
                        r.Error = "读数异常";
                        return r;
                    }

                    int percent = (level * 100 + MaxLevel / 2) / MaxLevel;
                    r.Online = true;
                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);

                    if (status == StatusCableCharging) r.Charging = true;
                    else if (status != StatusOnline) r.ChargingKnown = false;
                }
            }
            catch (Exception ex)
            {
                r.Sleeping = true;
                r.Error = "基站未应答";
                r.Raw = ex.Message;
            }

            return r;
        }

        private static byte[] Query(HidSession session)
        {
            byte[] request = new byte[ReportLength];
            request[0] = ReportId;
            request[1] = CmdStatus;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!session.Write(request, 200)) continue;

                // 同一接口可能包含 GG 的其他命令回复，仅接受当前状态命令。
                for (int i = 0; i < 8; i++)
                {
                    byte[] reply = session.Read(200);
                    if (reply == null) break;
                    if (reply.Length <= StatusIndex) continue;
                    if (reply[0] != ReportId || reply[1] != CmdStatus) continue;
                    return reply;
                }
            }

            return null;
        }
    }
}
