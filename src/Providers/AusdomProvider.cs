using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// AUSDOM G03 Ultra 8K 鼠标（JUXING 主控方案）：2.4G 接收器 363C:FF9F、
    /// USB 直连本体 363C:FF9E，两者暴露同款 FFA0/0002 命令接口。
    ///
    /// 协议来自厂商 Web 驱动 https://ausdomhubg.com/#/ 的逆向：页面为 Vue SPA，
    /// 核心逻辑在 chunk device-C2Owckh7.js，经 WebHID sendReport(11, data) 与
    /// 接收器通信，data 补零至 63 字节。
    ///
    /// 请求（64B = report id 11 + 63B data）：
    ///   0B 13 00 00 00 ...
    ///      |  |  |
    ///      |  |  +-- 参数 0
    ///      |  +----- 命令 0x13 = 取 mouse_info（含电量/充电/连接状态）
    ///      +-------- output report id 11
    ///
    /// 回复（64B，input report id 0x0A）。WebHID 视角 data 不含 report id，
    /// 即 e[i] = buf[i+1]：
    ///   0A 13 37 00 05 ... 01 00 48 00 01 00 ...
    ///      |  |  |  |              |  |  |
    ///      |  |  |  |              |  |  +-- e[19]=buf[20] 1=2.4G 已连接
    ///      |  |  |  |              |  +----- e[17]=buf[18] 电量百分比 0-100
    ///      |  |  |  |              +-------- e[16]=buf[17] 1=充电中（实测插线充电时 e[19] 仍为 1）
    ///      |  |  |  +---------------------- e[3]=buf[4] 模式 0/1/4/5=无线 2/3/6/7=有线
    ///      |  |  +------------------------ e[2]=buf[3] 00=成功
    ///      |  +--------------------------- e[1]=buf[2] 回复 payload 长度（0x13→0x37，0x14→0x03）
    ///      +------------------------------- e[0]=buf[1] 命令回显 0x13
    ///
    /// 充电判定：e[16]==1 且未满电即为充电中。Web 驱动原式为
    ///   (e[19]==1 || e[17]>=100) ? false : (e[16]==1)
    /// 但实测插线充电时 e[19] 仍为 1，该式会永远短路成不充电，故不采用。
    ///
    /// 实机验证（2026-11-19，接收器 363C:FF9F，接口 mi_01&col05
    /// usagePage=0xFFA0 usage=0x0002 in=64 out=256）：
    ///   请求 0B 13 00 00 ... →
    ///   放电 0A 13 37 00 05 FE FB CA 9A 3A A3 15 02 15 02 02 01 00 48 00 01 00 00 ...
    ///   充电 0A 13 37 00 05 FE FB CA 9A 3A A3 15 02 15 02 02 01 01 49 00 01 00 00 ...
    ///   差异仅 e[16] 0→1（充电位）、e[17] 0x48→0x49（电量 72→73 涨 1%）；
    ///   e[19] 两种状态均为 1，e[3]=5 无线模式。
    ///   USB 直连本体 363C:FF9E 同接口同协议：
    ///   0A 13 37 00 03 ... 02 01 01 5D 00 00 00 ... → e[3]=3 有线、e[16]=1 充电、
    ///   e[17]=93%、e[19]=0；同刻接收器 FF9F 报 83% 放电（无线断开前的缓存），
    ///   故 USB 本体在线时抑制接收器卡。
    ///   WriteFile 即可送达（无需 SET_REPORT）；鼠标休眠时接收器不回帧。
    /// </summary>
    public class AusdomProvider : IDeviceProvider
    {
        private const ushort AusdomVid = 0x363C;
        private const ushort DonglePid = 0xFF9F;   // G03 Ultra 8K 2.4G 接收器
        private const ushort UsbPid = 0xFF9E;      // G03 Ultra 8K 鼠标本体（USB 直连）
        private const ushort VendorPage = 0xFFA0;  // 厂商扩展页
        private const ushort VendorUsage = 0x0002;
        private const int InLength = 64;           // 实测 input report 长度
        private const int OutLength = 256;         // 实测 output report 长度

        private const byte ReportIdOut = 11;       // 发送 report id（WebHID sendReport 首参）
        private const byte ReportIdIn = 0x0A;      // 回复 report id
        private const byte CmdMouseInfo = 0x13;    // 取 mouse_info（含电量）

        private const int LengthIndex = 2;         // buf[2]=e[1]，回复 payload 长度
        private const int StatusIndex = 3;         // buf[3]=e[2]，0x00=成功
        private const int ModeIndex = 4;           // e[3]
        private const int ChargingIndex = 17;      // e[16]
        private const int BatteryIndex = 18;       // e[17]
        private const int ConnectedIndex = 20;     // e[19]

        public string Name { get { return "AUSDOM G03"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            // USB 直连的鼠标本体（FF9E）实时上报自身电量；此时接收器（FF9F）
            // 只剩无线断开前的缓存旧值（实测 93% 充电中 vs 83% 放电中），
            // 有本体在线就不再展示接收器卡，拔掉 USB 后自动回落。
            bool usbAttached = false;
            foreach (HidDeviceInfo h in catalog)
            {
                if (IsCommandInterface(h) && h.ProductId == UsbPid)
                {
                    usbAttached = true;
                    break;
                }
            }

            List<DeviceInfo> found = new List<DeviceInfo>();
            foreach (HidDeviceInfo h in catalog)
            {
                if (!IsCommandInterface(h)) continue;
                if (usbAttached && h.ProductId == DonglePid) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "ausdom-g03-" + h.ProductId.ToString("X4");
                d.Brand = "AUSDOM";
                d.Model = "G03";
                d.Name = "G03 Ultra 8K";
                d.ShortModel = "G03";
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;

                found.Add(d);
                break; // 命令接口唯一（col05），命中即止。
            }

            return found;
        }

        private static bool IsCommandInterface(HidDeviceInfo h)
        {
            return h.VendorId == AusdomVid
                && (h.ProductId == DonglePid || h.ProductId == UsbPid)
                && h.UsagePage == VendorPage
                && h.Usage == VendorUsage
                && h.InputReportLength == InLength
                && h.OutputReportLength >= InLength;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;
            bool usbAttached = device.ProductId == UsbPid;
            r.Detail = usbAttached
                ? "USB 直连 · AUSDOM G03 Ultra 8K"
                : "2.4G · AUSDOM G03 Ultra 8K";

            try
            {
                using (HidSession session = new HidSession(device.HidPath, InLength, OutLength))
                {
                    session.Flush(80);

                    byte[] reply = Query(session);
                    if (reply == null)
                    {
                        r.Sleeping = true;
                        r.Error = "设备未应答（可能已关机或休眠）";
                        return r;
                    }

                    int percent = reply[BatteryIndex];
                    r.Raw = BitConverter.ToString(reply, 0, 24);

                    if (percent < 0 || percent > 100)
                    {
                        r.Online = true;
                        r.Error = "读数异常";
                        return r;
                    }

                    r.Percent = percent;
                    r.Level = BatteryMath.FromPercent(percent);
                    r.Online = true;

                    // 充电位就是 e[16]，实测插线充电时 e[19]（2.4G 连接）仍为 1，
                    // 不能像 Web 驱动那样用 e[19]==1 短路掉充电显示；满电沿用其逻辑。
                    bool connected = reply[ConnectedIndex] == 1;
                    r.Charging = reply[ChargingIndex] == 1 && percent < 100;
                    r.ChargingKnown = true;

                    if (!usbAttached && !connected)
                        r.Detail = "接收器在线 · 鼠标未连接";
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
            byte[] request = new byte[OutLength];
            request[0] = ReportIdOut;
            request[1] = CmdMouseInfo;
            request[2] = 0x00;                  // 参数 0
            request[3] = 0x00;

            // 接收器回复延迟可达数百 ms（实测 200ms 内读不到），首读必须放宽；
            // 超时后本轮作废，Flush 会把迟到的回复冲掉，故直接重发下一轮。
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!session.Write(request, 400)) continue;

                for (int i = 0; i < 4; i++)
                {
                    byte[] reply = session.Read(i == 0 ? 1200 : 400);
                    if (reply == null) break;
                    if (reply.Length <= StatusIndex) continue;
                    if (reply[0] != ReportIdIn) continue;
                    if (reply[1] != CmdMouseInfo) continue;
                    if (reply[StatusIndex] != 0x00) continue;

                    return reply;
                }
            }

            return null;
        }
    }
}
