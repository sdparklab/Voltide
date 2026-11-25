using System;
using System.Collections.Generic;
using System.Threading;
using Voltide.Interop;
using Voltide.Providers;

namespace Voltide.Core
{
    public class DeviceSnapshot
    {
        public DeviceInfo Info;
        public DeviceReading Reading;
    }

    /// <summary>
    /// Owns all protocol providers and produces a stable snapshot for the UI.
    /// Refreshing happens on a background thread; the UI only ever reads snapshots.
    /// </summary>
    public class DeviceManager
    {
        private readonly List<IDeviceProvider> _providers = new List<IDeviceProvider>();
        private readonly object _lock = new object();
        private readonly object _refreshLock = new object();
        private List<DeviceSnapshot> _snapshots = new List<DeviceSnapshot>();

        public DeviceManager()
        {
            _providers.Add(new LogitechProvider());
            _providers.Add(new MchoseProvider());
            _providers.Add(new MchoseA7UltraProvider());
            _providers.Add(new VxeProvider());
            _providers.Add(new EweadnProvider());
            _providers.Add(new EweadnA5ProProvider());
            _providers.Add(new DareuA950MouseProvider());
            _providers.Add(new DareuA950Receiver1074Provider());
            _providers.Add(new DareuA950WiredProvider());            // 有线 USB 260D:1084
            _providers.Add(new DareuA950BluetoothProvider());        // 蓝牙 BLE 25A7:FA6C
            _providers.Add(new MchoseV9HeadsetProvider());
            _providers.Add(new MchoseK7MouseProvider());
            _providers.Add(new MchoseA5MouseProvider());
            _providers.Add(new AsusRogProvider());
            _providers.Add(new AsusHeadsetProvider());
            _providers.Add(new TaiduProvider());
            _providers.Add(new ImecooProvider());
            _providers.Add(new AtkMouseProvider());
            _providers.Add(new AtkHeadsetProvider());
            _providers.Add(new XInputProvider());
            _providers.Add(new X7MouseProvider());
            _providers.Add(new XiberiaT7Provider());
            _providers.Add(new DualSenseProvider());
            _providers.Add(new RapooProvider());
            _providers.Add(new RazerProvider());
            _providers.Add(new HeadsetProvider());
        }

        public List<DeviceSnapshot> Snapshot()
        {
            lock (_lock)
            {
                return new List<DeviceSnapshot>(_snapshots);
            }
        }

        /// <summary>Blocking. Safe to call from several threads: calls are serialized.</summary>
        public void Refresh()
        {
            lock (_refreshLock)
            {
                RefreshCore();
            }
        }

        private void RefreshCore()
        {
            List<DeviceSnapshot> next = new List<DeviceSnapshot>();

            // Enumerate once per refresh and share the result. Every provider used to
            // walk the whole HID tree itself, which cost ~400 ms per provider on a
            // machine with a busy USB stack — five providers meant two seconds of
            // identical work.
            List<HidDeviceInfo> catalog;
            try
            {
                catalog = HidNative.Enumerate();
            }
            catch
            {
                catalog = new List<HidDeviceInfo>();
            }

            foreach (IDeviceProvider p in _providers)
            {
                List<DeviceInfo> devices;
                try
                {
                    devices = p.Discover(catalog);
                }
                catch
                {
                    continue;
                }

                foreach (DeviceInfo d in devices)
                {
                    DeviceReading r;
                    try
                    {
                        r = p.Read(d);
                    }
                    catch (Exception ex)
                    {
                        r = new DeviceReading();
                        r.UpdatedAt = DateTime.Now;
                        r.Error = ex.Message;
                        r.Level = BatteryLevel.Unknown;
                    }

                    DeviceSnapshot s = new DeviceSnapshot();
                    s.Info = d;
                    s.Reading = r;
                    next.Add(s);
                }
            }

            next = KeepOnePerDevice(next);

            lock (_lock)
            {
                _snapshots = next;
            }
        }

        /// <summary>
        /// 同一只 A950 可能被多条链路同时认领：接收器插着、蓝牙连着、又插了数据线时，
        /// 三条链路会各报一个值（接收器在鼠标不在 2.4G 时回的是缓存值）。按 有线 > 蓝牙 > 2.4G
        /// 的优先级，每只设备只保留一条。只有这三个 provider 参与，其它品牌原样保留。
        /// </summary>
        private static List<DeviceSnapshot> KeepOnePerDevice(List<DeviceSnapshot> snapshots)
        {
            if (snapshots == null || snapshots.Count < 2) return snapshots;

            Dictionary<string, int> winner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < snapshots.Count; i++)
            {
                int priority = LinkPriority(snapshots[i]);
                if (priority < 0) continue;

                string key = DeviceKey(snapshots[i]);
                int current;
                if (!winner.TryGetValue(key, out current)) { winner[key] = i; continue; }
                if (priority < LinkPriority(snapshots[current])) winner[key] = i;
            }

            List<DeviceSnapshot> kept = new List<DeviceSnapshot>(snapshots.Count);
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (LinkPriority(snapshots[i]) < 0) { kept.Add(snapshots[i]); continue; }

                int chosen;
                if (winner.TryGetValue(DeviceKey(snapshots[i]), out chosen) && chosen != i) continue;
                kept.Add(snapshots[i]);
            }
            return kept;
        }

        /// <summary>0 = 优先级最高。</summary>
        private static int LinkPriority(DeviceSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Info == null) return -1;
            switch (snapshot.Info.ProviderName)
            {
                case "DAREU USB": return 0;    // 有线：实时值，且能确认充电
                case "DAREU BT": return 1;     // 蓝牙：系统估算值
                case "DAREU 2.4G": return 2;   // 接收器：其它模式下回缓存值
                default: return -1;
            }
        }

        private static string DeviceKey(DeviceSnapshot snapshot)
        {
            string brand = snapshot.Info == null ? "" : (snapshot.Info.Brand ?? "");
            string model = snapshot.Info == null ? "" : (snapshot.Info.Model ?? "");
            return brand + "|" + model;
        }
    }
}
