using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Voltide.Core
{
    /// <summary>
    /// Last known battery per device, kept on disk.
    ///
    /// Passive 2.4G receivers stop pushing reports as soon as the peripheral drops into
    /// its idle power state, and the upstream providers only cache in memory — so after
    /// a restart (or a boot where the mouse was already asleep) the card fell back to
    /// "--". Persisting the last reading lets a sleeping device keep reporting its last
    /// known value, greyed out, exactly like it does when it falls asleep mid-session.
    ///
    /// Format (one record per line, tab separated, UTF-8):
    ///   id \t percent \t charging(0/1) \t chargingKnown(0/1) \t roundtrip time \t raw
    /// </summary>
    internal static class ReadingStore
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, Entry> _map;
        private static string _path;

        private sealed class Entry
        {
            public int Percent;
            public bool Charging;
            public bool ChargingKnown;
            public string Raw;
            public DateTime At;
        }

        private static string FilePath
        {
            get
            {
                if (_path == null)
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Voltide");
                    _path = Path.Combine(dir, "readings.tsv");
                }
                return _path;
            }
        }

        private static void EnsureLoaded()
        {
            if (_map != null) return;

            _map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;

                foreach (string line in File.ReadAllLines(FilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 6) continue;

                    int percent;
                    if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out percent)) continue;

                    DateTime at;
                    if (!DateTime.TryParse(f[4], CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out at)) continue;

                    Entry e = new Entry();
                    e.Percent = percent;
                    e.Charging = f[2] == "1";
                    e.ChargingKnown = f[3] == "1";
                    e.Raw = f[5];
                    e.At = at;
                    _map[f[0]] = e;
                }
            }
            catch
            {
                // A corrupt cache must never stop a provider from reading live data.
                _map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static bool TryGet(string id, out int percent, out bool charging, out bool chargingKnown,
                                 out string raw, out DateTime at)
        {
            percent = 0;
            charging = false;
            chargingKnown = false;
            raw = "";
            at = DateTime.MinValue;

            if (string.IsNullOrEmpty(id)) return false;

            lock (_lock)
            {
                EnsureLoaded();
                Entry e;
                if (!_map.TryGetValue(id, out e)) return false;

                percent = e.Percent;
                charging = e.Charging;
                chargingKnown = e.ChargingKnown;
                raw = e.Raw;
                at = e.At;
                return true;
            }
        }

        public static void Save(string id, int percent, bool charging, bool chargingKnown, string raw)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (percent < 0 || percent > 100) return;

            lock (_lock)
            {
                EnsureLoaded();
                Entry e = new Entry();
                e.Percent = percent;
                e.Charging = charging;
                e.ChargingKnown = chargingKnown;
                e.Raw = raw == null ? "" : raw;
                e.At = DateTime.Now;
                _map[id] = e;
                Flush();
            }
        }

        private static void Flush()
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                List<string> lines = new List<string>(_map.Count);
                foreach (KeyValuePair<string, Entry> kv in _map)
                {
                    Entry e = kv.Value;
                    lines.Add(string.Join("\t", new string[]
                    {
                        kv.Key,
                        e.Percent.ToString(CultureInfo.InvariantCulture),
                        e.Charging ? "1" : "0",
                        e.ChargingKnown ? "1" : "0",
                        e.At.ToString("o", CultureInfo.InvariantCulture),
                        e.Raw.Replace('\t', ' ')
                    }));
                }

                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch
            {
                // Cache is a convenience; never let a disk problem break a refresh.
            }
        }
    }
}
