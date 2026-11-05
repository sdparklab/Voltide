using System;

namespace Voltide.Core
{
    /// <summary>Coarse battery grade, used when a device only reports steps.</summary>
    public enum BatteryLevel
    {
        Unknown = 0,
        Critical = 1,
        Low = 2,
        Medium = 3,
        High = 4,
        Full = 5
    }

    public enum DeviceKind
    {
        Unknown = 0,
        Mouse = 1,
        Keyboard = 2,
        Headset = 3,
        Other = 4
    }

    public class DeviceInfo
    {
        public string Id;
        public string ReceiverId;    // shared receiver identity for an explicitly paired child
        public string Name;
        public string Brand;
        public string Model;
        public string ShortModel;    // tray menu: same device, less width to spend
        public DeviceKind Kind;
        public ushort VendorId;
        public ushort ProductId;
        public string HidPath;
        public string ProviderName;
        public bool IsCharging;
    }

    public class DeviceReading
    {
        public int? Percent;          // null = unknown
        public BatteryLevel Level;
        public bool Charging;
        public bool ChargingKnown = true;   // false when the reply could not confirm it
        public bool Online;
        /// <summary>
        /// The dongle is present but the device did not answer (it sleeps between
        /// keystrokes / mouse moves). The card stays on screen with its last value.
        /// </summary>
        public bool Sleeping;
        public DateTime UpdatedAt;
        public string Error;
        public string Detail;         // extra line for the card, e.g. "2.4G"
        public string Raw;            // raw hex reply, for diagnostics
    }

    public static class BatteryMath
    {
        public static BatteryLevel FromPercent(int p)
        {
            if (p >= 90) return BatteryLevel.Full;
            if (p >= 60) return BatteryLevel.High;
            if (p >= 30) return BatteryLevel.Medium;
            if (p >= 15) return BatteryLevel.Low;
            return BatteryLevel.Critical;
        }
    }
}
