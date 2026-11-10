using System.Collections.Generic;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class TroubleshootingTests
    {
        [TestMethod]
        public void ParseDeviceId_DeviceAndInterface()
        {
            var d = UsbDeviceScanner.ParseDeviceId(@"USB\VID_05E0&PID_2101\21345678");
            Assert.AreEqual("05E0", d.Vid);
            Assert.AreEqual("2101", d.Pid);
            Assert.AreEqual("21345678", d.Tail);
            Assert.IsTrue(UsbDeviceScanner.IsInterfaceId(@"USB\VID_05E0&PID_2101&MI_01\6&2C3F&0&0001"));
            Assert.AreEqual("", UsbDeviceScanner.ParseDeviceId(@"HID\VID_046D").Vid);
        }

        [TestMethod]
        public void Group_RecognisesAdbMtpDriverAndUnknownBrands()
        {
            var entries = new List<(string, string, string, int)>
            {
                (@"USB\VID_05E0&PID_2101\21345678", "TC53", "USB", 0),
                (@"USB\VID_05E0&PID_2101&MI_00\6&1&0&0000", "TC53", "WPD", 0),
                (@"USB\VID_05E0&PID_2101&MI_01\6&1&0&0001", "Android Composite ADB Interface", "AndroidUsbDeviceClass", 0),
                (@"USB\VID_0C2E&PID_0BB0\CT45SERIAL", "CT45", "USB", 28),
                (@"USB\VID_1234&PID_0001\ABC", "Clé USB", "DiskDrive", 0),
                (@"USB\VID_9999&PID_0002\XYZ", "ADB Interface", "AndroidUsbDeviceClass", 0),
            };
            var list = UsbDeviceScanner.Group(entries);
            Assert.AreEqual(3, list.Count);   // la clé USB est ignorée
            var zebra = list.Single(d => d.Vid == "05E0");
            Assert.AreEqual("Zebra", zebra.Brand);
            Assert.AreEqual("21345678", zebra.Serial);
            Assert.IsTrue(zebra.HasAdbInterface);
            Assert.IsTrue(zebra.HasMtp);
            Assert.IsFalse(zebra.DriverMissing);
            var hw = list.Single(d => d.Vid == "0C2E");
            Assert.AreEqual("Honeywell", hw.Brand);
            Assert.IsTrue(hw.DriverMissing);
            Assert.IsTrue(list.Single(d => d.Vid == "9999").HasAdbInterface);   // fabricant inconnu mais interface ADB
        }

        [DataTestMethod]
        [DataRow("ET40*, ET45*", "ET45", true)]
        [DataRow("ET40*, ET45*", "et401", true)]
        [DataRow("ET40*, ET45*", "TC53", false)]
        [DataRow("*", "TC53", true)]
        [DataRow("Joya Touch A6*", "Joya Touch A6 Plus", true)]
        public void ModelMatches(string patterns, string model, bool expected) =>
            Assert.AreEqual(expected, RecoveryKeyGuide.ModelMatches(patterns, model));

        [TestMethod]
        public void Find_PreciseModelFirst_GenericLast()
        {
            var r = RecoveryKeyGuide.Find(RecoveryKeyGuide.BuiltIn, "Zebra Technologies", "WS50");
            Assert.AreEqual("WS50*", r[0].Models);
            Assert.IsTrue(r.Last().Brand.StartsWith("Android"));
            Assert.IsFalse(r.Any(e => e.Models.StartsWith("ET4")));
            Assert.AreEqual("Zebra", RecoveryKeyGuide.NormalizeBrand("Symbol Technologies"));
            Assert.AreEqual("Honeywell", RecoveryKeyGuide.NormalizeBrand("Intermec"));
            var h = RecoveryKeyGuide.Find(RecoveryKeyGuide.BuiltIn, "Honeywell", "CT45");
            Assert.IsTrue(h.All(e => e.Brand.StartsWith("Android")));   // rien de vérifié : génériques seulement
        }

        [TestMethod]
        public void UserFile_RoundTrip()
        {
            string path = System.IO.Path.GetTempFileName();
            try
            {
                System.IO.File.WriteAllText(path, RecoveryKeyGuide.UserTemplate());
                var l = RecoveryKeyGuide.LoadUser(path);
                Assert.AreEqual(1, l.Count);
                Assert.IsTrue(l[0].Custom);
                Assert.AreEqual("Honeywell", l[0].Brand);
                var found = RecoveryKeyGuide.Find(l.Concat(RecoveryKeyGuide.BuiltIn), "Honeywell", "CT45");
                Assert.IsTrue(found[0].Custom);
                System.IO.File.WriteAllText(path, "pas du json");
                Assert.AreEqual(0, RecoveryKeyGuide.LoadUser(path).Count);
            }
            finally { System.IO.File.Delete(path); }
        }

        [TestMethod]
        public void FastbootSummary_Readable()
        {
            var s = RecoveryKeyGuide.FastbootSummary(new Dictionary<string, string> { { "product", "athena" }, { "unlocked", "no" }, { "serialno", "123" }, { "is-userspace", "no" } });
            CollectionAssert.Contains(s.Select(x => x.Label + "=" + x.Value).ToList(), "Bootloader déverrouillé=non");
            CollectionAssert.Contains(s.Select(x => x.Label + "=" + x.Value).ToList(), "Mode=bootloader");
            Assert.IsFalse(s.Any(x => x.Label == "Slot actif"));
        }
    }
}
