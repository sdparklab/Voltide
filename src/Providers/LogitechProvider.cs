using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Logitech Lightspeed / Unifying receivers speaking HID++ 2.0.
    ///
    /// Two things are not fixed in the protocol and have to be discovered:
    ///   1. which device index the mouse is paired to (1..6) — the G Pro X
    ///      Superlight 2 sits on index 2, a G304 sits on index 1;
    ///   2. how the firmware reports charge. Older mice expose 0x1000
    ///      BATTERY_LEVEL_STATUS (a percentage), newer ones only expose
    ///      0x1004 BATTERY_VOLTAGE (millivolts).
    /// </summary>
    public class LogitechProvider : IDeviceProvider
    {
        private const ushort LogitechVid = 0x046D;
        private const ushort FeatureBatteryLevelStatus = 0x1000;   // level, function 0
        private const ushort FeatureBatteryVoltage = 0x1004;       // level, function 1

        private const byte ReportLong = 0x11;
        private const byte FuncRootGetFeature = 0x00;
        private const byte FuncRootPing = 0x01;          // IRoot.getProtocolVersion
        private const byte FuncBatteryGet = 0x00;        // 0x1000 function 0 = level %
        private const byte FuncBatteryLevel = 0x01;      // 0x1004 function 1 = level %

        /// <summary>
        /// HID++ 2.0 packs byte 3 as `(function &lt;&lt; 4) | swId`, i.e. the function id
        /// is the HIGH nibble and the software id is the LOW nibble. Captured on the
        /// wire for the GPW3: `11 01 06 1E ...` = feature 0x06, function 1, swId 0xE.
        /// 0x0D is what G HUB uses, so we stay on a different id.
        /// </summary>
        private const byte SwId = 0x0E;

        /// <summary>Device index per receiver path, discovered once by ping.</summary>
        private static readonly Dictionary<string, byte> _indexCache = new Dictionary<string, byte>();

        public string Name { get { return "Logitech HID++"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            // No passive listener any more: 0x1004 function 1 answers with the real
            // percentage, so there is nothing left to eavesdrop on. Keeping the
            // listener would hold a second HID handle open for no reason.

            List<DeviceInfo> found = new List<DeviceInfo>();
            
            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();
            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != LogitechVid) continue;
                if (h.UsagePage != 0xFF00) continue;
                if (h.Usage != 0x0002) continue;          // long report collection
                if (h.OutputReportLength != 20) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;
                string model = ResolveLogitechModel(h);

                DeviceInfo d = new DeviceInfo();
                d.Id = "logi-" + kv.Key;
                d.Brand = "Logitech";
                d.Model = model;
                d.Name = "Logitech " + model;
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo d)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;

            try
            {
                using (HidSession s = new HidSession(d.HidPath, 20, 20))
                {
                    s.Flush(20);

                    byte dev = ResolveDeviceIndex(s, d.HidPath);
                    if (dev == 0)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }


                    // ---- 0x1000: the firmware reports the level directly ----
                    // Present on the G304 and other older receivers. Preferred when
                    // available: the mouse's own gauge beats any curve we could derive.
                    bool responded;
                    byte feat = GetFeature(s, dev, FeatureBatteryLevelStatus, out responded);

                    // No reply at all after two tries = the mouse is asleep. A reply
                    // that simply says "feature 0" is a different thing entirely.
                    if (feat == 0 && !responded)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }

                    byte batFunc = FuncBatteryGet;

                    if (feat == 0)
                    {
                        // ---- 0x1004: function 1 IS the level on this firmware ----
                        // Verified on the GPW3 at USB level:
                        //   TX 11 01 06 1E 00 00 00
                        //   RX 11 01 06 1E 61 08 00   -> 0x61 = 97 %
                        //
                        // Function 0 of the same feature is a voltage field that
                        // answers with a constant 3855 mV "charging" frame. The
                        // receiver re-broadcasts every reply to all open handles, and
                        // G HUB adopts that constant as its own state, flipping its
                        // gauge to 15% until the mouse is re-enumerated. So function 0
                        // is never requested here, and there is no voltage fallback.
                        feat = GetFeature(s, dev, FeatureBatteryVoltage, out responded);
                        batFunc = FuncBatteryLevel;

                        if (feat == 0 && !responded)
                        {
                            r.Sleeping = true;
                            r.Error = "设备休眠";
                            return r;
                        }

                        if (feat == 0)
                        {
                            r.Online = true;
                            r.Percent = null;
                            r.Error = "该设备不上报电量";
                            return r;
                        }
                    }

                    byte[] bat = Call(s, dev, feat, batFunc);
                    if (bat == null) { r.Sleeping = true; r.Error = "设备休眠"; return r; }
                    if (bat[3] == 0xFF)
                    {
                        r.Error = "HID++ 错误码 0x" + bat[5].ToString("X2");
                        return r;
                    }

                    int level = bat[4];

                    // A level byte outside 0..100 means the reply was not what we
                    // asked for (wrong feature or stale packet). Report it as unknown
                    // instead of clamping a voltage high byte into a plausible number.
                    if (level > 100)
                    {
                        r.Raw = Hex(bat);
                        r.Online = true;
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    r.Raw = Hex(bat);
                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    // byte 5 means different things per feature:
                    //   0x1000  bit field - bit0 = charging, bit1 = external power
                    //   0x1004  enum      - 0 = discharging, 1 = charging, 2 = full
                    // Testing bit 0 is correct for both, and stops a battery-powered
                    // G304 (bit1 set) from reading as "charging".
                    r.Charging = (bat[5] & 0x01) != 0;
                    r.Detail = "HID++ 2.0";
                    r.Online = true;
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        // ------------------------------------------------------------ HID++

        /// <summary>Pings device slots until one answers, then remembers it.</summary>
        private static byte ResolveDeviceIndex(HidSession s, string path)
        {
            // A known slot is trusted without a ping. These receivers stop answering
            // for a while when they get hammered, and every request saved here is one
            // the device does not have to service.
            byte cached;
            if (_indexCache.TryGetValue(path, out cached)) return cached;

            long budget = Environment.TickCount + ScanBudget;
            for (byte i = 1; i <= MaxScanIndex; i++)
            {
                int left = (int)(budget - Environment.TickCount);
                if (left <= 150) break;

                if (Ping(s, i, Math.Min(WakeDeadline, left)))
                {
                    _indexCache[path] = i;
                    return i;
                }
            }

            return 0;
        }

        /// <summary>
        /// A mouse that has been idle for a while stops answering HID++ entirely —
        /// the request is what usually wakes it, which takes ~1.2 s, but a deeply
        /// asleep device never answers at all. Only the first four slots are scanned:
        /// every consumer receiver pairs at 1 or 2, and scanning six of them would
        /// cost a full second each on every poll.
        /// </summary>
        private const int WakeDeadline = 1100;
        private const byte MaxScanIndex = 4;
        private const int ScanBudget = 3600;

        private static bool Ping(HidSession s, byte dev, int deadlineMs)
        {
            byte[] req = NewRequest(dev, 0x00, FuncRootPing);
            if (!s.Write(req, 300)) return false;
            return WaitFor(s, dev, 0x00, req[3], deadlineMs) != null;
        }

        private static byte GetFeature(HidSession s, byte dev, ushort feature, out bool responded)
        {
            responded = false;

            // Two attempts: a mouse coming out of radio sleep regularly eats the
            // first request (the receiver forwards it before the mouse is listening),
            // which used to show up as a phantom "asleep" that cleared on its own.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte[] req = NewRequest(dev, 0x00, FuncRootGetFeature);
                req[4] = (byte)(feature >> 8);
                req[5] = (byte)(feature & 0xFF);
                if (!s.Write(req, 300))
                {
                    System.Threading.Thread.Sleep(120);
                    continue;
                }

                byte[] resp = WaitFor(s, dev, 0x00, req[3], attempt == 0 ? 350 : 500);
                if (resp != null)
                {
                    // The device DID answer. Feature index 0 means "no such feature",
                    // which is a fact about the device, not a sign of sleep.
                    responded = true;
                    return resp[4];
                }

                System.Threading.Thread.Sleep(250);
            }

            return 0;
        }

        /// <summary>
        /// Calls a feature function. No retry: the device already answered getFeature,
        /// so it is awake. Retrying here only made the receiver stop responding.
        /// </summary>
        private static byte[] Call(HidSession s, byte dev, byte featureIndex, byte function)
        {
            byte[] req = NewRequest(dev, featureIndex, function);
            if (!s.Write(req, 400)) return null;
            return WaitFor(s, dev, featureIndex, req[3], 600);
        }

        private static byte[] NewRequest(byte dev, byte featureIndex, byte function)
        {
            byte[] req = new byte[20];
            req[0] = ReportLong;
            req[1] = dev;
            req[2] = featureIndex;
            // function in the high nibble, software id in the low nibble
            req[3] = (byte)(((function & 0x0F) << 4) | (SwId & 0x0F));
            return req;
        }

        /// <summary>
        /// Reads in short slices until the matching reply lands or the deadline passes.
        ///
        /// Everything must match: device, feature index and the full function/swId
        /// byte. Another handle on the same collection gets a copy of every packet,
        /// so a loose match fed 1026 mV into the battery maths, and a shared swId
        /// let G HUB adopt our replies as its own.
        /// </summary>
        private static byte[] WaitFor(HidSession s, byte dev, byte feat, byte functionByte, int deadlineMs)
        {
            long until = Environment.TickCount + deadlineMs;

            while (true)
            {
                int left = (int)(until - Environment.TickCount);
                if (left <= 0) return null;

                byte[] p = s.Read(Math.Min(60, left));
                if (p == null)
                {
                    System.Threading.Thread.Sleep(2);
                    continue;
                }

                if (p.Length < 7) continue;
                if (p[1] != dev || p[2] != feat) continue;
                if (p[3] != functionByte) continue;
                if (p[3] == 0xFF) continue;      // error reply, let the caller time out

                return p;
            }
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

        private static string ResolveLogitechModel(HidDeviceInfo h)
        {
            // Short names: the card's model line is 13 px in the artboard and a full
            // marketing name ("G PRO X SUPERLIGHT 2") would run into the lightning.
            switch (h.ProductId)
            {
                case 0xC53F: return "G304";
                case 0xC547: return "GPW3";      // Lightspeed receiver: G Pro X Superlight 2
                case 0xC54D: return "GPW3";      // spare Lightspeed receiver for the same mouse
                case 0xC09B: return "GPW3";      // the same mouse, wired (appears when the cable is in)
                case 0xC548: return "M750M";     // Logi Bolt receiver
                case 0xC539: return "G102";
                case 0xC52B: return "Unifying";
                default:
                    // Fallback label: any Logitech PID we have not mapped yet.
                    // Logitech ships far more pointing devices than keyboards, and a
                    // generic receiver names itself "USB Receiver", so the name is the
                    // only hint we get.
                    string product = h.ProductName ?? "";
                    return product.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Keyboard"
                        : "Mouse";
            }
        }
    }
}
