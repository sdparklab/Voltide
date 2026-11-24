#if HAVE_BLE_GATT
namespace Voltide.Providers
{
    /// <summary>
    /// Battery percentage for an Xbox pad that Windows has bound over Bluetooth LE.
    ///
    /// Why this file exists: a pad paired over BLE goes through the HOGP path
    /// (mshidumdf -> "HID-compliant game controller"), and XInput answers
    /// BATTERY_TYPE_DISCONNECTED (type 0) for it - no percentage, no charging flag.
    /// The only real number Windows exposes is the GATT Battery Service
    /// (0000180F / 00002A19), which is also where the Settings app gets its value.
    ///
    /// What still cannot be read: the charging flag. The Linux xpadneo driver takes
    /// it from a one-byte "battery strength" field (usage page 0x06 / usage 0x20)
    /// that the Windows HID descriptor for this pad does not contain, and the GATT
    /// battery characteristic carries only the percentage. A pad on the USB cable
    /// still shows 100% charging because XInput then reports BATTERY_TYPE_WIRED.
    ///
    /// The catch that matters here: a pad charging from a wall charger keeps the
    /// Bluetooth link up and then reports a flat 0x64 (100%) through 0x2A19 for the
    /// whole charge - measured 68% before the charger went in, 100% within minutes
    /// after. The firmware treats the reading as "charging", so the number is not a
    /// measurement while the charger is connected. Nothing in the descriptor or the
    /// vendor GATT service (00000001-5f60-..., 32-byte blob unchanged between
    /// charging and idle) marks it, so this file has to infer it: a jump to 100 from
    /// a materially lower level inside a short window is the charger, not physics.
    /// ChargingGuess exposes that inference, and the caller shows the last real level
    /// instead of the inflated 100%.
    ///
    /// Cost control: discovery (one DeviceInformation pass plus a GATT service walk)
    /// runs at most once a minute, a normal refresh is a single GATT read of a few
    /// milliseconds, and every WinRT wait is bounded so a sleeping pad cannot block
    /// the refresh thread.
    ///
    /// Verified 2026-11-24 on an Xbox Series X pad (BLE, firmware 5.23.6.0): GATT
    /// reported 0x49 then 0x44 (73%, 68%) while XInput kept saying type 0 level 0.
    /// </summary>
    internal static class XboxBleBattery
    {
        private const int WaitMs = 1500;
        private const int RescanMs = 60000;
        private const int RetryMs = 5000;

        // A cell cannot climb this far this fast, so a 100% reading this soon after
        // a lower one is the charger talking, not the battery.
        private const int JumpWindowMs = 900000;
        private const int JumpFromMax = 95;
        private const int JumpToMin = 100;
        private const string BatteryServiceUuid = "0000180f";
        private const string BatteryLevelUuid = "00002a19";

        private static readonly object Sync = new object();
        private static Windows.Devices.Bluetooth.BluetoothLEDevice _device;
        private static Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService _service;
        private static Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic _level;
        private static System.DateTime _rescanAt = System.DateTime.MinValue;
        private static bool _unavailable;
        private static int? _lastPercent;
        private static System.DateTime _lastPercentAt = System.DateTime.MinValue;
        private static bool _chargingGuess;

        /// <summary>
        /// Current percentage, or null when the pad is not reachable this refresh.
        /// raw receives the GATT reply for the diagnostics line.
        /// </summary>
        internal static int? Read(out string raw)
        {
            raw = null;
            lock (Sync)
            {
                if (_unavailable) return null;

                System.DateTime now = System.DateTime.UtcNow;
                if (_level == null && now < _rescanAt) return null;

                try
                {
                    if (_level == null)
                    {
                        if (!Attach())
                        {
                            _rescanAt = System.DateTime.UtcNow.AddMilliseconds(RetryMs);
                            return null;
                        }
                        _rescanAt = System.DateTime.UtcNow.AddMilliseconds(RescanMs);
                    }

                    Windows.Devices.Bluetooth.GenericAttributeProfile.GattReadResult result = Wait(_level.ReadValueAsync());
                    if (result == null ||
                        result.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                    {
                        Detach();
                        _rescanAt = System.DateTime.UtcNow.AddMilliseconds(RetryMs);
                        return null;
                    }

                    byte[] value = Bytes(result.Value);
                    raw = Hex(value);
                    if (value.Length < 1 || value[0] > 100) return null;

                    Track(value[0]);
                    return value[0];
                }
                catch (System.TypeLoadException)
                {
                    // WinRT metadata missing on this machine: stop trying.
                    _unavailable = true;
                    Detach();
                    return null;
                }
                catch (System.IO.FileNotFoundException)
                {
                    _unavailable = true;
                    Detach();
                    return null;
                }
                catch
                {
                    Detach();
                    _rescanAt = System.DateTime.UtcNow.AddMilliseconds(RetryMs);
                    return null;
                }
            }
        }

        /// <summary>
        /// True when the last reading looks like a charging pad's inflated 100%.
        /// Only maintained for one process lifetime: an app started while the pad is
        /// already charging has no lower reading to compare against, so its first
        /// value is taken at face value.
        /// </summary>
        internal static bool ChargingGuess
        {
            get { lock (Sync) return _chargingGuess; }
        }

        private static void Track(int percent)
        {
            System.DateTime now = System.DateTime.UtcNow;
            if (percent >= JumpToMin && _lastPercent.HasValue && _lastPercent.Value <= JumpFromMax &&
                (now - _lastPercentAt).TotalMilliseconds <= JumpWindowMs)
            {
                _chargingGuess = true;
            }
            else if (percent < JumpToMin)
            {
                _chargingGuess = false;
            }

            _lastPercent = percent;
            _lastPercentAt = now;
        }

        private static bool Attach()
        {
            Windows.Devices.Enumeration.DeviceInformationCollection devices = Wait(
                Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                    Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelector()));
            if (devices == null) return false;

            foreach (Windows.Devices.Enumeration.DeviceInformation info in devices)
            {
                if (!IsXboxName(info.Name)) continue;

                try
                {
                    Windows.Devices.Bluetooth.BluetoothLEDevice device =
                        Wait(Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(info.Id));
                    if (device == null) continue;
                    if (device.ConnectionStatus != Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected) continue;

                    Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceServicesResult services =
                        Wait(device.GetGattServicesAsync());
                    if (services == null ||
                        services.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success) continue;

                    foreach (Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService service in services.Services)
                    {
                        if (service.Uuid.ToString().IndexOf(BatteryServiceUuid,
                                System.StringComparison.OrdinalIgnoreCase) != 0) continue;

                        Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicsResult characteristics =
                            Wait(service.GetCharacteristicsAsync());
                        if (characteristics == null ||
                            characteristics.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success) continue;

                        foreach (Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic characteristic in characteristics.Characteristics)
                        {
                            if (characteristic.Uuid.ToString().IndexOf(BatteryLevelUuid,
                                    System.StringComparison.OrdinalIgnoreCase) != 0) continue;

                            // Keep every WinRT object on the way alive: the
                            // characteristic is unusable once its service is released.
                            _device = device;
                            _service = service;
                            _level = characteristic;
                            return true;
                        }
                    }
                }
                catch
                {
                    // A device that vanished mid-walk: try the next one.
                }
            }

            return false;
        }

        /// <summary>
        /// Blocks up to WaitMs. WinRT operations complete on their own threads, so
        /// waiting with an event is safe from the refresh thread and cannot deadlock
        /// on a message pump the way a task wait would.
        /// </summary>
        private static T Wait<T>(Windows.Foundation.IAsyncOperation<T> operation)
        {
            if (operation == null) return default(T);

            System.Threading.ManualResetEventSlim done = new System.Threading.ManualResetEventSlim(false);
            operation.Completed = delegate(Windows.Foundation.IAsyncOperation<T> op, Windows.Foundation.AsyncStatus status)
            {
                done.Set();
            };

            if (!done.Wait(WaitMs)) return default(T);
            return operation.GetResults();
        }

        private static byte[] Bytes(Windows.Storage.Streams.IBuffer buffer)
        {
            int length = (int)buffer.Length;
            byte[] bytes = new byte[length];
            Windows.Storage.Streams.DataReader reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);
            return bytes;
        }

        private static string Hex(byte[] bytes)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static bool IsXboxName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("xbox", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Detach()
        {
            _level = null;
            _service = null;
            _device = null;
        }
    }
}
#endif
