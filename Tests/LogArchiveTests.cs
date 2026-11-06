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
                var win = a.Filter(new LogFilter { FromMinute = t - 1, ToMinute = t + 1 });
                CollectionAssert.AreEqual(new[] { "ScannerFramework", "Recent", "AndroidRuntime" }, win.Select(e => e.Tag).ToArray());

                // Filtres combinés.
                Assert.AreEqual(1, a.Filter(new LogFilter { Text = "null FRAME" }).Count);
                Assert.AreEqual(2, a.Filter(new LogFilter { Levels = new System.Collections.Generic.HashSet<string> { "E" } }).Count);
                Assert.AreEqual(2, a.Filter(new LogFilter { Text = "^(incident|après)", Regex = true }).Count);
                Assert.AreEqual(2, a.Filter(new LogFilter { Pid = "200" }).Count);
                Assert.AreEqual(1, a.Filter(new LogFilter { Sources = new System.Collections.Generic.HashSet<int> { a.Files.First(f => f.Name == "Crash0.txt").SourceId } }).Count);
                Assert.AreEqual("05/11 09:07", LogArchive.MinuteText(t));
                Assert.IsTrue(a.Timeline(a.Entries).Sum(b => b.Lines) >= 4);
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
