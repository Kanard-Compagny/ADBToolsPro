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
        public void ParseAccounts_GoogleAndOthers_NoDuplicates()
        {
            const string d = "Accounts: 2\n    Account {name=atelier@exemple.fr, type=com.google}\n    Account {name=Agent, type=net.soti.account}\n" +
                             "  RegisteredServicesCache\n    Account {name=atelier@exemple.fr, type=com.google}\n";
            var r = MasterProfile.ParseAccounts(d);
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("atelier@exemple.fr", r[0].Key);
            Assert.AreEqual("com.google", r[0].Value);
            Assert.AreEqual(0, MasterProfile.ParseAccounts("Accounts: 0").Count);
        }

        [TestMethod]
        public void ParseSettingInput_ValidAndInvalid()
        {
            var s = MasterProfile.ParseSettingInput("  Global stay_on_while_plugged_in = 3 ");
            Assert.AreEqual("global", s.Namespace);
            Assert.AreEqual("stay_on_while_plugged_in", s.Key);
            Assert.AreEqual("3", s.Value.Trim());
            Assert.AreEqual("a=b", MasterProfile.ParseSettingInput("secure x=a=b").Value);
            Assert.IsNull(MasterProfile.ParseSettingInput("screen_off_timeout=1"));
            Assert.IsNull(MasterProfile.ParseSettingInput("vendor x=1"));
        }

        [TestMethod]
        public void StepScreens_AllHaveCommandExceptNone()
        {
            foreach (var sc in MasterProfile.StepScreens)
                Assert.AreEqual(sc[0].Length > 0, MasterProfile.StepScreenCommand(sc[0]).StartsWith("am start"), sc[0]);
            StringAssert.Contains(MasterProfile.StepScreenCommand("google"), "com.google");
        }

        [TestMethod]
        public void Catalog_KeysUnique_DefaultsValid_NeverFilteredOut()
        {
            Assert.AreEqual(MasterSettingsCatalog.All.Count, MasterSettingsCatalog.All.Select(d => d.Namespace + "|" + d.Key).Distinct().Count());
            foreach (var d in MasterSettingsCatalog.All)
            {
                Assert.IsFalse(MasterProfile.IsDeviceSpecificSetting(d.Key), d.Key);
                Assert.IsTrue(MasterSettingsCatalog.Themes.Contains(d.Theme), d.Key);
                if (d.Kind != MasterSettingKind.Text) Assert.IsTrue(MasterSettingsCatalog.IsValid(d, d.Default), d.Key);
                if (d.Kind == MasterSettingKind.Choice) Assert.IsTrue(d.Options.Any(o => o.Value == d.Default), d.Key);
            }
            Assert.AreEqual("Jamais", MasterSettingsCatalog.DisplayValue(MasterSettingsCatalog.Find("system", "screen_off_timeout"), "2147483647"));
            Assert.IsTrue(MasterSettingsCatalog.SameValue("1", "1.0"));
            Assert.IsFalse(MasterSettingsCatalog.IsValid(MasterSettingsCatalog.Find("system", "screen_brightness"), "300"));
        }

        [DataTestMethod]
        [DataRow("database_creation_buildid")]
        [DataRow("Phenotype_flags")]
        [DataRow("cert_pin_content_url")]
        [DataRow("rfid_reader_state")]
        [DataRow("display_default_density_value")]
        [DataRow("wifi_state_flag")]
        [DataRow("config_update_certificate")]
        [DataRow("job_scheduler_constants")]
        [DataRow("low_battery_sound")]
        public void TechnicalNoise_IsFilteredOut(string key) => Assert.IsTrue(MasterProfile.IsDeviceSpecificSetting(key));

        [TestMethod]
        public void Wifi_TransitionModeDuplicates_AreMerged()
        {
            const string o = "Network Id      SSID                         Security type\n" +
                             "0            Atelier                    wpa2-psk\n" +
                             "1            Atelier                    wpa3-sae^\n";
            var r = MasterProfile.ParseWifiNetworks(o);
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("wpa2-psk/wpa3-sae", r[0].Security);
            Assert.AreEqual("wpa2", MasterProfile.WifiConnectType(r[0].Security));
        }

        [TestMethod]
        public void Normalize_OldMaster_IsCleanedOnce()
        {
            var m = new MasterProfile();
            m.Settings.Add(new MasterSetting { Namespace = "system", Key = "screen_off_timeout", Value = "600000" });
            m.Settings.Add(new MasterSetting { Namespace = "global", Key = "database_creation_buildid", Value = "x" });
            m.Settings.Add(new MasterSetting { Namespace = "secure", Key = "qs_auto_tiles", Value = "cast" });
            m.WifiNetworks.Add(new MasterWifi { Ssid = "A", Security = "wpa2-psk" });
            m.WifiNetworks.Add(new MasterWifi { Ssid = "A", Security = "wpa3-sae^", Password = "p" });
            m.Steps.Add(new MasterStep { Title = "Recréer le compte work-1@android-for-work.gserviceaccount.com (com.google.work)" });
            m.Steps.Add(new MasterStep { Title = "Ajouter le compte Google a@b.fr" });
            m.Normalize();
            Assert.AreEqual(2, m.Settings.Count);
            Assert.IsTrue(m.Settings.Single(x => x.Key == "screen_off_timeout").Include);
            Assert.IsFalse(m.Settings.Single(x => x.Key == "qs_auto_tiles").Include);
            Assert.AreEqual("p", m.WifiNetworks.Single().Password);
            Assert.AreEqual(1, m.Steps.Count);
            m.Settings.Single(x => x.Key == "qs_auto_tiles").Include = true;
            m.Normalize(); // déjà au format courant : les choix ne sont plus touchés
            Assert.IsTrue(m.Settings.Single(x => x.Key == "qs_auto_tiles").Include);
            Assert.IsTrue(MasterProfile.IsMdmManagedAccount("x", "com.google.work"));
            Assert.AreEqual("SOTI MobiControl", MasterProfile.KnownAppName("net.soti.mobicontrol.androidwork/net.soti.mobicontrol.admin.DeviceAdminAdapter"));
        }

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
                m.WifiNetworks.Add(new MasterWifi { Ssid = "Atelier", Security = "wpa2-psk", Password = "p'ss", Hidden = true });
                m.Launcher = new MasterItem { Name = "com.zebra.launcher/.Main" };
                m.Commands.Add("shell echo ok");
                m.Steps.Add(new MasterStep { Title = "Ajouter le compte Google", Screen = "google", Include = false });
                string archive = Path.Combine(root, "t" + MasterProfile.FileExtension);
                m.SaveTo(work, archive);

                string work2 = Path.Combine(root, "work2");
                var o = MasterProfile.Open(archive, work2);
                Assert.AreEqual("Test", o.Name);
                Assert.AreEqual("android.permission.CAMERA", o.Apps.Single().Permissions.Single());
                Assert.IsFalse(o.Settings.Single().Include);
                Assert.AreEqual("p'ss", o.WifiNetworks.Single().Password);
                Assert.IsTrue(o.WifiNetworks.Single().Hidden);
                Assert.AreEqual("com.zebra.launcher/.Main", o.Launcher.Name);
                Assert.IsNull(o.DeviceOwner);
                Assert.AreEqual("shell echo ok", o.Commands.Single());
                Assert.AreEqual("google", o.Steps.Single().Screen);
                Assert.IsFalse(o.Steps.Single().Include);
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
