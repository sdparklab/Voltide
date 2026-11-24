using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// Xbox-compatible gamepads, read through XInput instead of a HID frame.
    ///
    /// A pad in Xbox mode does not expose a vendor HID collection at all: Windows
    /// binds it to the Xbox Gaming Device driver and the only public way in is
    /// XInput. There is no report to build and no dongle to wake, so this adapter
    /// looks nothing like the mouse ones — Discover() ignores the HID catalog and
    /// walks the four XInput slots instead.
    ///
    /// The catch is resolution. XInputGetBatteryInformation returns a step, not a
    /// percentage:
    ///
    ///     BatteryType  0 disconnected, 1 wired, 2 alkaline, 3 NiMH
    ///     BatteryLevel 0 empty, 1 low, 2 medium, 3 full
    ///
    /// The rest of this app is built around a percentage (card, liquid, tray row),
    /// so each step is shown as the middle of its band and Detail() says so:
    ///
    ///     full -> 100, medium -> 50, low -> 20, empty -> 5
    ///
    /// Treat the number as a four-notch gauge, not a measurement. A wired pad
    /// reports type 1 and is shown as 100% charging, which is what it is.
    ///
    /// Bluetooth LE is the exception, and the reason this file knows about GATT. A
    /// pad paired over BLE reaches Windows through HOGP, and XInput then answers
    /// BATTERY_TYPE_DISCONNECTED (type 0) no matter what the cell holds - the old
    /// fallback turned that into a fabricated 5%. The real reading lives in the
    /// GATT Battery Service (0000180F / 00002A19), so XboxBleBattery reads it and
    /// the percentage below becomes a measurement instead of a notch. Charging
    /// stays unknown on that path: the HID descriptor Windows builds for a BLE pad
    /// carries no battery field, so the flag has no source. A pad on the cable is
    /// still type 1 and keeps the wired treatment.
    ///
    /// Verified on 8BitDo Ultimate 3E Xbox (VID 2DC8 / PID 202F, 2.4G): type 3,
    /// level 2 while the cell was half charged. Verified on Xbox Series X pad over
    /// BLE: type 0 level 0 while GATT reported 73% then 68%.
    /// </summary>
    public class XInputProvider : IDeviceProvider
    {
        private const int MaxSlots = 4;

        public string Name { get { return "XInput"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();

            for (int slot = 0; slot < MaxSlots; slot++)
            {
                if (!XInputNative.Connected(slot)) continue;

                DeviceInfo d = new DeviceInfo();
                d.Id = "xinput-" + slot;
                d.Brand = "XBOX";
                d.Model = "CONTROLLER";
                d.ShortModel = "XBOX " + (slot + 1);
                d.Name = "Xbox 手柄";
                d.Kind = DeviceKind.Other;

                // No HID handle exists for this device; the slot number is the
                // address. Encoded here so Read() stays stateless.
                d.HidPath = "@xinput:" + slot;
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

            int slot = SlotOf(device);
            if (slot < 0)
            {
                r.Error = "手柄编号无效";
                return r;
            }

            if (!XInputNative.Connected(slot))
            {
                r.Sleeping = true;
                r.Error = "设备休眠";
                return r;
            }

            XInputNative.BatteryInfo info;
            if (!XInputNative.Battery(slot, out info))
            {
                r.Online = true;
                r.Error = "读数失败";
                return r;
            }

            r.Online = true;

            if (info.BatteryType == 1)
            {
                // Wired: the pad runs off the cable. XInput has no charge flag,
                // and a cable is the closest thing to one.
                r.Percent = 100;
                r.Level = BatteryLevel.Full;
                r.Charging = true;
                r.Detail = "XInput 有线";
                r.Raw = "type=1 level=" + info.BatteryLevel;
                return r;
            }

            if (info.BatteryType == 0)
            {
                // A BLE pad reaches Windows through HOGP, so XInput cannot see its
                // cell at all (type 0). The GATT Battery Service knows the real
                // percentage; the four-notch gauge never applied to this transport,
                // and the old fallback turned type 0 into a fabricated 5%.
                int? ble = null;
                string bleRaw = null;
#if HAVE_BLE_GATT
                ble = XboxBleBattery.Read(out bleRaw);
#endif
                if (ble.HasValue)
                {
#if HAVE_BLE_GATT
                    if (XboxBleBattery.ChargingGuess)
                    {
                        // A pad on a wall charger keeps Bluetooth up but reports a flat
                        // 100% for the whole charge (measured 68% -> 100% within
                        // minutes), so the number is a charging indicator, not a
                        // measurement. Leaving Percent unset keeps the last real level
                        // on the card, greyed out, under 电池充电中 instead of Full.
                        r.Charging = true;
                        r.Detail = "蓝牙 · 充电中";
                        r.Raw = "type=0 level=" + info.BatteryLevel + " gatt=" + bleRaw + " guess=charging";
                        return r;
                    }
#endif
                    r.Percent = ble;
                    r.Level = BatteryMath.FromPercent(ble.Value);
                    r.Charging = false;
                    r.ChargingKnown = false;
                    r.Detail = "蓝牙 · 系统电量";
                    r.Raw = "type=0 level=" + info.BatteryLevel + " gatt=" + bleRaw;
                    return r;
                }

                // No answer from the battery service: say so instead of inventing a
                // number. The card greys out and keeps the last level it had.
                r.Error = "蓝牙电量不可读";
                r.Detail = "蓝牙";
                r.Raw = "type=0 level=" + info.BatteryLevel;
                return r;
            }

            int? percent = PercentFor(info.BatteryLevel);
            if (!percent.HasValue)
            {
                r.Error = "读数异常";
                r.Raw = "type=" + info.BatteryType + " level=" + info.BatteryLevel;
                return r;
            }

            r.Percent = percent;
            r.Level = BatteryMath.FromPercent(percent.Value);
            r.Detail = "XInput 四档";
            r.Raw = "type=" + info.BatteryType + " level=" + info.BatteryLevel;
            return r;
        }

        private static int SlotOf(DeviceInfo device)
        {
            string path = device.HidPath;
            if (path == null) return -1;
            const string prefix = "@xinput:";
            if (path.Length <= prefix.Length) return -1;
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return -1;

            int slot;
            if (!int.TryParse(path.Substring(prefix.Length), out slot)) return -1;
            if (slot < 0 || slot >= MaxSlots) return -1;
            return slot;
        }

        /// <summary>
        /// Middle of each band. XInput refuses to be more precise than this, and
        /// rounding up would make a half-full pad read as nearly full.
        /// </summary>
        private static int? PercentFor(byte level)
        {
            if (level == 3) return 100;
            if (level == 2) return 50;
            if (level == 1) return 20;
            if (level == 0) return 5;
            return null;
        }
    }

    /// <summary>
    /// XInput1_4 ships with Windows 10+; 1_3 covers 8 and 8.1. Both are probed at
    /// call time rather than at load, so a machine without either simply reports
    /// no gamepads instead of failing to start the app.
    /// </summary>
    internal static class XInputNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BatteryInfo
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("XInput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState14(uint index, byte[] state);

        [DllImport("XInput1_3.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState13(uint index, byte[] state);

        [DllImport("XInput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern uint GetBattery14(uint index, byte devType, out BatteryInfo info);

        [DllImport("XInput1_3.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern uint GetBattery13(uint index, byte devType, out BatteryInfo info);

        internal static bool Connected(int slot)
        {
            byte[] state = new byte[16];
            try
            {
                if (GetState14((uint)slot, state) == 0) return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            try
            {
                if (GetState13((uint)slot, state) == 0) return true;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            return false;
        }

        internal static bool Battery(int slot, out BatteryInfo info)
        {
            info = new BatteryInfo();
            BatteryInfo scratch;

            try
            {
                if (GetBattery14((uint)slot, 0, out scratch) == 0)
                {
                    info = scratch;
                    return true;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            try
            {
                if (GetBattery13((uint)slot, 0, out scratch) == 0)
                {
                    info = scratch;
                    return true;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            return false;
        }
    }
}
