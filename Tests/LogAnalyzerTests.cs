using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class LogAnalyzerTests
    {
        private static LogAnalysis Run(string text) =>
            LogAnalyzer.Analyze(LogImport.ParseLines(text.Replace("\r", "").Split('\n'), false));

        private const string Crash =
@"11-05 10:00:00.100  4321  4321 D MyApp   : starting
11-05 10:00:01.000  4321  4321 E AndroidRuntime: FATAL EXCEPTION: main
11-05 10:00:01.000  4321  4321 E AndroidRuntime: Process: com.exemple.inventaire, PID: 4321
11-05 10:00:01.000  1000  1100 I ActivityManager: unrelated line in between
11-05 10:00:01.000  4321  4321 E AndroidRuntime: java.lang.NullPointerException: Attempt to invoke virtual method on a null object
11-05 10:00:01.000  4321  4321 E AndroidRuntime: 	at com.exemple.inventaire.MainActivity.onCreate(MainActivity.java:42)
11-05 10:00:01.000  4321  4321 E AndroidRuntime: Caused by: java.io.IOException: disk
11-05 10:05:00.000  4400  4400 E AndroidRuntime: FATAL EXCEPTION: main
11-05 10:05:00.000  4400  4400 E AndroidRuntime: Process: com.exemple.inventaire, PID: 4400
11-05 10:05:00.000  4400  4400 E AndroidRuntime: java.lang.NullPointerException: again";

        [TestMethod]
        public void Plantage_RegroupeParApplicationEtException()
        {
            var a = Run(Crash);
            var f = a.Findings.Single(x => x.Category == LogAnalyzer.CatCrash);
            Assert.AreEqual("com.exemple.inventaire", f.App);
            Assert.AreEqual(2, f.Count);
            Assert.AreEqual(FindingSeverity.Bad, f.Severity);
            StringAssert.Contains(f.Detail, "NullPointerException");
            StringAssert.Contains(f.Detail, "java.io.IOException: disk");
            Assert.AreEqual("10:00:01", LogFinding.ShortTime(f.FirstTime).Substring(6));
            Assert.IsTrue(f.Sample.Count >= 4);
            Assert.IsNotNull(f.FirstEntry);
        }

        [TestMethod]
        public void Anr_AvecRaison()
        {
            var a = Run(
@"11-05 10:00:00.000  1000  1200 E ActivityManager: ANR in com.exemple.scan (com.exemple.scan/.Main)
11-05 10:00:00.000  1000  1200 E ActivityManager: PID: 5555
11-05 10:00:00.000  1000  1200 E ActivityManager: Reason: Input dispatching timed out");
            var f = a.Findings.Single(x => x.App == "com.exemple.scan");
            StringAssert.Contains(f.Detail, "Input dispatching timed out");
            Assert.AreEqual(FindingSeverity.Bad, f.Severity);
        }

        [TestMethod]
        public void PlantageNatif_CompteUneSeuleFoisLibcEtDebug()
        {
            var a = Run(
@"11-05 10:00:00.000  6000  6010 F libc    : Fatal signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0 in tid 6010 (RenderThread), pid 6000 (com.exemple.photo)
11-05 10:00:00.200  7000  7000 F DEBUG   : *** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***
11-05 10:00:00.200  7000  7000 F DEBUG   : pid: 6000, tid: 6010, name: RenderThread  >>> com.exemple.photo <<<
11-05 10:00:00.200  7000  7000 F DEBUG   : signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0");
            var f = a.Findings.Single(x => x.Category == LogAnalyzer.CatCrash);
            Assert.AreEqual("com.exemple.photo", f.App);
            Assert.AreEqual(1, f.Count);
            StringAssert.Contains(f.Detail, "SIGSEGV");
        }

        [TestMethod]
        public void Wifi_DeconnexionsAuthentificationEtItinerance()
        {
            var a = Run(
@"11-05 10:00:00.000  2000  2000 D wpa_supplicant: wlan0: CTRL-EVENT-CONNECTED - Connection to aa:bb:cc:00:00:01 completed [id=0 id_str=]
11-05 10:01:00.000  2000  2000 D wpa_supplicant: wlan0: CTRL-EVENT-DISCONNECTED bssid=aa:bb:cc:00:00:01 reason=34
11-05 10:02:00.000  2000  2000 D wpa_supplicant: wlan0: CTRL-EVENT-CONNECTED - Connection to aa:bb:cc:00:00:02 completed [id=0 id_str=]
11-05 10:03:00.000  2000  2000 D wpa_supplicant: wlan0: CTRL-EVENT-DISCONNECTED bssid=aa:bb:cc:00:00:02 reason=34
11-05 10:04:00.000  2000  2000 D wpa_supplicant: wlan0: CTRL-EVENT-DISCONNECTED bssid=aa:bb:cc:00:00:02 reason=3 locally_generated=1
11-05 10:05:00.000  2000  2000 D wpa_supplicant: wlan0: CTRL-EVENT-SSID-TEMP-DISABLED id=0 ssid=""Entrepot"" auth_failures=1 duration=10 reason=WRONG_KEY");
            var disc34 = a.Findings.Single(x => x.Detail.StartsWith("Code 34"));
            Assert.AreEqual(2, disc34.Count);
            Assert.AreEqual(FindingSeverity.Warn, disc34.Severity, "3 déconnexions ou plus : à surveiller");
            Assert.IsTrue(a.Findings.Any(x => x.Detail == "Mot de passe Wi-Fi refusé" && x.Severity == FindingSeverity.Bad));
            var conn = a.Findings.Single(x => x.Title.StartsWith("Itinérance"));
            StringAssert.Contains(conn.Detail, "2 connexion(s) vers 2 borne(s)");
        }

        [TestMethod]
        public void Reseau_DnsEtServeurInjoignable()
        {
            var a = Run(
@"11-05 10:00:00.000  3000  3000 W System.err: java.net.UnknownHostException: Unable to resolve host ""api.exemple.fr"": No address associated with hostname
11-05 10:00:00.000  3000  3000 W System.err: 	at java.net.Inet6AddressImpl.lookupHostByName(Inet6AddressImpl.java:156)
11-05 10:00:09.000  3000  3000 W System.err: java.net.ConnectException: failed to connect to /192.168.1.10 (port 8080) from /192.168.1.50 (port 40000) after 10000ms");
            Assert.AreEqual("api.exemple.fr", a.Findings.Single(x => x.Title.Contains("DNS")).App);
            Assert.AreEqual("/192.168.1.10:8080", a.Findings.Single(x => x.Title.StartsWith("Serveur injoignable")).App);
        }

        [TestMethod]
        public void Systeme_MemoireBatterieStockage()
        {
            var lines = string.Join("\n", Enumerable.Range(0, 6).Select(i =>
                $"11-05 10:0{i}:00.000   500   500 I lowmemorykiller: Kill 'com.exemple.app{i % 2}' (1234), uid 10100, oom_score_adj 900 to free 50000kB"));
            var a = Run(lines + "\n" +
@"11-05 10:10:00.000   400   400 I healthd : battery l=4 v=3500 t=38.5 h=2 st=3 c=-500 fc=3000000 cc=12 chg=
11-05 10:11:00.000   400   400 I healthd : battery l=3 v=3450 t=39.0 h=2 st=3 c=-500 fc=3000000 cc=12 chg=
11-05 10:12:00.000  3000  3000 E SQLiteLog: (13) statement aborts: database or disk is full, No space left on device");
            var lmk = a.Findings.Single(x => x.Title.StartsWith("Mémoire saturée"));
            Assert.AreEqual(6, lmk.Count);
            Assert.AreEqual(FindingSeverity.Warn, lmk.Severity);
            StringAssert.Contains(lmk.Detail, "com.exemple.app0 (3)");
            var batt = a.Findings.Single(x => x.Title == "Batterie presque vide");
            StringAssert.Contains(batt.Detail, "3 %");
            Assert.IsTrue(a.Findings.Any(x => x.Title.StartsWith("Stockage plein") && x.Severity == FindingSeverity.Bad));
        }

        [TestMethod]
        public void Scanner_ToutesMarquesMaisPasLeWifi()
        {
            var a = Run(
@"11-05 10:00:00.000  2500  2500 E DataWedge: Profile 'Inventaire' not found for com.exemple.inventaire
11-05 10:00:05.000  2500  2500 E DataWedge: Profile 'Inventaire' not found for com.exemple.inventaire
11-05 10:00:06.000  2600  2600 W AidcManager: Scanner claim failed, retry 2
11-05 10:00:07.000  2700  2700 W WifiScanner: scan failed
11-05 10:00:08.000  2700  2700 E MediaScannerService: error");
            var scan = a.Findings.Where(x => x.Category == LogAnalyzer.CatScanner).ToList();
            Assert.AreEqual(2, scan.Count);
            Assert.AreEqual(2, scan.Single(x => x.App == "DataWedge").Count);
            Assert.AreEqual(FindingSeverity.Warn, scan.Single(x => x.App == "AidcManager").Severity);
        }

        [TestMethod]
        public void Tri_GraveEnPremier_EtRapportTexte()
        {
            var a = Run(Crash + "\n11-05 10:06:00.000  2000  2000 D wpa_supplicant: wlan0: CTRL-EVENT-DISCONNECTED bssid=aa:bb:cc:00:00:01 reason=3");
            Assert.AreEqual(FindingSeverity.Bad, a.Findings[0].Severity);
            string txt = a.ToText();
            StringAssert.Contains(txt, "com.exemple.inventaire");
            StringAssert.Contains(txt, "PLANTAGES D'APPLICATIONS");
            StringAssert.Contains(a.Verdict, "1 problème(s) important(s)");
        }

        [TestMethod]
        public void Aucun_Probleme()
        {
            var a = Run("11-05 10:00:00.000  1000  1000 I ActivityManager: Start proc com.a");
            Assert.AreEqual(0, a.BadCount + a.WarnCount);
            Assert.AreEqual("Aucun problème détecté dans ces logs.", a.Verdict);
        }

        [TestMethod]
        public void Import_FormatsTimeBriefEtAnnee()
        {
            var t = LogImport.ParseAny("11-05 10:00:00.123 E/AndroidRuntime( 4321): FATAL EXCEPTION: main");
            Assert.AreEqual("AndroidRuntime", t.Tag); Assert.AreEqual("4321", t.PID); Assert.AreEqual("E", t.Level);
            var b = LogImport.ParseAny("W/DataWedge( 2500): Profile not found");
            Assert.AreEqual("DataWedge", b.Tag); Assert.AreEqual("W", b.Level);
            var y = LogImport.ParseAny("2026-11-05 10:00:00.123456  1000  1200  1300 F libc: Fatal signal 6 (SIGABRT)");
            Assert.AreEqual("11-05 10:00:00.123", y.Time); Assert.AreEqual("F", y.Level); Assert.AreEqual("libc", y.Tag);
        }

        [TestMethod]
        public void Import_BugreportZip_GardeSeulementLesLignesDeLog()
        {
            string dir = Path.Combine(Path.GetTempPath(), "adbt_br_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string zipPath = Path.Combine(dir, "bugreport.zip");
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("bugreport-TC52-2026-11-05.txt");
                    using (var w = new StreamWriter(entry.Open()))
                    {
                        w.WriteLine("========================================================");
                        w.WriteLine("== dumpstate: 2026-11-05 10:00:00");
                        w.WriteLine("------ SYSTEM LOG (logcat -v threadtime -v printable -v uid -d *:v) ------");
                        w.WriteLine("11-05 10:00:01.000  1000  4321  4321 E AndroidRuntime: FATAL EXCEPTION: main");
                        w.WriteLine("11-05 10:00:01.000  1000  4321  4321 E AndroidRuntime: Process: com.exemple.br, PID: 4321");
                        w.WriteLine("mWakefulness=Awake");
                    }
                    zip.CreateEntry("FS/data/anr/traces.txt");
                }
                var list = LogImport.LoadFile(zipPath);
                Assert.AreEqual(2, list.Count);
                var a = LogAnalyzer.Analyze(list);
                Assert.AreEqual("com.exemple.br", a.Findings.Single(x => x.Category == LogAnalyzer.CatCrash).App);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void Scanner_PasDeFauxPositifsDuSysteme()
        {
            var a = Run(
@"11-05 16:45:01.000  1684  1700 W ProcessStats: Tracking association SourceState{3def2ce com.symbol.datawedge:zdcpservice/10102 Fgs #30916} whose proc state 3 is better than process ProcessState{1ffc5c3 com.symbol.datawedge:hwinfoservice/10102 pkg=com.symbol.datawedge} proc state 5 (2 skipped)
11-05 16:45:12.000  5000  5000 W ndroid.settings: ApkAssets: Deleting an ApkAssets object '<empty>' and /system/priv-app/com.symbol.datawedge/com.symbol.datawedge.apk with 1 weak references
11-05 16:45:12.000  2500  2500 W ScannerFramework: [StateMachine::MoveState] Transition from state 'Disabled' to 'Disabled' is invalid
11-05 16:45:13.000  2500  2500 W ScannerFramework: Scanner state changed to Idle
11-05 16:45:14.000  6000  6000 E MonAppli: Barcode scanner error: unable to open the scanner");
            var scan = a.Findings.Where(x => x.Category == LogAnalyzer.CatScanner).ToList();
            Assert.IsFalse(scan.Any(x => x.App == "ProcessStats" || x.App == "ndroid.settings"));
            Assert.AreEqual(FindingSeverity.Warn, scan.Single(x => x.Detail.Contains("is invalid")).Severity);
            Assert.AreEqual(FindingSeverity.Info, scan.Single(x => x.Detail.Contains("Idle")).Severity);
            Assert.AreEqual(FindingSeverity.Bad, scan.Single(x => x.App == "MonAppli").Severity);
        }

        [TestMethod]
        public void Scanner_TracesZebraNiveauENeSontPasDesErreurs()
        {
            var a = Run(
@"11-06 10:13:55.000  2500  2500 E ScannerFramework: IMGKIT [ImgKitCWrapper::AttemptDecode] - 0 Time to decode: 20.04ms
11-06 10:12:21.000  2500  2500 E ScannerFramework: [ScannerContext::UpdateTimer] Updating No decode timer to 180000
11-06 10:13:56.000  2500  2500 E ScannerFramework: IMGKIT [ImgKitCWrapper::QueryData] Size of data - iIndex = 0, Size - 17
11-06 10:12:21.000  2500  2500 E ScannerFramework: [ImagerControl::setInitParameters] ORIENTATION command SUCCESS
11-06 10:12:31.000  2600  2600 E ScannerSDK: [InfoProxy::ConnectThreadProc::runInternal] Failed to connect to com.symbol.scanning.config");
            var scan = a.Findings.Where(x => x.Category == LogAnalyzer.CatScanner).ToList();
            Assert.AreEqual(1, scan.Count);
            Assert.AreEqual("ScannerSDK", scan[0].App);
        }

        [TestMethod]
        public void Scanner_VraiePanneToujoursSignalee()
        {
            var a = Run(
@"11-06 10:20:00.000  2500  2500 E ScannerFramework: IMGKIT [ImgKitCWrapper::AttemptDecode] - 0 Time to decode: 20.04ms
11-06 10:20:01.000  2500  2500 E ScannerFramework: [ScannerContext::Enable] Imager not responding, aborting
11-06 10:20:02.000  2500  2500 E ScannerFramework: [ImagerControl::Open] camera device is already in use
11-06 10:20:03.000  2500  2500 W ScannerFramework: Scan engine reset requested
11-06 10:20:04.000  2700  2700 E DataWedge: Scanner initialization failed");
            var scan = a.Findings.Where(x => x.Category == LogAnalyzer.CatScanner).ToList();
            Assert.AreEqual(4, scan.Count);
            Assert.IsFalse(scan.Any(x => x.Detail.Contains("Time to decode")));
            Assert.AreEqual(FindingSeverity.Warn, scan.Single(x => x.Detail.Contains("reset")).Severity);
            Assert.AreEqual(3, scan.Count(x => x.Severity == FindingSeverity.Bad));
        }

        [TestMethod]
        public void Cible_ConcerneLApplicationEtSesProcessus()
        {
            var a = Run(Crash + "\n" +
@"11-05 10:06:00.000  1000  1200 E ActivityManager: ANR in com.exemple.inventaire:sync (com.exemple.inventaire/.SyncService)
11-05 10:06:00.000  1000  1200 E ActivityManager: Reason: executing service
11-05 10:07:00.000  7777  7777 E AndroidRuntime: FATAL EXCEPTION: main
11-05 10:07:00.000  7777  7777 E AndroidRuntime: Process: com.autre.appli, PID: 7777
11-05 10:07:00.000  7777  7777 E AndroidRuntime: java.lang.IllegalStateException: boom
11-05 10:08:00.000  4400  4400 W System.err: java.net.UnknownHostException: Unable to resolve host ""api.exemple.fr"": No address");
            var mine = a.Findings.Where(f => f.ConcernsApp("com.exemple.inventaire", "4400")).ToList();
            Assert.AreEqual(3, mine.Count, string.Join(" | ", mine.Select(f => f.Title + " " + f.App)));
            Assert.IsFalse(a.Findings.Single(f => f.App == "com.autre.appli").ConcernsApp("com.exemple.inventaire", "4400"));
            var f0 = mine[0]; f0.IsTarget = true;
            StringAssert.StartsWith(f0.DisplayTitle, "🎯 ");
        }
    }
}
