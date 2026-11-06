using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class DiagnosticTests
    {
        [TestMethod]
        public void Thermique_ZonesIllisiblesIgnoreesSansDecalage()
        {
            var (types, temps) = DiagnosticService.SplitThermalZoneLines("cpu0|45000\nsoc|\ngpu|38000\n|12\n");

            CollectionAssert.AreEqual(new[] { "cpu0", "gpu" }, types);
            CollectionAssert.AreEqual(new[] { "45000", "38000" }, temps);
        }

        [TestMethod]
        public void Thermique_ZoneSocExclue()
        {
            var r = DiagnosticService.ParseThermalCheck("soc\ncpu0", "100000\n42000");
            StringAssert.Contains(r.Details, "cpu0");
            Assert.IsFalse(r.Details.Contains("soc"));
        }

        [TestMethod]
        public void Wifi_SsidNonConfonduAvecBssid()
        {
            var r = DiagnosticService.ParseWifiCheck("mWifiInfo BSSID: aa:bb:cc:dd:ee:ff, SSID: \"Entrepot\", RSSI: -60", "inet 10.0.0.5/24", "10.0.0.1", "");
            StringAssert.Contains(r.Details, "Réseau (SSID) : Entrepot");
            Assert.AreEqual("✅", r.StatusIcon);
        }

        [TestMethod]
        public void Wifi_Android11_SsidEtBssidDeLaConnexionEnCours()
        {
            string dump = "Supplicant state\n  SSID: DeploiTest BSSID: 00:00:00:00:00:00 nid: 1 state: ASSOCIATED\n"
                        + "mWifiInfo SSID: \"DeploiTest\", BSSID: 18:49:f8:84:2b:e4, MAC: 02:00:00:00:00:00, Supplicant state: COMPLETED, RSSI: -52, Link speed: 573Mbps, Frequency: 5500MHz\n";
            var r = DiagnosticService.ParseWifiCheck(dump, "inet 172.17.10.26/23\n link/ether a2:40:87:48:fa:91", "", "");
            StringAssert.Contains(r.Details, "Réseau (SSID) : DeploiTest\n".Replace("\n", System.Environment.NewLine));
            StringAssert.Contains(r.Details, "Point d'accès (BSSID) : 18:49:f8:84:2b:e4");
            StringAssert.Contains(r.Details, "Puissance Signal (RSSI) : -52 dBm");

            // Sans ligne mWifiInfo : SSID coupé avant "BSSID:", BSSID nul ignoré.
            Assert.AreEqual("DeploiTest", WifiParsingHelpers.ExtractSsid("SSID: DeploiTest BSSID: 00:00:00:00:00:00 nid: 1"));
            Assert.IsNull(WifiParsingHelpers.ExtractBssid("BSSID: 00:00:00:00:00:00 nid: 1"));
            Assert.IsTrue(ScanTriggerKeys.IsScanTrigger("BTN_TL"));
            Assert.IsFalse(ScanTriggerKeys.IsScanTrigger("KEY_VOLUMEUP"));
        }

        [TestMethod]
        public void Batterie_LibellesEtStatut()
        {
            Assert.AreEqual("Défaillance (Failure) ❌", BatteryLabels.Health(6));
            Assert.AreEqual("Inconnue", BatteryLabels.Health(1));
            Assert.AreEqual("⚠️", DiagnosticService.ParseBatteryCheck("level: 80\nhealth: 7\nstatus: 3").StatusIcon);
            Assert.AreEqual("✅", DiagnosticService.ParseBatteryCheck("level: 80\nhealth: 2\nstatus: 2").StatusIcon);
        }

        [TestMethod]
        public void Stockage_ValeursDf()
        {
            var v = DiagnosticService.ParseStorageValues("Filesystem Size Used Avail Use% Mounted on\n/dev/block/dm-5 53G 21G 32G 40% /data\n");
            Assert.IsTrue(v.HasValue);
            Assert.AreEqual("53G", v.Value.Total);
            Assert.AreEqual(40, v.Value.UsedPercent);
        }

        [TestMethod]
        public void Imei_ServiceCall()
        {
            string output = "Result: Parcel(\n  0x00000000: 00000000 0000000f 00350033 00300039 '........3.5.0.9.'\n  0x00000010: 00350031 00380030 00330030 00300039 '1.5.0.8.0.3.9.0.'\n  0x00000020: 00330034 00000035                   '4.3.5...        ')";
            Assert.AreEqual("350915080390435", DiagnosticService.ParseServiceCallImei(output));
        }

        [TestMethod]
        public void Crashs_FiltreAppCrashEtAnr()
        {
            var r = DiagnosticService.ParseCrashHistory("2026-10-08 data_app_crash (text, 1200 bytes)\n2026-10-08 SYSTEM_BOOT\n2026-10-08 data_app_anr (text)\n");
            StringAssert.Contains(r.Details, "2 ÉVÉNEMENT(S)");
            Assert.AreEqual("⚠️", r.StatusIcon);
        }

        [TestMethod]
        public void RapportRma_JsonEchappe()
        {
            string json = DiagnosticService.BuildRmaReportJson("1.2.3.4:5555", new[]
            {
                new DiagComponent { SystemId = "crashes", ComponentName = "Crash \"x\"", StatusIcon = "⚠️", Details = "C:\\chemin\ttab\nligne" }
            });

            StringAssert.Contains(json, "\"Nom\": \"Crash \\\"x\\\"\"");
            StringAssert.Contains(json, "C:\\\\chemin\\ttab\\nligne");
        }

        [TestMethod]
        public void GetEvent_ToucheEtFinDeTrame()
        {
            var key = GetEventParser.Parse("/dev/input/event0: EV_KEY       KEY_VOLUMEUP         DOWN");
            Assert.AreEqual(GetEventKind.KeyPress, key.Kind);
            Assert.AreEqual("VOLUMEUP", key.DisplayKeyName);

            Assert.AreEqual(GetEventKind.SyncReport, GetEventParser.Parse("/dev/input/event1: EV_SYN       SYN_REPORT           00000000").Kind);
            Assert.AreEqual(GetEventKind.None, GetEventParser.Parse("/dev/input/event1: EV_KEY       BTN_TOUCH            DOWN").Kind);
        }

        [TestMethod]
        public void Ecran_TailleEtDensite()
        {
            Assert.AreEqual((720, 1280), DeviceDisplayInfo.ParseScreenSize("Physical size: 1080x1920\nOverride size: 720x1280"));
            Assert.AreEqual((1080, 1920), DeviceDisplayInfo.ParseScreenSize("format inattendu"));
            Assert.AreEqual((420, true), DeviceDisplayInfo.ParseDensity("Physical density: 480\nOverride density: 420").Value);
        }

        [TestMethod]
        public void Modele_MarqueEtModeleCommunsAuxRapports()
        {
            Assert.AreEqual("Zebra TC53", DiagnosticService.FormatBrandModel("zebra", "TC53"));
            Assert.AreEqual("Samsung SM-A515F", DiagnosticService.FormatBrandModel("samsung", "Samsung SM-A515F"));
            Assert.AreEqual("TC53", DiagnosticService.FormatBrandModel("", "TC53"));
            Assert.AreEqual("", DiagnosticService.FormatBrandModel(null, null));
        }

        [TestMethod]
        public void Tactile_GrilleEtCalibration()
        {
            var grid = new TouchZoneGrid(6, 12);
            Assert.IsTrue(grid.RecordTouch(0.99, 0.99, out int row, out int col));
            Assert.AreEqual(11, row);
            Assert.AreEqual(5, col);
            Assert.IsFalse(grid.RecordTouch(0.99, 0.99, out _, out _));

            var cal = TouchCalibration.Parse("ABS_MT_POSITION_X     : value 0, min 0, max 4095\nABS_MT_POSITION_Y     : value 0, min 0, max 4095");
            Assert.AreEqual((0, 4095, 0, 4095), cal.Value);
        }
    }
}
