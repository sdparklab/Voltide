using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// LEADJOY 光剑 Saber Plus 手柄电量适配。
    ///
    /// 匹配：VID/PID 4131:3519；MI_04；UsagePage 0xFF80。
    /// 接口：64 字节数据；Windows 收发缓冲区另含值为 0 的报告 ID，共 65 字节。
    /// 协议来源：官方 PC 工具 device_manager_lib.dll 中的 Zhiwan 协议。
    ///
    /// 帧格式：A5 | LEN | CC | MessagePack | SUM。
    /// LEN 为整帧长度，SUM 为校验字节前各字节之和的低 8 位。
    /// 电量请求：A5 08 CC 81 A1 63 0E 0C，对应 {"c":14}。
    /// 电量回复示例：A5 1A CC 83 A1 63 0E A1 65 00 A1 72 83 A2 6D 62 4D A2 73 62 00 A2 63 73 00 69。
    /// 解码结果：{"c":14,"e":0,"r":{"mb":77,"sb":0,"cs":0}}。
    /// mb 为主电池百分比；sb 为副电池字段，已测设备上为 0。
    /// cs 为充电状态，已测值 0 表示未充电、1 表示充电；e 非 0 时不采用读数。
    ///
    /// 连接方式请求为 {"c":42}，回复示例为 {"c":42,"e":0,"r":{"l":2,"m":0}}。
    /// l 的映射来自官方库：0 为蓝牙，1 为有线，2 为 2.4G；m 为 0 表示 XInput 模式。
    /// 有线与接收器的 VID/PID 和序列号相同，连接方式通过该命令判断。
    /// 双路径同时应答时，l 可能均为 1；当前按型号合并路径并显示为“有线 + 2.4G”。
    /// 该合并方式尚不区分多台同型号手柄。
    ///
    /// 接收器的 XInput 电量接口报告有线满电，真实电量从厂商 HID 接口读取。
    /// 已测 2.4G 休眠后设备从系统枚举消失，此时沿用设备断开的处理方式。
    /// 官方工具也使用该接口，并行查询可能造成回复竞争；插线后上报百分比可能跳变。
    /// </summary>
    public class LeadjoyProvider : IDeviceProvider
    {
        private const ushort LeadjoyVid = 0x4131;
        private const ushort SaberPlusPid = 0x3519;
        private const ushort VendorPage = 0xFF80;

        private const int ReportLength = 65;      // 1 字节报告 ID（0）+ 64 字节数据。

        private const byte FrameHead = 0xA5;
        private const byte FrameCmd = 0xCC;
        private const int CmdGetBattery = 14;
        private const int CmdGetControllerMode = 42;

        public string Name { get { return "Leadjoy"; } }

        /// <summary>判断 VID/PID 是否属于本适配器支持的型号，供 XInput 去重使用。</summary>
        public static bool Owns(ushort vid, ushort pid)
        {
            return vid == LeadjoyVid && pid == SaberPlusPid;
        }

        // 保存当前型号的所有匹配路径，读取时逐条查询并合并为一张卡片。
        // 此处不提供多台同型号设备的独立身份。
        private List<string> _paths = new List<string>();

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            List<string> paths = new List<string>();
            HidDeviceInfo first = null;

            foreach (HidDeviceInfo h in catalog)
            {
                if (!Owns(h.VendorId, h.ProductId)) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.InputReportLength < ReportLength || h.OutputReportLength < ReportLength) continue;
                if (paths.Contains(h.Path)) continue;

                paths.Add(h.Path);
                if (first == null) first = h;
            }

            _paths = paths;

            if (first != null)
            {
                HidDeviceInfo h = first;
                DeviceInfo d = new DeviceInfo();
                d.Id = "leadjoy-" + h.ProductId.ToString("X4");
                d.Brand = "LEADJOY";
                d.Model = "SABER PLUS";
                d.ShortModel = "SABER+";
                d.Name = "光剑 Saber Plus";
                d.Kind = DeviceKind.Other;
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
            r.Detail = "";

            List<string> paths = new List<string>(_paths);
            if (paths.Count == 0) paths.Add(device.HidPath);

            bool wired = false, dongle = false, bluetooth = false;
            int answered = 0;
            List<string> raws = new List<string>();
            string error = null;

            foreach (string path in paths)
            {
                PathReading p = ReadPath(path);
                raws.Add(p.Raw);
                if (p.Link == 0) bluetooth = true;
                else if (p.Link == 1) wired = true;
                else if (p.Link > 1) dongle = true;

                if (p.Error != null) { if (error == null) error = p.Error; continue; }
                answered++;
                if (r.Percent.HasValue) continue;          // 使用首个有效电量读数。

                r.Online = true;
                r.Percent = p.Percent;
                r.Level = BatteryMath.FromPercent(p.Percent);
                if (p.Charging >= 0) r.Charging = p.Charging != 0;
                else r.ChargingKnown = false;
            }

            r.Raw = string.Join(" | ", raws.ToArray());

            // 已测双路径连接时，两条路径均可能报告 l=1。
            // 当前按单台手柄处理：多条路径应答且报告有线时，补充接收器连接标记。
            if (answered > 1 && wired && !dongle) dongle = true;

            List<string> links = new List<string>();
            if (wired) links.Add("有线");
            if (dongle) links.Add("2.4G");
            if (bluetooth) links.Add("蓝牙");
            if (links.Count > 0) r.Detail = string.Join(" + ", links.ToArray());

            if (!r.Percent.HasValue)
            {
                r.Sleeping = true;
                r.Error = error ?? "手柄未应答（可能已关机或休眠）";
            }

            return r;
        }

        private class PathReading
        {
            public int Percent;
            public long Charging = -1;
            public long Link = -1;
            public string Error;
            public string Raw = "";
        }

        /// <summary>读取单条 HID 路径的电量与连接方式；连接方式无有效回复时保留电量结果。</summary>
        private static PathReading ReadPath(string path)
        {
            PathReading p = new PathReading();
            try
            {
                using (HidSession session = new HidSession(path, ReportLength, ReportLength))
                {
                    session.Flush(20);

                    Dictionary<string, object> reply = Query(session, CmdGetBattery);
                    long err = AsLong(Get(reply, "e"), -1);
                    Dictionary<string, object> body = Get(reply, "r") as Dictionary<string, object>;
                    if (reply == null || err != 0 || body == null)
                    {
                        p.Error = reply == null ? "手柄未应答（可能已关机或休眠）" : "手柄未应答（e=" + err + "）";
                        p.Raw = "no-reply";
                    }
                    else
                    {
                        long mb = AsLong(Get(body, "mb"), -1);
                        p.Charging = AsLong(Get(body, "cs"), -1);
                        p.Raw = "mb=" + mb + " sb=" + AsLong(Get(body, "sb"), -1) + " cs=" + p.Charging;
                        if (mb < 0 || mb > 100) p.Error = "读数异常";
                        else p.Percent = (int)mb;
                    }

                    Dictionary<string, object> mode = Query(session, CmdGetControllerMode);
                    Dictionary<string, object> modeBody = Get(mode, "r") as Dictionary<string, object>;
                    if (AsLong(Get(mode, "e"), -1) == 0 && modeBody != null)
                    {
                        p.Link = AsLong(Get(modeBody, "l"), -1);
                        p.Raw += " l=" + p.Link;
                    }
                }
            }
            catch (Exception ex)
            {
                p.Error = "手柄未应答（可能已关机或休眠）";
                p.Raw = ex.Message;
            }
            return p;
        }

        // 协议帧构造与解析。

        private static Dictionary<string, object> Query(HidSession session, int command)
        {
            byte[] request = BuildRequest(command);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!session.Write(request, 200)) continue;

                for (int i = 0; i < 8; i++)
                {
                    byte[] report = session.Read(200);
                    if (report == null) break;

                    Dictionary<string, object> msg = ParseFrame(report);
                    if (msg == null) continue;
                    if (AsLong(Get(msg, "c"), -1) != command) continue;
                    return msg;
                }
            }

            return null;
        }

        /// <summary>构造 {"c":command} 请求；命令使用 MessagePack 正定长整数，取值为 0..127。</summary>
        private static byte[] BuildRequest(int command)
        {
            byte[] report = new byte[ReportLength];
            int o = 1;                                  // 第 0 字节为值为 0 的报告 ID。
            report[o++] = FrameHead;
            report[o++] = 0x08;                         // 协议帧长度为 8 字节。
            report[o++] = FrameCmd;
            report[o++] = 0x81;                         // MessagePack 单元素定长映射。
            report[o++] = 0xA1; report[o++] = (byte)'c';
            report[o++] = (byte)(command & 0x7F);

            int sum = 0;
            for (int i = 1; i < o; i++) sum += report[i];
            report[o] = (byte)sum;
            return report;
        }

        private static Dictionary<string, object> ParseFrame(byte[] report)
        {
            // Windows 报告缓冲区包含前导报告 ID，本接口使用 0。
            int start = (report.Length >= ReportLength && report[0] == 0x00) ? 1 : 0;
            if (report.Length < start + 5) return null;
            if (report[start] != FrameHead || report[start + 2] != FrameCmd) return null;

            int len = report[start + 1];
            if (len < 5 || start + len > report.Length) return null;

            int sum = 0;
            for (int i = start; i < start + len - 1; i++) sum += report[i];
            if ((byte)sum != report[start + len - 1]) return null;

            int pos = start + 3;
            int end = start + len - 1;
            object v;
            try { v = MsgPack.Read(report, ref pos, end); }
            catch { return null; }
            return v as Dictionary<string, object>;
        }

        private static object Get(Dictionary<string, object> map, string key)
        {
            object v;
            return (map != null && map.TryGetValue(key, out v)) ? v : null;
        }

        private static long AsLong(object v, long fallback)
        {
            if (v is long) return (long)v;
            if (v is bool) return (bool)v ? 1 : 0;
            return fallback;
        }

        /// <summary>
        /// 解码本协议使用的 MessagePack 整数、空值、布尔值、字符串、映射和数组。
        /// 遇到不支持的类型或越界数据时抛出异常，由帧解析器拒绝该回复。
        /// </summary>
        private static class MsgPack
        {
            public static object Read(byte[] b, ref int p, int end)
            {
                byte t = Take(b, ref p, end);

                if (t <= 0x7F) return (long)t;
                if (t >= 0xE0) return (long)(sbyte)t;
                if ((t & 0xF0) == 0x80) return ReadMap(b, ref p, end, t & 0x0F);
                if ((t & 0xF0) == 0x90) return ReadArray(b, ref p, end, t & 0x0F);
                if ((t & 0xE0) == 0xA0) return ReadStr(b, ref p, end, t & 0x1F);

                switch (t)
                {
                    case 0xC0: return null;
                    case 0xC2: return false;
                    case 0xC3: return true;
                    case 0xCC: return (long)Uint(b, ref p, end, 1);
                    case 0xCD: return (long)Uint(b, ref p, end, 2);
                    case 0xCE: return (long)Uint(b, ref p, end, 4);
                    case 0xD0: return (long)(sbyte)Uint(b, ref p, end, 1);
                    case 0xD1: return (long)(short)Uint(b, ref p, end, 2);
                    case 0xD2: return (long)(int)Uint(b, ref p, end, 4);
                    case 0xD9: return ReadStr(b, ref p, end, (int)Uint(b, ref p, end, 1));
                    case 0xDA: return ReadStr(b, ref p, end, (int)Uint(b, ref p, end, 2));
                    case 0xDC: return ReadArray(b, ref p, end, (int)Uint(b, ref p, end, 2));
                    case 0xDE: return ReadMap(b, ref p, end, (int)Uint(b, ref p, end, 2));
                }
                throw new FormatException("msgpack type 0x" + t.ToString("X2"));
            }

            private static Dictionary<string, object> ReadMap(byte[] b, ref int p, int end, int n)
            {
                Dictionary<string, object> m = new Dictionary<string, object>();
                for (int i = 0; i < n; i++)
                {
                    object k = Read(b, ref p, end);
                    object v = Read(b, ref p, end);
                    m[k == null ? "" : k.ToString()] = v;
                }
                return m;
            }

            private static List<object> ReadArray(byte[] b, ref int p, int end, int n)
            {
                List<object> a = new List<object>(n);
                for (int i = 0; i < n; i++) a.Add(Read(b, ref p, end));
                return a;
            }

            private static string ReadStr(byte[] b, ref int p, int end, int n)
            {
                if (p + n > end) throw new FormatException("msgpack string overrun");
                string s = System.Text.Encoding.UTF8.GetString(b, p, n);
                p += n;
                return s;
            }

            private static uint Uint(byte[] b, ref int p, int end, int n)
            {
                uint v = 0;
                for (int i = 0; i < n; i++) v = (v << 8) | Take(b, ref p, end);   // 按大端字节序读取。
                return v;
            }

            private static byte Take(byte[] b, ref int p, int end)
            {
                if (p >= end) throw new FormatException("msgpack overrun");
                return b[p++];
            }
        }
    }
}
