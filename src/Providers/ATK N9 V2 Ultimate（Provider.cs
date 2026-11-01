using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// ATK N9V2 ULTIMATE (海王星 N9 V2 Ultimate) 2.4G headset.
    ///
    /// Dongle: VID 0x373B, PID 0x1262, product string "ATK N9V2 ULTIMATE dongle",
    /// manufacturer "Actions". It is a composite device: MI_00 is a HID interface with
    /// four top-level collections (0x000C consumer, 0x000B, 0xFF90 64/64, 0xFF82 64/64)
    /// and MI_01 is audio, owned by the vendor driver FlowAudioDevice_n9sultimate.
    /// The battery is answered by the 0xFF90 vendor collection: input report length 64,
    /// output report length 64.
    ///
    /// The headset speaks a Bolutek-style vendor protocol on HID report id 0x55. The
    /// frames below were recovered from ATK HUB's own renderer bundle
    /// (resources\main\app.asar -> static/demoMode.store-*.js, the class whose
    /// debug tag is "bolutek": makePacket() allocates Uint8Array(63), data[0] = command,
    /// data[1] = target, and every command is pushed out with device.sendReport(0x55, data))
    /// and then verified against the real dongle on 2026-11-01.
    ///
    /// Two things about the transport are easy to get wrong:
    ///
    ///   1) The command channel is the interrupt OUT pipe (WriteFile), NOT HID SET_REPORT.
    ///      HidD_SetOutputReport returns FALSE / error 1 (ERROR_INVALID_FUNCTION) on this
    ///      collection, which previously made the whole vendor collection look read-only.
    ///
    ///   2) The writable report id is 0x55. A scan of report ids 0x00..0x3F therefore finds
    ///      nothing; 0x55 = 85 is just outside that range. Writing any other report id to
    ///      this collection fails with error 1. The 0xFF82 collection rejects 0x55 too, so
    ///      the query has to go to the 0xFF90 handle.
    ///
    /// Protocol (verified capture, Windows buffer INCLUDES the report id byte):
    ///
    ///   query : 64-byte frame, sent with WriteFile on the 0xFF90 handle.
    ///
    ///     query[0]      = 0x55  vendor report id
    ///     query[1]      = 0x65  command: GetBattery / GetDeviceInfo
    ///     query[2]      = 0x01  target: 0x01 = headset
    ///                           (0x00 = dongle is accepted by the transport but is
    ///                            answered only for command 0xAA, never for 0x65)
    ///     query[3..63]  = 0x00
    ///
    ///   reply : 64-byte input report read back from the same handle with ReadFile
    ///           (overlapped). Verified capture at 100%, matching ATK HUB's own UI:
    ///
    ///     55 65 64 02 02 00 00 04 00 00 00 00 01 02 01 00 05 02 00 ...
    ///     ^^ ^^ ^^ ^^
    ///     |  |  |  +-- charge state: 0x02 discharging, 0x03 charging,
    ///     |  |  |                     0x04 full, 0x1A unknown
    ///     |  |  +----- percent (0x64 = 100)
    ///     |  +-------- command echo (0x65)
    ///     +----------- report id (0x55)
    ///
    ///   Remaining fields, for reference, are not used here:
    ///     reply[4] eqMode, reply[5] surround71, reply[6] spatialSound,
    ///     reply[7] autoPowerOff, reply[8] sidetoneOn, reply[9] sidetoneVolume,
    ///     reply[10..13] dongle version, reply[14..17] headset version, reply[18] rgbOn.
    ///
    ///   Command 0xAA (GetVersion) answers on both targets and returns the same layout
    ///   shape; 55 AA 01 ... = headset, 55 AA 00 ... = dongle. It is not used for battery.
    ///
    /// The dongle answers while ATK HUB and atk-hub-service are running, so there is no
    /// handle contention to work around: HID handles are opened FILE_SHARE_READ|WRITE.
    /// </summary>
    public class AtkHeadsetProvider : IDeviceProvider
    {
        private const ushort AtkVid = 0x373B;
        private const ushort N9V2UltimatePid = 0x1262;
        private const ushort VendorPage = 0xFF90;

        private const int FrameSize = 64;
        private const byte ReportId = 0x55;
        private const byte CmdDeviceInfo = 0x65;
        private const byte TargetHeadset = 0x01;

        private const int PercentIndex = 2;
        private const int ChargeStateIndex = 3;

        private const byte ChargeDischarging = 0x02;
        private const byte ChargeCharging = 0x03;
        private const byte ChargeFull = 0x04;

        public string Name { get { return "ATK HID"; } }

        public List<DeviceInfo> Discover()
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            List<HidDeviceInfo> all = HidNative.Enumerate();

            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();
            foreach (HidDeviceInfo h in all)
            {
                if (h.VendorId != AtkVid) continue;
                if (h.ProductId != N9V2UltimatePid) continue;
                if (h.UsagePage != VendorPage) continue;     // 0xFF90, not the 0xFF82 twin
                if (h.InputReportLength != FrameSize) continue;
                if (h.OutputReportLength != FrameSize) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;

                DeviceInfo d = new DeviceInfo();
                d.Id = "atk-" + kv.Key;
                d.Brand = "ATK";
                d.Model = "N9V2 ULTIMATE";
                d.Name = "ATK N9V2 ULTIMATE";
                d.ShortModel = "N9V2 ULT";
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

            // How the dongle is attached feeds the card's connection line. The HID
            // path is the only transport evidence a provider gets; the rules (and
            // their limits) are documented in ConnectionProbe.
            if (device != null)
            {
                r.Connection = ConnectionProbe.ForDevice(
                    device.HidPath, device.VendorId, device.ProductId);
            }

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

                        // The first attempt gets the full budget; the retries are short
                        // because a headset that is awake answers in a few milliseconds.
                        int budget = attempt == 0 ? 1200 : 600;

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
                        r.Error = "设备休眠";
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

                    byte state = resp[ChargeStateIndex];

                    // The firmware reports a distinct "full" state. Normalise it to the
                    // two fields the UI understands: 100% and not charging.
                    if (state == ChargeFull) level = 100;

                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    r.Charging = state == ChargeCharging;
                    r.Online = true;
                    r.Detail = "2.4G";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        /// <summary>
        /// Builds the 64-byte battery query: report id 0x55, command 0x65, target headset,
        /// zero-filled body. No checksum - this firmware does not use one, unlike the
        /// 0xFF02 mouse dongle.
        /// </summary>
        private static byte[] NewQuery()
        {
            byte[] cmd = new byte[FrameSize];
            cmd[0] = ReportId;
            cmd[1] = CmdDeviceInfo;
            cmd[2] = TargetHeadset;
            return cmd;
        }

        /// <summary>
        /// Reads until a frame that is really an answer to 0x65 shows up or the budget
        /// runs out. Every other report is ignored: the headset also pushes state frames
        /// on its own (report[1] = 0x0A means "headset disconnected / sleeping").
        /// </summary>
        private static byte[] WaitFor(HidSession s, int budgetMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(budgetMs);
            while (DateTime.Now < deadline)
            {
                byte[] p = s.Read(150);
                if (p == null) continue;
                if (p.Length >= 4 && p[0] == ReportId && p[1] == CmdDeviceInfo) return p;
            }
            return null;
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
