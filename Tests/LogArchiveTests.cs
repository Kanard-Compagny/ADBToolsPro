using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class LogArchiveTests
    {
        private static string MakeRx(string dir)
        {
            string zipPath = Path.Combine(dir, "RxLogger.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                void Write(string name, params string[] lines)
                {
                    using (var w = new StreamWriter(zip.CreateEntry("RxLogger/" + name).Open()))
                        foreach (var l in lines) w.WriteLine(l);
                }
                Write("Main1.txt", "DEVICE: Zebra/TC53E/TC53E SERIAL NUMBER: 25336524211469 ANDROID VERSION: 14 BUILD ID: 14-15-22.00-UG-U149 Incremental: 369",
                      "11-05 08:00:00.000   100   100 I Ancien: avant",
                      "11-05 09:06:30.000   100   100 E ScannerFramework: More than 10 Null frame received");
                Write("Main0.txt", "DEVICE: Zebra/TC53E/TC53E SERIAL NUMBER: 25336524211469 ANDROID VERSION: 14 BUILD ID: 14-15-22.00-UG-U149 Incremental: 369",
                      "11-05 09:07:00.000   200   200 W Recent: incident ici",
                      "11-05 09:30:00.000   200   200 I Recent: après");
                Write("Crash0.txt", "11-05 09:07:10.000   300   300 E AndroidRuntime: FATAL EXCEPTION: main");
                Write("KLog0.txt", "<6>[55496.959716][ T7569] noyau");
                Write("anr/anr_2026-11-05-09-07-12", "----- pid 1234 at 2026-11-05 09:07:12 -----", "Cmd line: com.exemple");
                Write("snapshots/snapshot0.txt", "[ro.product.model]: [TC53E]", "[ro.build.version.security_patch]: [2026-09-01]");
            }
            return zipPath;
        }

        [TestMethod]
        public void RxLogger_FusionChronologiqueEtAnnexes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "adbt_arch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var a = LogArchive.Open(MakeRx(dir));
                CollectionAssert.AreEqual(new[] { "Ancien", "ScannerFramework", "Recent", "AndroidRuntime", "Recent" }, a.Entries.Select(e => e.Tag).ToArray());
                Assert.AreEqual("Crash0.txt", a.Entries[3].Source);
                Assert.AreEqual(3, a.LogFiles.Count());
                Assert.IsTrue(a.OtherFiles.Any(f => f.Kind == ArchiveFileKind.Anr));
                Assert.IsTrue(a.OtherFiles.Any(f => f.Kind == ArchiveFileKind.Snapshot));
                Assert.IsTrue(a.OtherFiles.Any(f => f.Name == "KLog0.txt" && f.Kind == ArchiveFileKind.Text));
                Assert.AreEqual("Zebra TC53E", a.Device.Single(p => p.Key == "Terminal").Value);
                Assert.AreEqual("2026-09-01", a.Device.Single(p => p.Key == "Correctif de sécurité").Value);
                StringAssert.Contains(a.ReadText(a.OtherFiles.First(f => f.Kind == ArchiveFileKind.Anr)), "com.exemple");

                // Aller à l'incident : 9h07 ± 1 min.
                int t = a.ParseIncidentTime("9h07");
                Assert.AreEqual(LogArchive.ToMinute("11-05 09:07:00.000"), t);
                var win = a.Filter(new ArchiveFilter { FromMinute = t - 1, ToMinute = t + 1 });
                CollectionAssert.AreEqual(new[] { "ScannerFramework", "Recent", "AndroidRuntime" }, win.Select(e => e.Tag).ToArray());

                // Filtres combinés.
                Assert.AreEqual(1, a.Filter(new ArchiveFilter { Text = "null FRAME" }).Count);
                Assert.AreEqual(2, a.Filter(new ArchiveFilter { Levels = new System.Collections.Generic.HashSet<string> { "E" } }).Count);
                Assert.AreEqual(2, a.Filter(new ArchiveFilter { Text = "^(incident|après)", Regex = true }).Count);
                Assert.AreEqual(2, a.Filter(new ArchiveFilter { Pid = "200" }).Count);
                Assert.AreEqual(1, a.Filter(new ArchiveFilter { Sources = new System.Collections.Generic.HashSet<int> { a.Files.First(f => f.Name == "Crash0.txt").SourceId } }).Count);
                Assert.AreEqual("05/11 09:07", LogArchive.MinuteText(t));
                Assert.IsTrue(a.Timeline(a.Entries).Sum(b => b.Lines) >= 4);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void Enregistrement_IncidentsFusionnesEtSignales()
        {
            string dir = Path.Combine(Path.GetTempPath(), "adbt_rec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllLines(Path.Combine(dir, "logcat_000.txt"), new[]
                {
                    "11-06 10:00:00.000  1000  1000 I ActivityManager: Start proc 4321:com.exemple/u0a12",
                    "11-06 10:00:05.000  5762  6864 E ScannerFramework: [ImagerAdapter::getFrameThreadProc] More than 10 Null frame received",
                    "11-06 10:00:20.000  1000  1000 I ActivityManager: Displayed com.exemple/.Main",
                });
                File.WriteAllLines(Path.Combine(dir, "events.txt"), new[]
                {
                    "11-06 09:59:50.000 12345 12345 I ADBTools-Rec: Batterie 86 % · 31.5 °C | Mémoire libre 2100 Mo / 3800 Mo | Wi-Fi -54 dBm Reseau",
                    "11-06 10:00:10.000 12345 12345 W ADBTools-Incident: Incident n°1 signalé sur le terminal : Le lecteur ne lit pas",
                });
                File.WriteAllLines(Path.Combine(dir, "snapshot_incident_01.txt"), new[] { "=== Incident n°1", "mCurrentFocus=Window{com.exemple/.Main}" });

                var a = LogArchive.Open(dir);
                CollectionAssert.AreEqual(new[] { "ADBTools-Rec", "ActivityManager", "ScannerFramework", "ADBTools-Incident", "ActivityManager" }, a.Entries.Select(e => e.Tag).ToArray());
                Assert.IsTrue(a.OtherFiles.Any(f => f.Name == "snapshot_incident_01.txt" && f.Kind == ArchiveFileKind.Snapshot));
                Assert.AreEqual(1, a.Timeline(a.Entries).Sum(b => b.Incidents));

                var findings = LogAnalyzer.Analyze(a.Entries.Cast<LogEntry>().ToList()).Findings;
                var inc = findings.Single(f => f.Category == LogAnalyzer.CatIncident);
                StringAssert.Contains(inc.Detail, "Le lecteur ne lit pas");
                Assert.AreSame(a.Entries[3], inc.FirstEntry);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void Rapport_HtmlAutonomeAvecConstatsEtContexte()
        {
            string dir = Path.Combine(Path.GetTempPath(), "adbt_rep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var a = LogArchive.Open(MakeRx(dir));
                var analysis = LogAnalyzer.Analyze(a.Entries.Cast<LogEntry>().ToList(), a.Title);
                string html = LogReport.BuildHtml(new LogReport.Options
                {
                    Title = a.Title, Archive = a, Analysis = analysis, View = a.Entries, ScopeText = "Tout le relevé", FiltersText = "aucun",
                });
                StringAssert.StartsWith(html, "<!DOCTYPE html>");
                StringAssert.Contains(html, "Zebra TC53E");
                StringAssert.Contains(html, "le capteur ne renvoie plus d&#39;image");     // titre du constat, échappé
                StringAssert.Contains(html, "<span class=\"hl\">");                     // ligne du constat surlignée dans son contexte
                StringAssert.Contains(html, "<svg");
                Assert.IsFalse(html.Contains("<script"));

                // Période réelle : les lignes d'avant la mise à l'heure ne faussent pas le résumé.
                Assert.AreEqual(LogArchive.ToMinute("11-05 08:00:00.000"), a.SpanFrom);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void Comparaison_MessagesPropresOuPlusFrequents()
        {
            FileLogEntry E(string time, string lvl, string tag, string msg) => new FileLogEntry { Time = time, Level = lvl, Tag = tag, Message = msg, Minute = LogArchive.ToMinute(time) };
            var a = Enumerable.Range(0, 60).Select(i => E($"11-05 08:{i:00}:00.000", "E", "ScannerFramework", "Polling 35ms Timeout: " + i)).ToList();
            var b = Enumerable.Range(0, 60).Select(i => E($"11-05 08:{i:00}:00.000", "E", "ScannerFramework", "Polling 35ms Timeout: " + i)).ToList();
            b.AddRange(Enumerable.Range(0, 6).Select(i => E($"11-05 08:{i:00}:30.000", "E", "ScannerFramework", "More than 10 Null frame received")));
            var rows = LogCompare.Compare(a, b);
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("More than # Null frame received", rows[0].Template);
            Assert.AreEqual(6, rows[0].CountB);
        }
    }
}
