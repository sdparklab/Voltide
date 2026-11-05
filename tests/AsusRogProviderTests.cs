using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;
using Voltide.Providers;
using Voltide.UI;

namespace Voltide.Tests
{
    internal static class AsusRogProviderTests
    {
        public static int Main()
        {
            try
            {
                Run("two paired devices, different battery values", DualPairing);
                Run("keyboard-only, mouse-only and empty pairing tables", SinglePairing);
                Run("legacy keyboard without a pairing reply", LegacyKeyboard);
                Run("two receivers with the same model, one asleep", ReceiverIsolation);
                Run("malformed and ambiguous pairing tables", BadPairingTable);
                Run("standby, out-of-range and real zero battery replies", UnavailableBattery);
                Run("other receivers and existing panel deduplication", OtherReceivers);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void DualPairing()
        {
            var hid = new FakeHid();
            var catalog = Receiver("111");
            // Real capture: 1AB0/02/04 and 1A94/03/05; reverse the rows to
            // make sure the position in the table does not select the route.
            hid.Expect(catalog[0], 1, 0xA0, 0, PairList(0x94, 0x1A, 3, 5, 0xB0, 0x1A, 2, 4));
            hid.Expect(catalog[1], 2, 0x12, 1, Keyboard100());
            hid.Expect(catalog[2], 3, 0x12, 7,
                Report(2, 0x12, 7, 0, 0, 99),        // right command, wrong report
                Report(3, 0x12, 1, 0, 0, 88),        // right report, wrong command
                new byte[] { 3, 0x12, 7, 0, 0, 77 }, // truncated reply
                Report(3, 0x12, 7, 0, 0, 3, 2, 0x14, 0xFA, 0x0D, 1));
            catalog.Reverse();

            List<DeviceInfo> devices = hid.Provider.Discover(catalog);
            Check(devices.Count == 2, "two paired children");
            DeviceInfo keyboard = devices.Find(d => d.Kind == DeviceKind.Keyboard);
            DeviceInfo mouse = devices.Find(d => d.Kind == DeviceKind.Mouse);
            Check(keyboard.ProductId == 0x1AB0 && keyboard.Model == "SCOPE II 96", "paired keyboard model");
            Check(mouse.ProductId == 0x1A94 && mouse.Model == "HARPE ACE", "paired mouse model");
            Check(keyboard.ReceiverId == mouse.ReceiverId && keyboard.Id != mouse.Id &&
                  keyboard.HidPath != mouse.HidPath, "shared receiver, distinct ids and HID channels");
            DeviceSnapshot keys = Snapshot(hid, keyboard);
            DeviceSnapshot pointer = Snapshot(hid, mouse);
            Check(keys.Reading.Online && keys.Reading.Percent == 100, "keyboard: byte 6 = 100%");
            Check(pointer.Reading.Online && pointer.Reading.Percent == 3, "mouse: byte 5 = 3%");
            Check(MainForm.CollapseTransports(new List<DeviceSnapshot> { keys, pointer }).Count == 2,
                "both battery readings reach the panel");
            hid.Done();
        }

        private static void SinglePairing()
        {
            var catalog = Receiver("111");
            var hid = new FakeHid();
            hid.Expect(catalog[0], 1, 0xA0, 0, PairList(0x42, 0x1B, 2, 4));
            hid.Expect(catalog[1], 2, 0x12, 1, Keyboard48());
            List<DeviceInfo> devices = hid.Provider.Discover(catalog);
            Check(devices.Count == 1 && devices[0].Name == "ROG AZOTH EXTREME", "keyboard-only Omni");
            Check(hid.Provider.Read(devices[0]).Percent == 48, "existing Azoth battery layout");
            hid.Done();

            hid.Expect(catalog[0], 1, 0xA0, 0, PairList(0xFE, 0x1C, 3, 5));
            hid.Expect(catalog[2], 3, 0x12, 7, Report(3, 0x12, 7, 0, 0, 61));
            devices = hid.Provider.Discover(catalog);
            Check(devices.Count == 1 && devices[0].Kind == DeviceKind.Mouse, "mouse-only Omni");
            Check(devices[0].Model == "鼠标 1CFE" && hid.Provider.Read(devices[0]).Percent == 61,
                "unknown model uses its paired PID and mouse battery layout");
            hid.Done();

            hid.Expect(catalog[0], 1, 0xA0, 0, PairList());
            Check(hid.Provider.Discover(catalog).Count == 0, "empty table emits no phantom devices");
            hid.Done();
        }

        private static void LegacyKeyboard()
        {
            var hid = new FakeHid();
            var catalog = Receiver("111");
            hid.Expect(catalog[0], 1, 0xA0, 0); // timeout
            hid.Expect(catalog[1], 2, 0x12, 1, Keyboard48());
            List<DeviceInfo> devices = hid.Provider.Discover(catalog);
            Check(devices.Count == 1 && devices[0].Kind == DeviceKind.Keyboard && devices[0].ProductId == 0,
                "one confirmed keyboard without a model guess");
            Check(hid.Provider.Read(devices[0]).Percent == 48, "legacy result reused, no second query");
            hid.Done();

            hid.Expect(catalog[0], 1, 0xA0, 0);
            hid.Expect(catalog[1], 2, 0x12, 1);
            Check(hid.Provider.Discover(catalog).Count == 0, "no legacy card without a valid reply");
            hid.Done();
        }

        private static void ReceiverIsolation()
        {
            var hid = new FakeHid();
            var a = Receiver("111");
            var b = Receiver("222");
            var catalog = new List<HidDeviceInfo> { a[1], b[2], b[0], a[2], b[1], a[0] };
            hid.Expect(a[0], 1, 0xA0, 0, PairList(0x42, 0x1B, 2, 4));
            hid.Expect(b[0], 1, 0xA0, 0, PairList(0x42, 0x1B, 2, 4));
            hid.Expect(a[1], 2, 0x12, 1, Keyboard48());
            hid.Expect(b[1], 2, 0x12, 1);
            List<DeviceInfo> devices = hid.Provider.Discover(catalog);
            Check(devices.Count == 2 && devices[0].Id != devices[1].Id &&
                  devices[0].ReceiverId != devices[1].ReceiverId, "separate receiver instances with the same PID");
            DeviceSnapshot first = Snapshot(hid, devices.Find(d => d.HidPath == a[1].Path));
            DeviceSnapshot second = Snapshot(hid, devices.Find(d => d.HidPath == b[1].Path));
            Check(first.Reading.Percent == 48 && second.Reading.Sleeping && !second.Reading.Percent.HasValue,
                "sleeping device does not inherit the other receiver's value");
            Check(MainForm.CollapseTransports(new List<DeviceSnapshot> { first, second }).Count == 2,
                "same-model sleeping child stays separate on the panel");
            hid.Done();

            hid.Expect(a[0], 1, 0xA0, 0, PairList(0xB0, 0x1A, 2, 4));
            List<DeviceInfo> updated = hid.Provider.Discover(a);
            Check(updated.Count == 1 && updated[0].ReceiverId == first.Info.ReceiverId &&
                  updated[0].Id != first.Info.Id, "different product paired to the same report gets a new id");
            hid.Done();
        }

        private static void BadPairingTable()
        {
            var hid = new FakeHid();
            var catalog = Receiver("111");
            hid.Expect(catalog[0], 1, 0xA0, 0, new byte[] { 1, 0xA0, 0, 2, 0, 0xB0, 0x1A, 2, 4 });
            Check(hid.Provider.Discover(catalog).Count == 0, "truncated table is rejected");
            hid.Expect(catalog[0], 1, 0xA0, 0, PairList(0x42, 0x1B, 2, 4, 0xB0, 0x1A, 2, 4));
            Check(hid.Provider.Discover(catalog).Count == 0, "conflicting products on one report are rejected");
            hid.Done();
        }

        private static void UnavailableBattery()
        {
            var hid = new FakeHid();
            var catalog = Receiver("111");
            hid.Expect(catalog[0], 1, 0xA0, 0, PairList(0x42, 0x1B, 2, 4, 0x94, 0x1A, 3, 5));
            hid.Expect(catalog[1], 2, 0x12, 1, Report(2, 0x12, 1, 0, 0, 0, 101));
            hid.Expect(catalog[2], 3, 0x12, 7, Report(3, 0x12, 7));
            List<DeviceInfo> devices = hid.Provider.Discover(catalog);
            Check(devices.Count == 2, "both devices still paired");
            foreach (DeviceInfo d in devices)
            {
                DeviceReading reading = hid.Provider.Read(d);
                Check(reading.Sleeping && !reading.Percent.HasValue, "unavailable reading stays unknown");
            }
            hid.Expect(catalog[2], 3, 0x12, 7, Report(3, 0x12, 7, 0, 0, 0, 2, 0x14, 0xFA, 0x0D));
            DeviceReading zero = hid.Provider.Read(devices.Find(d => d.Kind == DeviceKind.Mouse));
            Check(zero.Online && zero.Percent == 0, "valid zero percent preserved");
            hid.Done();
        }

        private static void OtherReceivers()
        {
            var hid = new FakeHid();
            var logi = new HidDeviceInfo { VendorId = 0x046D, ProductId = 0xC53F, UsagePage = 0xFF00,
                Usage = 2, InputReportLength = 20, OutputReportLength = 20, Path = "logi" };
            var audio = new HidDeviceInfo { VendorId = 0x0B05, ProductId = 0x1AFA, UsagePage = 0xFF00,
                InputReportLength = 64, OutputReportLength = 64, Path = "audio" };
            var catalog = new List<HidDeviceInfo> { logi, audio };
            Check(hid.Provider.Discover(catalog).Count == 0, "Omni leaves other receivers alone");
            List<DeviceInfo> logitech = new LogitechProvider().Discover(catalog);
            Check(logitech.Count == 1, "G304 discovery");
            Check(new AsusHeadsetProvider().Discover(catalog).Count == 1, "Delta II discovery");

            var awake = new DeviceSnapshot { Info = logitech[0], Reading = new DeviceReading { Online = true } };
            var asleep = new DeviceSnapshot
            {
                Info = new DeviceInfo { Id = "wired", Brand = awake.Info.Brand, Model = awake.Info.Model },
                Reading = new DeviceReading { Sleeping = true }
            };
            List<DeviceSnapshot> panel = MainForm.CollapseTransports(new List<DeviceSnapshot> { asleep, awake });
            Check(panel.Count == 1 && panel[0] == awake, "existing wired/wireless dedup still prefers live");
            hid.Done();
        }

        private static List<HidDeviceInfo> Receiver(string instance)
        {
            var catalog = new List<HidDeviceInfo>();
            ushort[] pages = { 0xFF02, 0xFF00, 0xFF01 };
            for (int i = 0; i < pages.Length; i++)
            {
                catalog.Add(new HidDeviceInfo
                {
                    VendorId = 0x0B05, ProductId = 0x1ACE, UsagePage = pages[i],
                    InputReportLength = 64, OutputReportLength = 64,
                    Path = @"\\?\hid#vid_0b05&pid_1ace&mi_02&col0" + (i + 1) +
                           "#9&" + instance + "&0&000" + i + "#{4d1e55b2-f16f-11cf-88cb-001111000030}"
                });
            }
            return catalog;
        }

        private static byte[] Report(params byte[] prefix)
        {
            var report = new byte[64];
            prefix.CopyTo(report, 0);
            return report;
        }

        private static byte[] PairList(params byte[] entries)
        {
            byte[] report = Report(1, 0xA0, 0, (byte)(entries.Length / 4), 0);
            entries.CopyTo(report, 5);
            return report;
        }

        private static byte[] Keyboard48()
        {
            return Report(2, 0x12, 1, 0, 0, 0, 0x30, 2, 1, 0, 0x14, 0x30, 0xED, 0x0E);
        }

        private static byte[] Keyboard100()
        {
            return Report(2, 0x12, 1, 0, 0, 0, 0x64, 0xFF, 1, 1, 0x14, 0x64, 0xB0, 0x10);
        }

        private static DeviceSnapshot Snapshot(FakeHid hid, DeviceInfo device)
        {
            return new DeviceSnapshot { Info = device, Reading = hid.Provider.Read(device) };
        }

        private static void Run(string name, Action test)
        {
            test();
            Console.WriteLine("PASS " + name);
        }

        private static void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("Assertion failed: " + description);
        }

        private sealed class FakeHid
        {
            private readonly Dictionary<string, Exchange> _pending = new Dictionary<string, Exchange>();
            public readonly AsusRogProvider Provider;

            public FakeHid()
            {
                Provider = new AsusRogProvider(Open);
            }

            public void Expect(HidDeviceInfo channel, byte id, byte family, byte command, params byte[][] replies)
            {
                _pending.Add(channel.Path, new Exchange(new byte[] { id, family, command }, replies));
            }

            public void Done()
            {
                Check(_pending.Count == 0, "all expected queries were sent");
            }

            private IHidReportSession Open(HidDeviceInfo channel)
            {
                Exchange exchange;
                Check(_pending.TryGetValue(channel.Path, out exchange), "unexpected HID channel " + channel.Path);
                _pending.Remove(channel.Path);
                return exchange;
            }
        }

        private sealed class Exchange : IHidReportSession
        {
            private readonly byte[] _prefix;
            private readonly Queue<byte[]> _replies;
            private bool _sent;

            public Exchange(byte[] prefix, byte[][] replies)
            {
                _prefix = prefix;
                _replies = new Queue<byte[]>(replies);
            }

            public void Flush(int idleMs) { }

            public bool Write(byte[] request, int timeoutMs)
            {
                Check(!_sent && request.Length == 64, "one full-size request per session");
                for (int i = 0; i < request.Length; i++)
                    Check(request[i] == (i < _prefix.Length ? _prefix[i] : 0), "request byte " + i);
                _sent = true;
                return true;
            }

            public byte[] Read(int timeoutMs)
            {
                Check(_sent && timeoutMs > 0, "read after write with a bounded timeout");
                return _replies.Count == 0 ? null : _replies.Dequeue();
            }

            public void Dispose()
            {
                Check(_sent, "expected request was sent");
            }
        }
    }
}
