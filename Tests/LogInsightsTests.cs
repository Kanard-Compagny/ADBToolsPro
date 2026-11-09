using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class LogInsightsTests
    {
        private static LogArchive Open(string dir, params string[] lines)
        {
            File.WriteAllLines(Path.Combine(dir, "logcat.txt"), lines);
            return LogArchive.Open(dir);
        }

        private static string Dir() { string d = Path.Combine(Path.GetTempPath(), "adbt_ins_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

        [TestMethod]
        public void AvantIncident_NouveauxMessagesPicsEtEvenements()
        {
            string dir = Dir();
            try
            {
                var lines = new List<string>();
                // 08:00 → 08:59 : une erreur de routine toutes les minutes.
                for (int m = 0; m < 60; m++) lines.Add($"11-05 08:{m:00}:00.000   100   100 E Routine: Polling timeout {m}");
                // 09:00 → 09:07 : la même erreur s'emballe, un message nouveau, une coupure Wi-Fi.
                for (int s = 0; s < 84; s++) lines.Add($"11-05 09:0{s / 12}:{(s % 12) * 5:00}.000   100   100 E Routine: Polling timeout {s}");
                lines.Add("11-05 09:05:00.000   200   200 E ScannerFramework: [ImagerAdapter::getFrameThreadProc] More than 10 Null frame received");
                lines.Add("11-05 09:06:00.000   300   300 I wpa_supplicant: wlan0: CTRL-EVENT-DISCONNECTED bssid=aa:bb:cc:dd:ee:ff reason=3");
                lines.Sort(StringComparer.Ordinal);
                var a = Open(dir, lines.ToArray());

                var rows = LogInsights.BeforeIncident(a, LogArchive.ToMinute("11-05 09:07:00.000"), 10);
                Assert.IsTrue(rows.Any(r => r.Kind == "event" && r.Tag == "wpa_supplicant"));
                Assert.IsTrue(rows.Any(r => r.Kind == "new" && r.Tag == "ScannerFramework"));
                Assert.IsTrue(rows.Any(r => r.Kind == "spike" && r.Tag == "Routine"));
                Assert.AreEqual(2, a.Entries.Single(e => e.Tag == "wpa_supplicant").Lane);
                Assert.AreEqual(5, a.Entries.Single(e => e.Tag == "ScannerFramework").Lane);

                // Tendance : l'erreur de routine est régulière puis s'emballe.
                var analysis = LogAnalyzer.Analyze(a.Entries.Cast<LogEntry>().ToList());
                var f = new LogFinding { FirstEntry = a.Entries.First(e => e.Tag == "Routine") };
                LogInsights.ApplyTrends(new[] { f }, LogInsights.BuildTrends(a, 12));
                StringAssert.StartsWith(f.TrendPath, "M0,");
                StringAssert.Contains(f.TrendText, "En hausse");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void VieDeLAppli_DemarragesArretsEtCauses()
        {
            string dir = Dir();
            try
            {
                var a = Open(dir,
                    "11-05 08:00:00.000  1000  1100 I am_proc_start: [0,4321,10123,com.client.wms,activity,{com.client.wms/.Main}]",
                    "11-05 08:00:00.100  1000  1100 I ActivityManager: Start proc 4321:com.client.wms/u0a123 for activity {com.client.wms/.Main}",
                    "11-05 08:30:00.000  1000  1100 I am_kill: [0,4321,com.client.wms,900,lmk]",
                    "11-05 08:30:00.200   400   400 I lmkd: Kill 'com.client.wms' (4321), uid 10123, oom_score_adj 900 to free 50000kB",
                    "11-05 08:31:00.000  1000  1100 I am_proc_start: [0,5555,10123,com.client.wms,activity,{com.client.wms/.Main}]",
                    "11-05 08:40:00.000  5555  5555 E AndroidRuntime: FATAL EXCEPTION: main",
                    "11-05 08:40:00.001  5555  5555 E AndroidRuntime: Process: com.client.wms, PID: 5555",
                    "11-05 08:41:00.000  1000  1100 I am_proc_start: [0,6666,10200,com.android.settings,activity,{x}]");
                var apps = LogInsights.StartedApps(a);
                Assert.AreEqual("com.client.wms", apps[0].Key);
                var life = LogInsights.Lifecycle(a, "com.client.wms");
                Assert.AreEqual(2, life.Starts);
                Assert.AreEqual(1, life.MemoryKills);  // même arrêt écrit deux fois (am_kill + lmkd) : compté une fois
                Assert.AreEqual(1, life.Crashes);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void Sessions_RedemarrageInattenduEtArretPropre()
        {
            string dir = Dir();
            try
            {
                var lines = new List<string> { "11-05 08:00:00.000   100   100 I Debut: relevé" };
                for (int i = 0; i < 30; i++) lines.Add($"11-05 08:{10 + i:00}:00.000   100   100 I App: ligne {i}");
                lines.Add("11-05 08:45:00.000  1500  1500 I SystemServer: Entered the Android system server!");   // sans arrêt propre
                for (int i = 0; i < 30; i++) lines.Add($"11-05 09:{i:00}:00.000   100   100 I App: ligne {i}");
                lines.Add("11-05 09:40:00.000  1500  1600 I ShutdownThread: Rebooting, reason: userrequested");
                lines.Add("11-05 09:42:00.000  1500  1500 I SystemServer: Entered the Android system server!");
                for (int i = 0; i < 30; i++) lines.Add($"11-05 10:{i:00}:00.000   100   100 I App: ligne {i}");
                var a = Open(dir, lines.ToArray());
                var s = LogInsights.Sessions(a);
                Assert.AreEqual(3, s.Count);
                Assert.IsTrue(s[0].Unexpected);
                Assert.IsFalse(s[1].Unexpected);
                StringAssert.Contains(s[1].EndCause, "userrequested");
                Assert.AreEqual(1, LogInsights.SessionFindings(s).Count);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void Sessions_RaisonAndroidEtDoublonsEtBruitAuDemarrage()
        {
            string dir = Dir();
            try
            {
                var lines = new List<string>();
                for (int i = 0; i < 30; i++) lines.Add($"11-05 07:{i:00}:00.000   100   100 I App: ligne {i}");
                // Deux marqueurs du même démarrage, à 3 s d'écart, puis la raison donnée par bootstat.
                lines.Add("11-05 08:25:56.771  1191  1191 I boot_progress_start: 12666");
                lines.Add("11-05 08:25:56.998  1541  1541 I bootstat: Canonical boot reason: shutdown,userrequested");
                lines.Add("11-05 08:25:59.668  2154  2154 I SystemServer: Entered the Android system server!");
                // Bruit du lecteur pendant le démarrage des services.
                for (int i = 0; i < 6; i++) lines.Add($"11-05 08:26:3{i}.000  5794  5794 E ScannerFramework: [Module{(char)('A' + i)}::Init] Unable to set scheduling parameters");
                for (int i = 0; i < 30; i++) lines.Add($"11-05 09:{i:00}:00.000   100   100 I App: ligne {i}");
                var a = Open(dir, lines.ToArray());

                var s = LogInsights.Sessions(a);
                Assert.AreEqual(2, s.Count, "un démarrage noté deux fois ne fait qu'une session");
                Assert.IsFalse(s[0].Unexpected);
                StringAssert.Contains(s[0].EndCause, "Arrêt demandé par l'utilisateur");
                StringAssert.Contains(s[1].StartReason, "shutdown,userrequested");

                var analysis = LogAnalyzer.Analyze(a.Entries.Cast<LogEntry>().ToList());
                Assert.IsTrue(analysis.Findings.Count(f => f.Category == LogAnalyzer.CatScanner) >= 2);
                var folded = LogInsights.FoldBootNoise(analysis.Findings.ToList(), s);
                Assert.AreEqual(0, folded.Count(f => f.Category == LogAnalyzer.CatScanner));
                Assert.AreEqual(FindingSeverity.Info, folded.Single(f => f.Title.Contains("démarrage")).Severity);

                // Rapport client : les constats du même genre forment une ligne.
                var many = Enumerable.Range(0, 5).Select(i => new LogFinding { Title = "Erreur du lecteur code-barres", Severity = FindingSeverity.Bad, Category = LogAnalyzer.CatScanner, Detail = "msg " + i, Count = 2 }).ToList();
                var grouped = LogWordReport.GroupForCustomer(many);
                Assert.AreEqual(1, grouped.Count);
                Assert.AreEqual(10, grouped[0].Count);
                StringAssert.Contains(grouped[0].Detail, "5 messages différents");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void RaisonDeDemarrage_EnClair()
        {
            Assert.IsFalse(LogInsights.DescribeBootReason("reboot,ota").Unexpected);
            StringAssert.Contains(LogInsights.DescribeBootReason("reboot,ota").Text, "OTA");
            Assert.IsTrue(LogInsights.DescribeBootReason("kernel_panic").Unexpected);
            Assert.IsTrue(LogInsights.DescribeBootReason("reboot,longkey").Unexpected);
            Assert.IsFalse(LogInsights.DescribeBootReason("reboot,userrequested").Unexpected);
            Assert.IsTrue(LogInsights.DescribeBootReason("").Unexpected);
        }

        [TestMethod]
        public void Annexes_ResumeAnrEtTombstone()
        {
            var anr = LogInsights.Summarize(ArchiveFileKind.Anr,
                "Subject: Input dispatching timed out (Waiting to send key event)\n\n----- pid 1234 at 2026-11-05 09:07:12.345 -----\nCmd line: com.client.wms\n\n\"main\" prio=5 tid=1 Blocked\n  | group=\"main\"\n  at com.client.wms.Sync.run(Sync.java:42)\n  - waiting to lock <0x0a1b> (a java.lang.Object) held by thread 23\n  at android.os.Handler.dispatchMessage(Handler.java:106)\n\n");
            Assert.AreEqual("com.client.wms", anr.Package);
            Assert.AreEqual(LogArchive.ToMinute("11-05 09:07:00.000"), anr.Minute);
            StringAssert.Contains(anr.Text, "Blocked");
            StringAssert.Contains(anr.Text, "Sync.java:42");
            StringAssert.Contains(anr.Text, "Input dispatching timed out");

            var tomb = LogInsights.Summarize(ArchiveFileKind.Tombstone,
                "Timestamp: 2026-11-05 09:10:00+0100\npid: 4321, tid: 4321, name: main  >>> com.client.wms <<<\nsignal 6 (SIGABRT), code -6 (SI_TKILL), fault addr --------\nAbort message: 'boom'\nbacktrace:\n      #00 pc 000000000004e1b8  /apex/com.android.runtime/lib64/bionic/libc.so (abort+168)\n");
            StringAssert.Contains(tomb.Title, "SIGABRT");
            StringAssert.Contains(tomb.Text, "boom");
            StringAssert.Contains(tomb.Text, "abort+168");
        }

        [TestMethod]
        public void PannesConnuesEtSignets_Enregistres()
        {
            string dir = Dir();
            try
            {
                var a = Open(dir,
                    "11-05 09:05:00.000   200   200 E ScannerFramework: [ImagerAdapter::getFrameThreadProc] More than 10 Null frame received",
                    "11-05 09:06:00.000   200   200 E ScannerFramework: [ImagerAdapter::getFrameThreadProc] More than 12 Null frame received",
                    "11-05 09:07:00.000   300   300 I Autre: rien");
                string pattern = LogInsights.SuggestPattern(a.Entries[0].Message);
                var sig = new LogInsights.Signature { Title = "Moteur SE5500 défaillant", Tag = "ScannerFramework", Pattern = pattern, Diagnosis = "Nappe", Solution = "Retour SAV" };
                string file = Path.Combine(dir, "sigs.json");
                LogInsights.SaveSignatures(file, new[] { sig });
                var loaded = LogInsights.LoadSignatures(file, Path.Combine(dir, "absent.json"));
                var found = LogInsights.MatchSignatures(a.Entries, loaded);
                Assert.AreEqual(1, found.Count);
                Assert.AreEqual(2, found[0].Count);
                StringAssert.Contains(found[0].Title, "SE5500");

                string bmFile = LogInsights.BookmarkFile(dir, a.Path);
                LogInsights.SaveBookmarks(bmFile, new[] { new LogInsights.Bookmark { Time = a.Entries[1].Time, Tag = a.Entries[1].Tag, Message = a.Entries[1].Message, Note = "ici", Index = 1 } });
                var bms = LogInsights.LoadBookmarks(bmFile, a);
                Assert.AreSame(a.Entries[1], bms.Single().Entry);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
