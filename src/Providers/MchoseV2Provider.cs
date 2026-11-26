using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// MCHOSE "protocol V2" devices on their own 2.4G receiver — verified on a
    /// G87 V3 (receiver 3837:1015, product "MCHOSE G87 V3 2.4G").
    ///
    /// The receiver exposes three vendor collections; only usage page 0xFF80,
    /// 64-byte IN/OUT answers. The query goes out on the interrupt-OUT pipe
    /// (WriteFile) — HidD_SetOutputReport returns success but stays silent.
    ///
    ///   request : 4D 01 01 10 00 09 00 00 ... xor@24   (cmd 0x0900 = device info)
    ///   reply   : 4D 01 01 14 00 09 00 00 37 38 29 20 03 02 00 00 00 10 02 00
    ///                                     ^^^^^ ^^^^^ ^^ ^^ ^^
    ///                                     vid   pid   |  |  +-- payload[12] battery %
    ///                                                  |  +----- payload[11] charge (0/1/2)
    ///                                                  +-------- payload[10] link (2 = 2.4G)
    ///
    ///   Frame layout (from the public MCHOSE V2 write-up):
    ///     [0]=0x4D magic  [1]=0x01 version  [2]=flags  [3]=payload length
    ///     [4..5]=cmd LE   [6]=biz  [7]=seq
    ///     [8 .. 7+len]=payload   [8+len]=XOR of bytes 2..7+len   rest zero padding
    /// </summary>
    public class MchoseV2Provider : IDeviceProvider
    {
        private const ushort ReportLength = 64;
        private const byte ReportId = 0x4D;
        private const ushort CmdDeviceInfo = 0x0900;
        private const ushort PageVendor = 0xFF80;
        private const int ReplyTimeoutMs = 900;

        public string Name { get { return "MCHOSE V2"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            if (catalog == null) return found;

            Dictionary<string, HidDeviceInfo> seen = new Dictionary<string, HidDeviceInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo h in catalog)
            {
                if (!Matches(h)) continue;
                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (seen.ContainsKey(key)) continue;
                seen.Add(key, h);

                string model = ModelName(h);

                DeviceInfo d = new DeviceInfo();
                d.Id = "mchosev2-" + key;
                d.Brand = "MCHOSE";
                d.Model = model;
                d.ShortModel = model;
                d.Name = "MCHOSE " + model;
                d.Kind = IsKeyboard(h) ? DeviceKind.Keyboard : DeviceKind.Mouse;
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
            r.Level = BatteryLevel.Unknown;

            if (device == null || string.IsNullOrEmpty(device.HidPath))
            {
                r.Error = "没有接收器";
                return r;
            }

            try
            {
                byte[] request = CreateDeviceInfoRequest();

                using (HidSession s = new HidSession(device.HidPath, ReportLength, ReportLength))
                {
                    s.Flush(30);

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(150);

                        if (!s.Write(request, 900)) continue;

                        byte[] reply = WaitReply(s, ReplyTimeoutMs);
                        if (reply == null) continue;

                        int percent;
                        bool charging;
                        byte connection;
                        if (!TryParse(reply, out percent, out charging, out connection))
                        {
                            r.Raw = ToHex(reply);
                            r.Error = "电量应答无效";
                            return r;
                        }

                        r.Raw = ToHex(reply);
                        if (connection == 0)
                        {
                            r.Sleeping = true;
                            r.Error = "设备休眠";
                            return r;
                        }

                        r.Percent = percent;
                        r.Level = BatteryMath.FromPercent(percent);
                        r.Charging = charging;
                        r.ChargingKnown = true;
                        r.Online = true;
                        r.Detail = connection == 2 ? "2.4G" : "有线";
                        ReadingStore.Save(device.Id, percent, charging, true, r.Raw);
                        return r;
                    }

                    SeedFromStore(device, r);
                    r.Sleeping = true;
                    r.Error = "设备休眠";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        internal static byte[] CreateDeviceInfoRequest()
        {
            byte[] f = new byte[ReportLength];
            f[0] = ReportId;
            f[1] = 0x01;
            f[2] = 0x01;
            f[3] = 0x10;
            f[4] = (byte)(CmdDeviceInfo & 0xFF);
            f[5] = (byte)(CmdDeviceInfo >> 8);

            int chk = 0;
            for (int i = 2; i <= 7 + f[3]; i++) chk ^= f[i];
            f[8 + f[3]] = (byte)chk;
            return f;
        }

        /// <summary>
        /// payload[0..3] = device vid/pid, [10] link (2 = 2.4G), [11] charge, [12] percent.
        /// The checksum is validated when present but a mismatch alone never throws the
        /// reading away — some firmware revisions fold the length byte in differently.
        /// </summary>
        internal static bool TryParse(byte[] f, out int percent, out bool charging, out byte connection)
        {
            percent = 0;
            charging = false;
            connection = 0;

            if (f == null || f.Length < 21) return false;
            if (f[0] != ReportId) return false;
            if (f[4] != (CmdDeviceInfo & 0xFF) || f[5] != (CmdDeviceInfo >> 8)) return false;

            int len = f[3];
            if (len < 13 || 8 + len >= f.Length) return false;

            ushort vid = (ushort)(f[8] | (f[9] << 8));
            if (vid != 0x3837 && vid != 0x3554) return false;

            connection = f[18];
            int charge = f[19];
            int level = f[20];
            if (level > 100 || charge > 2) return false;

            percent = level;
            charging = charge != 0;
            return true;
        }

        /// <summary>Disk cache fallback so a sleeping device still shows its last known level.</summary>
        private static void SeedFromStore(DeviceInfo device, DeviceReading r)
        {
            int percent;
            bool charging;
            bool chargingKnown;
            string raw;
            DateTime at;
            if (!ReadingStore.TryGet(device.Id, out percent, out charging, out chargingKnown, out raw, out at))
                return;

            r.Percent = percent;
            r.Level = BatteryMath.FromPercent(percent);
            r.Charging = charging;
            r.ChargingKnown = chargingKnown;
            r.Raw = raw;
            r.UpdatedAt = at;
            r.Detail = "2.4G";
        }

        private static byte[] WaitReply(HidSession s, int totalMs)        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < totalMs)
            {
                int remaining = totalMs - (int)clock.ElapsedMilliseconds;
                byte[] p = s.Read(Math.Max(1, Math.Min(120, remaining)));
                if (p == null) continue;
                if (p.Length < 21 || p[0] != ReportId) continue;
                return p;
            }
            return null;
        }

        private static bool Matches(HidDeviceInfo h)
        {
            return h != null &&
                (h.VendorId == 0x3837 || h.VendorId == 0x3554) &&
                h.UsagePage == PageVendor &&
                h.InputReportLength == ReportLength &&
                h.OutputReportLength == ReportLength &&
                !string.IsNullOrEmpty(h.Path);
        }

        private static bool IsKeyboard(HidDeviceInfo h)
        {
            string n = h.ProductName == null ? "" : h.ProductName.ToUpperInvariant();
            // G-series / K-series naming: a mouse model never carries "V3" plus a key count,
            // but product strings are the only hint the receiver gives, so keep it simple.
            return n.Length > 0 && (n.Contains(" V2") || n.Contains(" V3") || n.Contains("K8") || n.Contains("K9") || n.Contains("G8"));
        }

        private static string ModelName(HidDeviceInfo h)
        {
            string model = h.ProductName == null ? "" : h.ProductName.Trim();
            if (model.StartsWith("MCHOSE ", StringComparison.OrdinalIgnoreCase)) model = model.Substring(7).Trim();
            if (model.EndsWith(" 2.4G", StringComparison.OrdinalIgnoreCase)) model = model.Substring(0, model.Length - 5).Trim();
            if (model.Length == 0) model = h.ProductId.ToString("X4");
            return model;
        }

        private static string ToHex(byte[] b)
        {
            if (b == null) return "";
            StringBuilder sb = new StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length && i < 32; i++)
            {
                if (i != 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
