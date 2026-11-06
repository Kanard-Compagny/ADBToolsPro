using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class LogWordReportTests
    {
        private static string FindTemplate()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string p = Path.Combine(dir.FullName, "Docs", "Modeles", "Rapport_Logs.docx");
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }
            return null;
        }

        [TestMethod]
        public void RapportWord_ConstatsEnTableauEtBalisesRemplies()
        {
            string template = FindTemplate();
            if (template == null) Assert.Inconclusive("Modèle Rapport_Logs.docx introuvable.");
            string dir = Path.Combine(Path.GetTempPath(), "adbt_word_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllLines(Path.Combine(dir, "Main0.txt"), new[]
                {
                    "DEVICE: Zebra/TC53E/TC53E SERIAL NUMBER: 25336524211469 ANDROID VERSION: 14 BUILD ID: 14-15-22.00-UG-U149 Incremental: 369",
                    "11-05 09:05:00.000   200   200 E ScannerFramework: [ImagerAdapter::getFrameThreadProc] More than 10 Null frame received",
                    "11-05 09:06:00.000   300   300 I wpa_supplicant: wlan0: CTRL-EVENT-DISCONNECTED bssid=aa:bb:cc:dd:ee:ff reason=3",
                    "11-05 09:07:00.000   400   400 W ADBTools-Incident: Incident n°1 signalé sur le terminal : Le lecteur ne lit pas",
                });
                var a = LogArchive.Open(dir);
                var analysis = LogAnalyzer.Analyze(a.Entries.Cast<LogEntry>().ToList(), a.Title);
                var opt = new LogReport.Options { Title = a.Title, Archive = a, Analysis = analysis, View = a.Entries, ScopeText = "05/11 09:05 → 09:07", Sessions = LogInsights.Sessions(a) };
                string output = LogWordReport.Generate(template, Path.Combine(dir, "out"), "Client", "INC-42", opt, "");

                string xml;
                using (var z = ZipFile.OpenRead(output))
                using (var r = new StreamReader(z.GetEntry("word/document.xml").Open())) xml = r.ReadToEnd();
                string text = Regex.Replace(xml, "<[^>]+>", " ");
                Assert.IsFalse(text.Contains("{{"), "balise non remplie");
                Assert.IsFalse(text.Contains("§§"));
                StringAssert.Contains(text, "25336524211469");
                StringAssert.Contains(text, "le capteur ne renvoie plus d");
                StringAssert.Contains(text, "Incident signalé sur le terminal");
                StringAssert.Contains(text, "Détail et conseil");
                StringAssert.Contains(text, "INC-42");
                StringAssert.Contains(Path.GetFileName(output), "Rapport_Logs_25336524211469");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
