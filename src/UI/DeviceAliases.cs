using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Voltide.UI
{
    internal static class DeviceAliases
    {
        private const string KeyPath = @"Software\Voltide\DeviceAliases";
        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string alias;
            if (Cache.TryGetValue(id, out alias)) return alias;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    alias = key == null ? null : key.GetValue(id) as string;
                }
            }
            catch (System.Security.SecurityException) { alias = null; }
            catch (UnauthorizedAccessException) { alias = null; }
            Cache[id] = alias;
            return alias;
        }

        public static void Save(string id, string alias)
        {
            alias = alias.Trim();
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (alias.Length == 0) key.DeleteValue(id, false);
                else key.SetValue(id, alias);
            }
            Cache[id] = alias.Length == 0 ? null : alias;
        }
    }
}
