using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class ReportDeviceTestsTests
    {
        private static string FindTemplate()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string p = Path.Combine(dir.FullName, "Docs", "Modeles", "Rapport_SAV.docx");
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }
            return null;
        }

        private static string DocText(string docx)
        {
            using (var z = ZipFile.OpenRead(docx))
            using (var r = new StreamReader(z.GetEntry("word/document.xml").Open()))
                return Regex.Replace(r.ReadToEnd(), "<[^>]+>", " ");
        }

        [TestMethod]
        public void Rapport_SeulementLesTestsDuTerminalFaits()
        {
            string template = FindTemplate();
            if (template == null) Assert.Inconclusive("Modèle Rapport_SAV.docx introuvable.");
            string outDir = Path.Combine(Path.GetTempPath(), "adbt_rep_" + Guid.NewGuid().ToString("N"));
            try
            {
                var comps = new List<DiagComponent>
                {
                    new DiagComponent { SystemId = "auth", StatusIcon = "✅", ComponentName = "Identité", Details = "• Modèle : TC52\n• Version Android : 11" },
                    new DiagComponent { SystemId = "scanner", StatusIcon = "✅", ComponentName = "Optique Scanner (Test Manuel)", Details = "ok", Comment = "" },
                    new DiagComponent { SystemId = "dev_camera_back", StatusIcon = "✅", ComponentName = "Caméra arrière", Details = "Photo 1920 × 1080", Comment = "RAS" },
                    new DiagComponent { SystemId = "dev_microphone", StatusIcon = "❌", ComponentName = "Micro", Details = "Pratiquement aucun son capté.", Comment = "" },
                    new DiagComponent { SystemId = "dev_nfc", StatusIcon = "⚪", ComponentName = "NFC", Details = "", Comment = "" },
                };
                string path = WordReportService.GenerateReport(template, outDir, "SN1", "Client", "INC-1", comps);
                string text = DocText(path);
                StringAssert.Contains(text, "Tests sur le terminal");
                StringAssert.Contains(text, "Caméra arrière");
                StringAssert.Contains(text, "Photo 1920 × 1080");
                StringAssert.Contains(text, "Pratiquement aucun son capté.");
                Assert.IsFalse(text.Contains(" NFC "), "un test non fait n'apparaît pas");
                Assert.IsFalse(text.Contains("{{"), "aucune balise restante");
            }
            finally { try { Directory.Delete(outDir, true); } catch { } }
        }
    }
}
