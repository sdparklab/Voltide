using System;
using System.Collections.Generic;
using System.Threading;
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
            _providers.Add(new AsusRogProvider());
            _providers.Add(new ATKA9AiraddProvider());
            _providers.Add(new ATKN9V2UltimateProvider());
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

            foreach (IDeviceProvider p in _providers)
            {
                List<DeviceInfo> devices;
                try
                {
                    devices = p.Discover();
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

            lock (_lock)
            {
                _snapshots = next;
            }
        }
    }
}
