using System.Collections.Generic;

namespace Voltide.UI
{
    internal sealed class BatteryAlertState
    {
        private readonly Dictionary<string, int> _notified = new Dictionary<string, int>();

        public int Update(string id, int percent, bool charging)
        {
            if (percent < 0 || percent > 100) return 0;
            if (percent > 10)
            {
                _notified.Remove(id);
                return 0;
            }

            int previous;
            _notified.TryGetValue(id, out previous);
            if (percent > 5 && previous == 5)
            {
                previous = 10;
                _notified[id] = previous;
            }

            int threshold = percent <= 5 ? 5 : 10;
            if (charging || (previous != 0 && previous <= threshold)) return 0;
            _notified[id] = threshold;
            return threshold;
        }
    }
}
