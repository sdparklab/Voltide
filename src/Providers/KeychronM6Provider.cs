using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Keychron M6 1K (M6-A3): VID 3434, mouse PID D03F, Link receivers
    /// D030/D031. Classic protocol on usage page 008C, usage 1.
    /// SET_FEATURE / GET_FEATURE report 51, 21 bytes (including report ID):
    ///   03: paired VID at [3..4], PID at [5..6], little endian (cached).
    ///   06: VID [4..5], PID [6..7], [10]&08 = receiver linked,
    ///       [11] = battery percentage, [12] = charging (nonzero).
    /// A receiver can return the previous command's buffer before updating it;
    /// poll for the matching command and validate identity before decoding.
    /// References: ernestnurma/engine-for-keychron, ClassicCommands/Parsers.
    /// Live M6-A3 + D030 reply: 51 06 01 00 34 34 3F D0 06 01 09 0F 00.
    /// Only identity/status queries are sent; no settings or firmware writes.
    /// </summary>
    public sealed class KeychronM6Provider : IDeviceProvider
    {
        private const ushort Vid = 0x3434;
        private const ushort MousePid = 0xD03F;
        private const int ReportLength = 21;
        public string Name { get { return "Keychron M6"; } }

        private static bool IsReceiver(ushort pid)
        {
            return pid == 0xD030 || pid == 0xD031;
        }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != Vid || (h.ProductId != MousePid && !IsReceiver(h.ProductId))) continue;
                if (h.UsagePage != 0x008C || h.Usage != 1 || h.FeatureReportLength < ReportLength) continue;
                if (string.IsNullOrEmpty(h.Path) || !seen.Add(h.Path)) continue;
                if (IsReceiver(h.ProductId))
                {
                    // Link receivers are shared by several M-series models.
                    // Never label an arbitrary Keychron receiver as an M6.
                    try
                    {
                        using (HidSession s = new HidSession(h.Path, h.InputReportLength, h.OutputReportLength))
                        {
                            byte[] paired = Query(s, 0x03);
                            if (!IsM6Pair(paired)) continue;
                        }
                    }
                    catch { continue; }
                }
                found.Add(new DeviceInfo {
                    Id = "keychron-m6-" + h.Path, Name = "Keychron M6", Brand = "Keychron",
                    Model = "M6 1K", ShortModel = "M6", Kind = DeviceKind.Mouse,
                    VendorId = h.VendorId, ProductId = h.ProductId,
                    HidPath = h.Path, ProviderName = Name
                });
            }
            return found;
        }

        internal static bool IsM6Pair(byte[] r)
        {
            return r != null && r.Length >= 7 && r[0] == 0x51 && r[1] == 0x03 &&
                r[3] == 0x34 && r[4] == 0x34 && r[5] == 0x3F && r[6] == 0xD0;
        }

        public DeviceReading Read(DeviceInfo d)
        {
            bool receiver = IsReceiver(d.ProductId);
            try
            {
                // Classic configuration collection has 33-byte input reports.
                using (HidSession s = new HidSession(d.HidPath, 33, 33))
                    return DecodeStatus(Query(s, 0x06), receiver);
            }
            catch (Exception ex)
            {
                DeviceReading r = DecodeStatus(null, receiver);
                r.Error = ex.Message;
                return r;
            }
        }

        internal static DeviceReading DecodeStatus(byte[] data, bool receiver)
        {
            DeviceReading r = new DeviceReading {
                UpdatedAt = DateTime.Now, Level = BatteryLevel.Unknown,
                Detail = receiver ? "2.4G" : "USB", ChargingKnown = false,
                Raw = data == null ? "" : BitConverter.ToString(data).Replace('-', ' ')
            };
            if (data == null || data.Length < 13 || data[0] != 0x51 || data[1] != 0x06)
            {
                r.Error = "Keychron status query failed";
                return r;
            }
            if (receiver && (data[10] & 0x08) == 0)
            {
                r.Sleeping = true;
                r.Error = "Mouse sleeping or disconnected";
                return r;
            }
            if (data[4] != 0x34 || data[5] != 0x34 || data[6] != 0x3F || data[7] != 0xD0)
            {
                r.Error = "Unexpected mouse identity";
                return r;
            }
            if (data[11] > 100)
            {
                r.Error = "Invalid battery percentage";
                return r;
            }
            r.Percent = data[11];
            r.Level = BatteryMath.FromPercent(data[11]);
            r.Charging = data[12] != 0;
            r.ChargingKnown = true;
            r.Online = true;
            return r;
        }

        private static byte[] Query(HidSession s, byte command)
        {
            // Leave the receiver time to finish the preceding transaction,
            // including when Discover and Read use separate handles.
            Thread.Sleep(200);
            byte[] request = new byte[ReportLength];
            request[0] = 0x51;
            request[1] = command;
            if (!HidNative.HidD_SetFeature(s.Handle, request, request.Length))
                throw new InvalidOperationException("Keychron SET_FEATURE failed");
            Thread.Sleep(200);
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                Thread.Sleep(50);
                byte[] reply = new byte[ReportLength];
                reply[0] = 0x51;
                if (HidNative.HidD_GetFeature(s.Handle, reply, reply.Length) &&
                    reply[0] == 0x51 && reply[1] == command) return reply;
            } while (timer.ElapsedMilliseconds < 1000);
            throw new TimeoutException("Keychron status reply timed out");
        }
    }
}
