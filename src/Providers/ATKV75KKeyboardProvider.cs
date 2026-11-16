using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// ATK V75 series 2.4G keyboards (V75K / V75X share PID 0x5088).
    ///
    /// Dongle: VID 0x320F, PID 0x5088, product "ATK V75X 2.4G Dongle", mfr "ATK".
    /// Composite HID device. The command channel is MI_01 col04; the other
    /// collections either fail CreateFile or reject WriteFile. col05 accepts writes
    /// but the firmware just echoes the frame back, so a query there never reaches
    /// the keyboard.
    ///
    /// Protocol recovered from USBPcap and replayed with a bare WriteFile/ReadFile
    /// pair (2026-11-16):
    ///
    ///   Query - 64-byte frame over interrupt OUT, report id 0x04:
    ///
    ///     04 11 00 1A 00 00 00 00  + 56 zero bytes
    ///     ^^ ^^    ^^
    ///     |  |     +-- 0x1A query tail
    ///     |  +-------- block 0x11: battery (0x22 is the backup copy)
    ///     +----------- report id
    ///
    ///   Reply - 64-byte input report from the same handle, verified capture at 91%:
    ///
    ///     04 11 00 1A 00 00 00 00 5B 00 ...
    ///                               ^^
    ///                               +-- percent (0x5B = 91)
    ///
    ///   No checksum, sequence number or length field, and the firmware does not
    ///   check one either. Adding the 0x4D checksum the A9 Air mouse needs shifts the
    ///   command and the keyboard stops answering.
    ///
    ///   The generic block head 04 11 00 1B 18 &lt;off_lo&gt; &lt;off_hi&gt; 00 also reads
    ///   block 0x11 and returns the same 8-byte prefix shape, but byte 8 is not the
    ///   percentage: one capture gave 0x6A (106) while the keyboard was really at
    ///   91%. Reply[3] is checked so those frames are skipped.
    ///
    ///   Byte 9 is the charge state, located by diffing captures taken before and
    ///   after the cable was plugged in. Three values, all observed on one charge:
    ///
    ///     04 11 00 1A 00 00 00 00 59 00 ...   89%, unplugged
    ///     04 11 00 1A 00 00 00 00 5A 01 ...   90%, charging
    ///     04 11 00 1A 00 00 00 00 64 02 ...  100%, plugged and full
    ///
    ///   So 0x02 is a state of its own, not a repeat of 0x01: charging is 0x01,
    ///   while "cable attached" is 0x01 or 0x02. It only changes on plug/unplug and
    ///   carries no current or time-to-full.
    ///
    ///   No coulomb counter. The firmware is a voltage lookup table, so plugging
    ///   the cable can push the reading to 100% within minutes.
    ///
    ///   With the keyboard switched off the dongle stays enumerated but silent. That
    ///   is reported as sleeping, keeping the last reading on the card.
    /// </summary>
    public class AtkKeyboardProvider : IDeviceProvider
    {
        private const ushort AtkVid = 0x320F;
        private const ushort V75SeriesPid = 0x5088;

        private const int FrameSize = 64;
        private const byte ReportId = 0x04;
        private const byte CmdBattery = 0x11;
        private const byte QueryTail = 0x1A;         // distinguishes the battery reply from block replies
        private const int PercentIndex = 8;
        private const int ChargeIndex = 9;
        private const byte StateDischarging = 0x00;
        private const byte StateCharging = 0x01;
        private const byte StateFull = 0x02;         // plugged in and already at 100%
        private const string CommandCollection = "col04";   // the only writable collection on this dongle

        private const string ProductPrefix = "ATK ";         // dongle product string: "ATK V75X 2.4G Dongle"
        private const string ProductSuffix = " 2.4G Dongle";
        private const string FallbackModel = "V75K";         // used when the product string is empty

        public string Name { get { return "ATK HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            List<HidDeviceInfo> all = catalog;

            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();
            foreach (HidDeviceInfo h in all)
            {
                if (h.VendorId != AtkVid) continue;
                if (h.ProductId != V75SeriesPid) continue;
                if (h.InputReportLength != FrameSize) continue;
                if (h.OutputReportLength != FrameSize) continue;
                if (h.Path == null) continue;
                if (h.Path.IndexOf(CommandCollection, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;

                DeviceInfo d = new DeviceInfo();
                d.Id = "atk-" + kv.Key;
                d.Brand = "ATK";
                d.Model = ResolveModel(h.ProductName);
                d.Name = "ATK " + d.Model;
                d.ShortModel = d.Model;
                d.Kind = DeviceKind.Keyboard;
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

            try
            {
                byte[] cmd = NewQuery();

                using (HidSession s = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    s.Flush(30);

                    byte[] resp = null;

                    for (int attempt = 0; attempt < 3 && resp == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);

                        // An awake keyboard answers in a few milliseconds; only the
                        // first attempt gets the long budget.
                        int budget = attempt == 0 ? 800 : 500;

                        if (!s.Write(cmd, 200))
                        {
                            r.Error = "发送失败（设备被占用？）";
                            return r;
                        }

                        resp = WaitFor(s, budget);
                    }

                    if (resp == null)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠或关机";
                        return r;
                    }

                    r.Raw = Hex(resp);

                    int level = resp[PercentIndex];
                    if (level > 100)
                    {
                        r.Online = true;
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    byte state = resp[ChargeIndex];

                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    r.Charging = state == StateCharging;
                    r.ChargingKnown = state == StateDischarging || state == StateCharging || state == StateFull;
                    r.Online = true;
                    r.Detail = state == StateFull ? "2.4G · 已充满" : "2.4G";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        private static byte[] NewQuery()
        {
            byte[] cmd = new byte[FrameSize];
            cmd[0] = ReportId;
            cmd[1] = CmdBattery;
            cmd[3] = QueryTail;
            return cmd;
        }

        private static byte[] WaitFor(HidSession s, int budgetMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(budgetMs);
            while (DateTime.Now < deadline)
            {
                byte[] p = s.Read(150);
                if (p == null) continue;
                if (p.Length > ChargeIndex && p[0] == ReportId && p[1] == CmdBattery && p[3] == QueryTail) return p;
            }
            return null;
        }

        private static string ResolveModel(string productName)
        {
            if (!string.IsNullOrEmpty(productName))
            {
                string name = productName.Trim();
                if (name.StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(ProductPrefix.Length);
                if (name.EndsWith(ProductSuffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ProductSuffix.Length);

                name = name.Trim();
                if (name.Length > 0) return name;
            }
            return FallbackModel;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
    }
}
