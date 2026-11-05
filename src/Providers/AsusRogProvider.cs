using System;
using System.Collections.Generic;
using System.Diagnostics;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// ROG Omni (0B05:1ACE) exposes three vendor HID collections on interface 02.
    /// Report 01 lists the paired devices; reports 02 and 03 address them separately.
    /// See docs/rog-omni.md for the reply layouts and captured examples.
    /// </summary>
    public class AsusRogProvider : IDeviceProvider
    {
        private const ushort AsusVid = 0x0B05;
        private const ushort OmniReceiverPid = 0x1ACE;
        private const byte ReceiverReport = 0x01;
        private const byte KeyboardReport = 0x02;
        private const byte MouseReport = 0x03;
        private const int FrameSize = 64;
        private const int PairHeaderSize = 5;
        private const int PairEntrySize = 4;

        private readonly Func<HidDeviceInfo, IHidReportSession> _open;

        private sealed class OmniDevice : DeviceInfo
        {
            public HidDeviceInfo Channel;
            public byte ReportId;
            public DeviceReading LegacyReading;
        }

        public AsusRogProvider()
            : this(h => new HidSession(h.Path, h.InputReportLength, h.OutputReportLength))
        {
        }

        internal AsusRogProvider(Func<HidDeviceInfo, IHidReportSession> open)
        {
            _open = open;
        }

        public string Name { get { return "ROG HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            var receivers = new SortedDictionary<string, HidDeviceInfo[]>(StringComparer.Ordinal);
            foreach (HidDeviceInfo h in catalog)
            {
                string receiverId;
                byte reportId = IdentifyChannel(h, out receiverId);
                if (reportId == 0) continue;

                HidDeviceInfo[] channels;
                if (!receivers.TryGetValue(receiverId, out channels))
                    receivers.Add(receiverId, channels = new HidDeviceInfo[MouseReport + 1]);
                channels[reportId] = h;
            }

            var found = new List<DeviceInfo>();
            foreach (var receiver in receivers)
            {
                HidDeviceInfo[] channels = receiver.Value;
                byte[] reply = ReadPairList(channels[ReceiverReport]);
                if (reply == null)
                {
                    // Keep the old single-keyboard read path when the pairing
                    // channel is unavailable. Only a real reading establishes
                    // presence here; never assume the keyboard is paired.
                    if (channels[KeyboardReport] == null) continue;
                    OmniDevice legacy = CreateDevice(receiver.Key, KeyboardReport, 0, channels[KeyboardReport]);
                    legacy.LegacyReading = ReadBattery(legacy);
                    if (legacy.LegacyReading.Percent.HasValue) found.Add(legacy);
                    continue;
                }

                ushort[] products = ParsePairList(reply);
                if (products == null) continue;
                for (byte report = KeyboardReport; report <= MouseReport; report++)
                {
                    if (products[report] == 0) continue;
                    found.Add(CreateDevice(receiver.Key, report, products[report], channels[report]));
                }
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            OmniDevice omni = (OmniDevice)device;
            return omni.LegacyReading ?? ReadBattery(omni);
        }

        private static byte IdentifyChannel(HidDeviceInfo h, out string receiverId)
        {
            receiverId = null;
            if (h.VendorId != AsusVid || h.ProductId != OmniReceiverPid ||
                h.InputReportLength < FrameSize || h.OutputReportLength < FrameSize ||
                string.IsNullOrEmpty(h.Path)) return 0;

            string[] parts = h.Path.Split('#');
            if (parts.Length < 3) return 0;

            byte report;
            if (h.UsagePage == 0xFF02 && parts[1].EndsWith("&mi_02&col01", StringComparison.OrdinalIgnoreCase))
                report = ReceiverReport;
            else if (h.UsagePage == 0xFF00 && parts[1].EndsWith("&mi_02&col02", StringComparison.OrdinalIgnoreCase))
                report = KeyboardReport;
            else if (h.UsagePage == 0xFF01 && parts[1].EndsWith("&mi_02&col03", StringComparison.OrdinalIgnoreCase))
                report = MouseReport;
            else return 0;

            // Only the last component of the interface instance differs across
            // collections (e.g. &0000 / &0001 / &0002). Keep the instance prefix.
            int collection = parts[2].LastIndexOf('&');
            if (collection < 1) return 0;
            receiverId = "asus-omni-1ACE-" + parts[2].Substring(0, collection).ToUpperInvariant();
            return report;
        }

        private byte[] ReadPairList(HidDeviceInfo channel)
        {
            if (channel == null) return null;
            try
            {
                return Query(channel, ReceiverReport, 0xA0, 0x00, PairHeaderSize);
            }
            catch (InvalidOperationException)
            {
                // The HID handle can disappear between enumeration and open.
                return null;
            }
        }

        private static ushort[] ParsePairList(byte[] reply)
        {
            int count = reply[3];
            if (count > 2 || reply.Length < PairHeaderSize + count * PairEntrySize) return null;

            var products = new ushort[MouseReport + 1];
            for (int i = PairHeaderSize; i < PairHeaderSize + count * PairEntrySize; i += PairEntrySize)
            {
                ushort pid = (ushort)(reply[i] | (reply[i + 1] << 8));
                byte report = reply[i + 2];
                byte type = reply[i + 3];
                if (pid == 0 || !((report == KeyboardReport && type == 0x04) ||
                                  (report == MouseReport && type == 0x05))) continue;

                // Two different devices claiming one report id are ambiguous.
                if (products[report] != 0 && products[report] != pid) return null;
                products[report] = pid;
            }
            return products;
        }

        private OmniDevice CreateDevice(string receiverId, byte report, ushort pid, HidDeviceInfo channel)
        {
            DeviceKind kind = report == KeyboardReport ? DeviceKind.Keyboard : DeviceKind.Mouse;
            string model = ResolveModel(pid, kind);
            return new OmniDevice
            {
                Id = receiverId + "-" + report.ToString("X2") + "-" + pid.ToString("X4"),
                ReceiverId = receiverId,
                Brand = "ROG",
                Name = pid == 0x1B42 ? "ROG AZOTH EXTREME" :
                       pid == 0x1A94 ? "ROG HARPE ACE AIM LAB EDITION" : "ROG " + model,
                Model = model,
                ShortModel = pid == 0x1B42 ? "AZOTH" : model,
                Kind = kind,
                VendorId = AsusVid,
                ProductId = pid,
                HidPath = channel != null ? channel.Path : null,
                ProviderName = Name,
                Channel = channel,
                ReportId = report
            };
        }

        private DeviceReading ReadBattery(OmniDevice device)
        {
            var reading = new DeviceReading
            {
                UpdatedAt = DateTime.Now,
                Detail = "2.4G · Omni",
                Sleeping = true,
                ChargingKnown = false
            };

            if (device.Channel == null)
            {
                reading.Error = "未找到设备的 HID 通道";
                return reading;
            }

            bool keyboard = device.Kind == DeviceKind.Keyboard;
            byte command = keyboard ? (byte)0x01 : (byte)0x07;
            int percentIndex = keyboard ? 6 : 5;
            int chargeIndex = keyboard ? 9 : 10;
            int voltageIndex = keyboard ? 12 : 8;
            try
            {
                byte[] reply = Query(device.Channel, device.ReportId, 0x12, command, keyboard ? 14 : 11);
                if (reply == null)
                {
                    reading.Error = "设备未应答";
                    return reading;
                }

                reading.Raw = BitConverter.ToString(reply, 0, Math.Min(14, reply.Length));
                int percent = reply[percentIndex];
                bool charging = keyboard ? reply[chargeIndex] == 1 : reply[chargeIndex] > 0;
                if (percent > 100)
                {
                    reading.Error = "读数异常";
                    return reading;
                }

                // In standby the receiver can echo an empty status. A zero
                // with a measured voltage or while charging is a valid 0%.
                if (percent == 0 && !charging && reply[voltageIndex] == 0 && reply[voltageIndex + 1] == 0)
                {
                    reading.Error = "设备未上报电量";
                    return reading;
                }

                reading.Online = true;
                reading.Sleeping = false;
                reading.Percent = percent;
                reading.Level = BatteryMath.FromPercent(percent);
                reading.Charging = charging;
                reading.ChargingKnown = true;
                return reading;
            }
            catch (InvalidOperationException ex)
            {
                reading.Error = ex.Message;
                return reading;
            }
        }

        private byte[] Query(HidDeviceInfo channel, byte report, byte family, byte command, int minLength)
        {
            using (IHidReportSession session = _open(channel))
            {
                session.Flush(20);
                byte[] request = new byte[channel.OutputReportLength];
                request[0] = report;
                request[1] = family;
                request[2] = command;
                if (!session.Write(request, 500)) return null;

                // A collection can also deliver unsolicited or late reports.
                // Accept only a reply for the report id and command we sent.
                Stopwatch wait = Stopwatch.StartNew();
                int left;
                while ((left = 900 - (int)wait.ElapsedMilliseconds) > 0)
                {
                    byte[] reply = session.Read(left);
                    if (reply == null) return null;
                    if (reply.Length >= minLength && reply[0] == report &&
                        reply[1] == family && reply[2] == command) return reply;
                }
                return null;
            }
        }

        private static string ResolveModel(ushort pid, DeviceKind kind)
        {
            if (kind == DeviceKind.Keyboard)
            {
                switch (pid)
                {
                    case 0x1A85: return "AZOTH";
                    case 0x1B42: return "AZOTH EX";
                    case 0x1C25: return "AZOTH X";
                    case 0x1AB0: return "SCOPE II 96";
                    case 0x1B7A: return "SCOPE II 96 RX";
                    case 0x1B06: return "FALCHION RX LP";
                    default: return pid == 0 ? "键盘" : "键盘 " + pid.ToString("X4");
                }
            }

            switch (pid)
            {
                case 0x1A94: return "HARPE ACE";
                case 0x1B65: return "HARPE ACE MINI";
                case 0x1B68:
                case 0x1B69: return "HARPE ACE EX";
                case 0x1A68:
                case 0x1A6A: return "KERIS AIMPOINT";
                case 0x1B18:
                case 0x1B1A: return "KERIS II ACE";
                case 0x1C0E: return "KERIS II ORIGIN";
                case 0x1A72: return "GLADIUS III AP";
                case 0x1AD7: return "IMPACT III";
                default: return "鼠标 " + pid.ToString("X4");
            }
        }
    }
}
