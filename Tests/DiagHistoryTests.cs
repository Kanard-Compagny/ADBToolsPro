using System;
using System.IO;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class DiagHistoryTests
    {
        private string _dir;
        private string _previous;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "adbtools_hist_" + Guid.NewGuid().ToString("N"));
            _previous = AppSettings.Current.HistoryFolder;
            AppSettings.Current.HistoryFolder = _dir; // en mémoire seulement (pas d'appel à Save)
        }

        [TestCleanup]
        public void Cleanup()
        {
            AppSettings.Current.HistoryFolder = _previous;
            try { Directory.Delete(_dir, true); } catch { }
        }

        [TestMethod]
        public void FromDiagnostic_CompteLesVerdictsEtLitLIdentite()
        {
            var comps = new[]
            {
                new DiagComponent { SystemId = "auth", StatusIcon = "✅", ComponentName = "Autorisation", Details = "Modèle : TC52\nVersion Android : 11" },
                new DiagComponent { SystemId = "battery", StatusIcon = "✅", ComponentName = "Batterie" },
                new DiagComponent { SystemId = "wifi", StatusIcon = "⚠️", ComponentName = "Wi-Fi", Comment = "Signal faible" },
                new DiagComponent { SystemId = "screen", StatusIcon = "❌", ComponentName = "Écran" },
                new DiagComponent { SystemId = "keys", StatusIcon = "⚪", ComponentName = "Boutons" },
            };
            var e = DiagHistory.FromDiagnostic("Diagnostic SAV", "ABC123", "Client", "INC-1", comps);

            Assert.AreEqual("TC52", e.Model);
            Assert.AreEqual("11", e.Android);
            Assert.AreEqual(1, e.Ok);
            Assert.AreEqual(1, e.Warn);
            Assert.AreEqual(1, e.Bad);
            Assert.AreEqual(4, e.Items.Count); // « auth » n'est pas un test
            Assert.AreEqual("Signal faible", e.Items.First(i => i.Name == "Wi-Fi").Comment);
            Assert.AreEqual("Non testé", e.Items.First(i => i.Name == "Boutons").Status);
        }

        [TestMethod]
        public void Save_Puis_LoadAll_ParNumeroDeSerie()
        {
            string report = Path.Combine(Path.GetTempPath(), "rapport_" + Guid.NewGuid().ToString("N") + ".docx");
            File.WriteAllText(report, "x");
            try
            {
                DiagHistory.Save(new HistoryEntry { Type = "Diagnostic SAV", Serial = "SN1", Date = new DateTime(2026, 1, 1, 10, 0, 0), Ok = 3 }, report);
                DiagHistory.Save(new HistoryEntry { Type = "Analyse Wi-Fi", Serial = "SN1", Date = new DateTime(2026, 2, 1, 10, 0, 0), Summary = "Excellent" }, null);
                DiagHistory.Save(new HistoryEntry { Type = "Diagnostic SAV", Serial = "SN2", Date = new DateTime(2026, 3, 1, 10, 0, 0) }, null);

                var all = DiagHistory.LoadAll();
                Assert.AreEqual(3, all.Count);
                Assert.AreEqual("SN2", all[0].Serial); // la plus récente d'abord

                var sn1 = DiagHistory.LoadAll("SN1");
                Assert.AreEqual(2, sn1.Count);
                var withReport = sn1.Single(x => x.Type == "Diagnostic SAV");
                Assert.IsTrue(File.Exists(withReport.ReportPath));
                Assert.AreEqual("3 conformes", withReport.Verdict);
            }
            finally { File.Delete(report); }
        }

        [TestMethod]
        public void Purge_SupprimeLesFichesAnciennes()
        {
            DiagHistory.Save(new HistoryEntry { Type = "Diagnostic SAV", Serial = "OLD", Date = DateTime.Now.AddDays(-400) }, null);
            DiagHistory.Save(new HistoryEntry { Type = "Diagnostic SAV", Serial = "NEW", Date = DateTime.Now.AddDays(-2) }, null);

            Assert.AreEqual(0, DiagHistory.Purge(0)); // « Jamais »
            Assert.AreEqual(1, DiagHistory.Purge(365));
            var left = DiagHistory.LoadAll();
            Assert.AreEqual(1, left.Count);
            Assert.AreEqual("NEW", left[0].Serial);
            Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "OLD")));
        }
    }
}
