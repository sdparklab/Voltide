using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;
using Voltide.Providers;

public static class KeychronM6Tests
{
    private static int count;
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        count++;
    }
    private static byte[] Status()
    {
        return new byte[] { 0x51, 6, 1, 0, 0x34, 0x34, 0x3F, 0xD0, 6, 1, 9, 15, 0 };
    }
    public static int Main(string[] args)
    {
        try
        {
            byte[] frame = Status();
            DeviceReading r = KeychronM6Provider.DecodeStatus(frame, true);
            Check(r.Online && r.Percent == 15 && !r.Charging && r.ChargingKnown, "captured M6-A3 response");
            frame[11] = 0;
            Check(KeychronM6Provider.DecodeStatus(frame, true).Percent == 0, "zero percent is valid");
            frame[11] = 100; frame[12] = 1;
            r = KeychronM6Provider.DecodeStatus(frame, true);
            Check(r.Percent == 100 && r.Charging, "full and charging");
            frame[11] = 101;
            Check(!KeychronM6Provider.DecodeStatus(frame, true).Online, "reject invalid percent");
            frame = Status(); frame[10] = 1;
            r = KeychronM6Provider.DecodeStatus(frame, true);
            Check(r.Sleeping && !r.Online && !r.Percent.HasValue && !r.ChargingKnown, "unlinked receiver cannot supply live battery");
            Check(KeychronM6Provider.DecodeStatus(frame, false).Online, "USB needs no radio link bit");
            frame = Status(); frame[6] = 0x33;
            Check(!KeychronM6Provider.DecodeStatus(frame, true).Online, "reject another paired mouse");
            frame = Status(); frame[1] = 3;
            Check(!KeychronM6Provider.DecodeStatus(frame, true).Online, "reject stale identity reply");
            Check(!KeychronM6Provider.DecodeStatus(null, true).Online, "null reply");
            Check(!KeychronM6Provider.DecodeStatus(new byte[12], true).Online, "short reply");
            byte[] pair = { 0x51, 3, 1, 0x34, 0x34, 0x3F, 0xD0 };
            Check(KeychronM6Provider.IsM6Pair(pair), "M6 identity");
            pair[5] = 0x33;
            Check(!KeychronM6Provider.IsM6Pair(pair), "shared receiver must not match M3");
            Check(!KeychronM6Provider.IsM6Pair(new byte[6]), "short identity");
            var provider = new KeychronM6Provider();
            Check(provider.Discover(new List<HidDeviceInfo> {
                new HidDeviceInfo { VendorId = 0x3434, ProductId = 0xD03F, UsagePage = 1, Usage = 2, FeatureReportLength = 1001, Path = "mouse" },
                new HidDeviceInfo { VendorId = 0x3434, ProductId = 0xD046, UsagePage = 0x8C, Usage = 1, FeatureReportLength = 1001, Path = "4k" }
            }).Count == 0, "exclude pointer collection and 4K model");
            Console.WriteLine("PASS: " + count + " protocol checks");
            if (args.Length > 0 && args[0] == "--hardware")
            {
                List<DeviceInfo> devices = provider.Discover(HidNative.Enumerate());
                Check(devices.Count > 0, "connected M6 discovered");
                foreach (DeviceInfo d in devices)
                {
                    Console.WriteLine(d.Name + " " + d.VendorId.ToString("X4") + ":" + d.ProductId.ToString("X4"));
                    for (int i = 0; i < 3; i++)
                    {
                        r = provider.Read(d);
                        Console.WriteLine("online=" + r.Online + " percent=" + r.Percent + " charging=" + r.Charging + " raw=" + r.Raw + " error=" + r.Error);
                        Check(r.Online && r.Percent.HasValue, "live battery read " + i);
                    }
                }
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
