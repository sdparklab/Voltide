using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// DAREU（达尔优）A950 无线鼠标 —— 蓝牙（BLE / HOGP）模式。
    ///
    /// 蓝牙模式下这只鼠标不再是 260D:1074 接收器，而是标准的 BLE HID 设备：
    ///
    ///   接口路径 : hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&0225a7_pid&fa6c_rev&0300_<token>&colNN
    ///   VID/PID  : 0x25A7 / 0xFA6C   （配对名与设备节点名均为 "A950 BT"）
    ///   集合     : col01 鼠标 in=7、col02 键盘 in=9 out=2、col03 消费者 in=3
    ///
    /// 关键结论（实测，见 docs/protocol.md）：蓝牙下**没有任何厂商集合**
    /// （无 FF01/FF02 那一对），三个集合也都没有 FEATURE 报文，所以 2.4G 的
    /// 17 字节协议在这里根本不存在，读写都无从谈起。
    ///
    /// 电量的唯一来源是 Windows 自己缓存的蓝牙设备属性
    /// {104EA319-6EE2-4701-BD47-8DDBF425BBE5} pid 2（0..100，BYTE），
    /// 也就是「设置 → 蓝牙和其他设备」里显示的那个数值，由蓝牙栈从 GATT
    /// Battery Level（0x180F / 0x2A19）通知刷新。因此：
    ///   - 只读，不向设备写任何报文；
    ///   - 没有充电位（BLE Battery 只报百分比）-> ChargingKnown = false；
    ///   - 数值可能滞后，因为刷新由 GATT 通知驱动而不是由我们轮询。
    ///
    /// 插线实测（2026-11-25，蓝牙模式）：未插线时属性 50，插上充电线的同一分钟内跳到 100，
    /// 隔 20 秒连测三次都是 100，节点身份未变（仍是 BTHLE\Dev_…）。一分钟内 50→100 物理上
    /// 不可能，只能是固件在充电时把电池等级报成 100 —— 与 2.4G 侧官方驱动"插线显示 100%"
    /// 的行为一致。BLE 没有充电位，所以 100 与"真满电"在本路径上无法区分：
    /// 这里只转发原值、ChargingKnown=false，**不要根据 100 推断充电**。
    ///
    /// 拔线实测（同一分钟内继续）：拔掉充电线后属性逐档回落 ——
    /// 95 → 85 → 80 → 75（停 2 分钟）→ 65，4 分钟后仍高于插线前的 50。
    /// 也就是说这条读数不能当"当前实时电量"，Detail 因此标成「BT（估算）」。
    /// 拔线后 95 → 85 → 80 → 75 → 65 → 55 是虚报值回落到真实值的过程；按实测速率推算，插线那
    /// 3.4 分钟真实充进了约 5 个点（50 → 55），所以回落的终点 55 就是当时的真值。
    /// 硬结论只有一条：**插线时读到的 100 不是真实电量**，所以 detail 标成「BT（估算）」。
    ///
    /// 切回 2.4G 的实测（2026-11-25 09:52）：蓝牙设备节点**不会立刻消失**，它以旧值
    /// （实测 95）留在设备树里；而此刻 HOGP 的 HID 集合已经从目录里消失。所以认领必须
    /// 同时满足"HOGP 集合在"和"设备节点在"——只判节点就会把过期的 95 当成当前电量
    /// （真实值 55）。这条别为了省一次枚举而简化掉。
    ///
    /// 认领条件写死为「HOGP 集合 + VID/PID 匹配 + 设备节点此刻在连」：
    /// 鼠标切回 2.4G 后 BTHLE 节点不再 present，本 provider 自然认领 0 个设备，
    /// 不会和接收器那条链路打架。
    /// </summary>
    public class DareuA950BluetoothProvider : IDeviceProvider
    {
        private const ushort BtVid = 0x25A7;
        private const ushort BtPid = 0xFA6C;
        private const string HogpService = "{00001812-0000-1000-8000-00805f9b34fb}";

        private const string BtDetail = "BT（估算）";

        public string Name { get { return "DAREU BT"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null || catalog.Count == 0) return found;

            // 1) 先在共享的 HID 目录里找出属于 A950 的 HOGP 集合。
            string hidPath = null;
            string token = null;
            for (int i = 0; i < catalog.Count; i++)
            {
                HidDeviceInfo hid = catalog[i];
                if (hid == null || String.IsNullOrEmpty(hid.Path)) continue;
                if (hid.Path.IndexOf(HogpService, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!PathSaysVidPid(hid.Path, BtVid, BtPid)) continue;
                token = TokenOfPath(hid.Path);
                if (String.IsNullOrEmpty(token)) continue;
                hidPath = hid.Path;
                break;
            }
            if (hidPath == null) return found;

            // 2) 设备节点必须此刻在连：这一步同时回答「鼠标现在是不是蓝牙模式」。
            BtDeviceInfo node = BluetoothNative.FindByToken(BluetoothNative.Enumerate(), token);
            if (node == null) return found;

            DeviceInfo d = new DeviceInfo();
            d.Id = "dareu-a950-bt";
            d.Name = "DAREU A950 (BT)";
            d.Brand = "DAREU";
            d.Model = "A950";
            d.ShortModel = "A950 BT";
            d.Kind = DeviceKind.Mouse;
            d.VendorId = BtVid;
            d.ProductId = BtPid;
            d.HidPath = hidPath;
            d.ProviderName = Name;
            found.Add(d);
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Detail = BtDetail;
            r.Charging = false;
            r.ChargingKnown = false;   // 蓝牙没有充电位可读

            string token = device == null ? null : TokenOfPath(device.HidPath);
            BtDeviceInfo node = BluetoothNative.FindByToken(BluetoothNative.Enumerate(), token);

            if (node == null)
            {
                r.Online = false;
                r.Sleeping = true;            // 节点不在 = 未连接/已切回 2.4G
                r.Level = BatteryLevel.Unknown;
                r.Error = "蓝牙未连接";
                return r;
            }

            r.Raw = node.BatteryRaw;
            if (!node.BatteryPercent.HasValue)
            {
                r.Online = false;
                r.Level = BatteryLevel.Unknown;
                r.Error = "蓝牙电量不可用（系统未缓存）";
                return r;
            }

            r.Percent = node.BatteryPercent.Value;
            r.Level = BatteryMath.FromPercent(r.Percent.Value);
            r.Online = true;
            return r;
        }

        /// <summary>
        /// BLE HID 的接口路径里 VID/PID 是 _vid&02xxxx_pid&xxxx（小写十六进制、
        /// 4 位补零），这是唯一不需要打开设备就能确认身份的字段。
        /// </summary>
        private static bool PathSaysVidPid(string path, ushort vid, ushort pid)
        {
            string vidToken = "_vid&02" + vid.ToString("x4");
            string pidToken = "_pid&" + pid.ToString("x4");
            return path.IndexOf(vidToken, StringComparison.OrdinalIgnoreCase) >= 0
                && path.IndexOf(pidToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// "..._rev&0300_&lt;token&gt;&amp;col01#..." -> token。
        /// 该 token 与 BTHLE 设备实例 id 的尾段一致，用来把集合和设备节点配成一对。
        /// </summary>
        private static string TokenOfPath(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            int rev = path.IndexOf("_rev&", StringComparison.OrdinalIgnoreCase);
            if (rev < 0) return null;
            int under = path.IndexOf('_', rev + 5);
            if (under < 0) return null;
            int amp = path.IndexOf('&', under + 1);
            if (amp < 0) return null;
            string token = path.Substring(under + 1, amp - under - 1);
            return token.Length == 0 ? null : token;
        }
    }
}
