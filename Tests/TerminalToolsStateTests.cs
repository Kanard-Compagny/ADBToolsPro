using System;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class TerminalToolsStateTests
    {
        private static string[] Sample() => new[]
        {
            "1763030645",
            "1", "0", "2", "0", "Night mode: no", "153",
            "Physical density: 320\nOverride density: 280",
            "Physical size: 720x1280",
            "feature:android.hardware.nfc\nfeature:android.hardware.bluetooth\nfeature:android.hardware.wifi",
            "mState=on",
            "3", "1", "ABSENT", "60000", "1",
            "volume is 8 in range [0..15]",
            "0", "1", "Europe/Paris", "fr-FR", "0", "34", "Zebra Technologies",
            "package:com.symbol.datawedge", "TC53", "14",
        };

        [TestMethod]
        public void ReadsEveryValue()
        {
            Assert.AreEqual(TerminalToolsState.Commands(3).Length, Sample().Length);
            var t = TerminalToolsState.Parse(Sample());
            Assert.AreEqual(true, t.Wifi);
            Assert.AreEqual(false, t.Airplane);
            Assert.AreEqual(false, t.Bluetooth, "2 = BLE seul, vu comme inactif");
            Assert.AreEqual(false, t.DarkMode);
            Assert.AreEqual(60, t.BrightnessPercent);
            Assert.AreEqual(280, t.Dpi);
            Assert.IsTrue(t.DpiOverridden);
            Assert.AreEqual("720x1280", t.Resolution);
            Assert.IsTrue(t.HasNfc);
            Assert.IsFalse(t.HasTelephony);
            Assert.AreEqual(true, t.Nfc);
            Assert.AreEqual(true, t.Location);
            Assert.IsTrue(t.SimAbsent);
            Assert.AreEqual(1, TerminalToolsState.TimeoutIndex(t.TimeoutMs));
            Assert.AreEqual(1, t.RingerMode);
            Assert.AreEqual(8, t.VolumeIndex);
            Assert.AreEqual(15, t.VolumeMax);
            Assert.AreEqual(false, t.AutoTime);
            Assert.AreEqual("Europe/Paris", t.TimeZone);
            Assert.AreEqual(0, t.StayOnPlugged);
            Assert.AreEqual(34, t.Sdk);
            Assert.IsNotNull(t.ScanTriggerCommand());
        }

        [TestMethod]
        public void UnreadableValuesStayUnknown()
        {
            var t = TerminalToolsState.Parse(new string[0]);
            Assert.IsNull(t.Wifi);
            Assert.IsNull(t.Nfc);
            Assert.IsNull(t.VolumeIndex);
            Assert.IsNull(t.ClockOffsetSeconds(DateTime.UtcNow));
            Assert.IsNull(t.ScanTriggerCommand());
        }

        [TestMethod]
        public void ScanTriggerOnlyOnZebraWithDataWedge()
        {
            var s = Sample();
            s[23] = "Honeywell";
            Assert.IsNull(TerminalToolsState.Parse(s).ScanTriggerCommand());
            s = Sample();
            s[24] = "";
            Assert.IsNull(TerminalToolsState.Parse(s).ScanTriggerCommand());
        }

        [TestMethod]
        public void ClockOffset()
        {
            var t = TerminalToolsState.Parse(Sample());
            var pc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1763030645 - 84);
            Assert.AreEqual(84, t.ClockOffsetSeconds(pc));
            StringAssert.Contains(TerminalToolsState.FormatDuration(84), "1 min 24 s");
        }

        [TestMethod]
        public void TimeoutMapping()
        {
            Assert.AreEqual(4, TerminalToolsState.TimeoutIndex(int.MaxValue));
            Assert.AreEqual(4, TerminalToolsState.TimeoutIndex(1000000000));
            Assert.AreEqual(-1, TerminalToolsState.TimeoutIndex(120000));
            Assert.AreEqual(-1, TerminalToolsState.TimeoutIndex(null));
        }

        [TestMethod]
        public void NfcStates()
        {
            Assert.AreEqual(false, TerminalToolsState.ParseNfcState("  mState=off"));
            Assert.AreEqual(true, TerminalToolsState.ParseNfcState("mState=turning_on"));
            Assert.IsNull(TerminalToolsState.ParseNfcState("Can't find service: nfc"));
        }
    }
}
