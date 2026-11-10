using System;
using System.IO;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class MasterProfileTests
    {
        [TestMethod]
        public void ParseSettingsList_KeepsEqualsInValue_AndSkipsGarbage()
        {
            var r = MasterProfile.ParseSettingsList("screen_off_timeout=600000\r\nenabled_accessibility_services=a/b=c\nnot a setting\n=x\n");
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("600000", r[0].Value);
            Assert.AreEqual("a/b=c", r[1].Value);
        }

        [DataTestMethod]
        [DataRow("android_id", true)]
        [DataRow("bluetooth_name", true)]
        [DataRow("device_provisioned", true)]
        [DataRow("adb_enabled", true)]
        [DataRow("boot_count", true)]
        [DataRow("screen_off_timeout", false)]
        [DataRow("default_input_method", false)]
        [DataRow("stay_on_while_plugged_in", false)]
        public void IsDeviceSpecificSetting(string key, bool expected) =>
            Assert.AreEqual(expected, MasterProfile.IsDeviceSpecificSetting(key));

        [TestMethod]
        public void ParsePackagesWithVersion_WithAndWithoutVersion()
        {
            var r = MasterProfile.ParsePackagesWithVersion("package:com.soti.mobicontrol versionCode:1500\npackage:com.example.app\nERROR\n");
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("com.soti.mobicontrol", r[0].Key);
            Assert.AreEqual("1500", r[0].Value);
            Assert.AreEqual("", r[1].Value);
        }

        [TestMethod]
        public void ParsePmPath_SplitApks()
        {
            var r = MasterProfile.ParsePmPath("package:/data/app/~~x/com.a-1/base.apk\r\npackage:/data/app/~~x/com.a-1/split_config.fr.apk\n");
            CollectionAssert.AreEqual(new[] { "/data/app/~~x/com.a-1/base.apk", "/data/app/~~x/com.a-1/split_config.fr.apk" }, r);
        }

        [TestMethod]
        public void ParseGrantedRuntimePermissions_OnlyGrantedInRuntimeSection()
        {
            const string dump = "    install permissions:\n      android.permission.INTERNET: granted=true\n" +
                                "    User 0: ceDataInode=1 installed=true\n" +
                                "      runtime permissions:\n" +
                                "        android.permission.CAMERA: granted=true, flags=[ USER_SET ]\n" +
                                "        android.permission.ACCESS_FINE_LOCATION: granted=false, flags=[ ]\n" +
                                "        android.permission.READ_EXTERNAL_STORAGE: granted=true\n" +
                                "      disabledComponents:\n        com.a.X: granted=true\n";
            var r = MasterProfile.ParseGrantedRuntimePermissions(dump);
            CollectionAssert.AreEqual(new[] { "android.permission.CAMERA", "android.permission.READ_EXTERNAL_STORAGE" }, r);
        }

        [TestMethod]
        public void ParseWifiNetworks_Android11Output()
        {
            const string o = "Network Id      SSID                         Security type\n" +
                             "0            Atelier Wi-Fi                   wpa2-psk\n" +
                             "1            Invites                         open\n" +
                             "2            Entreprise                      wpa2-eap\n";
            var r = MasterProfile.ParseWifiNetworks(o);
            Assert.AreEqual(3, r.Count);
            Assert.AreEqual("Atelier Wi-Fi", r[0].Ssid);
            Assert.AreEqual("wpa2", MasterProfile.WifiConnectType(r[0].Security));
            Assert.AreEqual("open", MasterProfile.WifiConnectType(r[1].Security));
            Assert.AreEqual("", MasterProfile.WifiConnectType(r[2].Security));
            Assert.AreEqual("wpa3", MasterProfile.WifiConnectType("sae"));
        }

        [TestMethod]
        public void ParseDeviceOwner_AndHome()
        {
            const string dp = "Current Device Policy Manager state:\n  Device Owner: \n    admin=ComponentInfo{net.soti.mobicontrol.androidwork/net.soti.mobicontrol.admin.DeviceAdminAdapter}\n    name=\n";
            Assert.AreEqual("net.soti.mobicontrol.androidwork/net.soti.mobicontrol.admin.DeviceAdminAdapter", MasterProfile.ParseDeviceOwner(dp));
            Assert.AreEqual("", MasterProfile.ParseDeviceOwner("No device owner"));
            Assert.AreEqual("com.zebra.launcher/.Main", MasterProfile.ParseHomeActivity("priority=0 preferredOrder=0\ncom.zebra.launcher/.Main\n"));
            Assert.AreEqual("", MasterProfile.ParseHomeActivity("No activity found"));
        }

        [TestMethod]
        public void ShellQuote_EscapesApostrophes() =>
            Assert.AreEqual("'l'\\''écran'", MasterProfile.ShellQuote("l'écran"));

        [TestMethod]
        public void SaveThenOpen_RoundTrip()
        {
            string root = Path.Combine(Path.GetTempPath(), "adbt_master_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                string work = Path.Combine(root, "work");
                Directory.CreateDirectory(Path.Combine(work, "apks", "com.a"));
                File.WriteAllText(Path.Combine(work, "apks", "com.a", "base.apk"), "apk");
                var m = new MasterProfile { Name = "Test", SourceModel = "TC58" };
                m.Apps.Add(new MasterApp { Package = "com.a", Files = { "apks/com.a/base.apk" }, Permissions = { "android.permission.CAMERA" } });
                m.Settings.Add(new MasterSetting { Namespace = "system", Key = "screen_off_timeout", Value = "600000", Include = false });
                m.WifiNetworks.Add(new MasterWifi { Ssid = "Atelier", Security = "wpa2-psk", Password = "p'ss" });
                m.Launcher = new MasterItem { Name = "com.zebra.launcher/.Main" };
                m.Commands.Add("shell echo ok");
                string archive = Path.Combine(root, "t" + MasterProfile.FileExtension);
                m.SaveTo(work, archive);

                string work2 = Path.Combine(root, "work2");
                var o = MasterProfile.Open(archive, work2);
                Assert.AreEqual("Test", o.Name);
                Assert.AreEqual("android.permission.CAMERA", o.Apps.Single().Permissions.Single());
                Assert.IsFalse(o.Settings.Single().Include);
                Assert.AreEqual("p'ss", o.WifiNetworks.Single().Password);
                Assert.AreEqual("com.zebra.launcher/.Main", o.Launcher.Name);
                Assert.IsNull(o.DeviceOwner);
                Assert.AreEqual("shell echo ok", o.Commands.Single());
                Assert.IsTrue(File.Exists(Path.Combine(work2, "apks", "com.a", "base.apk")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidDataException))]
        public void Open_RejectsZipWithoutManifest()
        {
            string root = Path.Combine(Path.GetTempPath(), "adbt_master_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "x.txt"), "x");
            string zip = Path.Combine(root, "x.adbmaster");
            System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(root, "src"), zip);
            try { MasterProfile.Open(zip, Path.Combine(root, "w")); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
