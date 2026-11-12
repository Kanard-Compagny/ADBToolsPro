using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class DiagnosticSheetTests
    {
        [TestMethod]
        public void TouchMapReadsTheAppGrid()
        {
            var m = TouchMap.Parse("{\"covered\":3,\"total\":4,\"percent\":75,\"cols\":2,\"rows\":2,\"grid\":\"1101\"}");
            Assert.IsNotNull(m);
            Assert.AreEqual(2, m.Cols);
            Assert.AreEqual(3, m.Covered);
            Assert.IsTrue(m.Touched(0));
            Assert.IsFalse(m.Touched(2));
            Assert.IsNull(TouchMap.Parse("{\"covered\":3,\"total\":4}"), "ancienne appli : pas de grille");
            Assert.IsNull(TouchMap.Parse("{\"cols\":2,\"rows\":2,\"grid\":\"110\"}"), "grille incomplète");
        }

        [TestMethod]
        public void EveryAppTestHasExactlyOneLine()
        {
            var ids = DiagnosticSheet.Lines.Select(l => l.Id).ToList();
            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "identifiants en double");
            foreach (var def in DeviceTestCatalog.All)
                Assert.IsTrue(ids.Contains(DiagnosticSheet.LineIdForAppTest(def.Id)), "pas de ligne pour " + def.Id);
        }

        [TestMethod]
        public void PcChecksKeepTheirIds()
        {
            // Rapport Word et historique reposent sur ces identifiants : aucun contrôle du PC ne doit disparaître.
            foreach (var id in new[] { "auth", "root", "wifi", "ping", "battery", "storage", "sensors", "thermal", "crashes", "keys", "touch", "scanner", "screen" })
                Assert.IsTrue(DiagnosticSheet.Lines.Any(l => l.Id == id), id);
            Assert.AreEqual("keys", DiagnosticSheet.LineIdForAppTest("keys"));
            Assert.AreEqual("sensors", DiagnosticSheet.LineIdForAppTest("sensors"));
            Assert.AreEqual("dev_microphone", DiagnosticSheet.LineIdForAppTest("microphone"));
        }

        [TestMethod]
        public void DetailsJoinPcAndTerminalParts()
        {
            var c = new DiagComponent { Details = "Relevé PC" };
            Assert.AreEqual("Relevé PC", c.Details);
            c.TerminalDetails = "Réaction OK";
            StringAssert.Contains(c.Details, "Relevé PC");
            StringAssert.Contains(c.Details, "SUR LE TERMINAL");
            StringAssert.Contains(c.Details, "Réaction OK");
            var t = new DiagComponent { TerminalDetails = "Photo 1920 × 1080" };
            Assert.AreEqual("Photo 1920 × 1080", t.Details);
        }
    }
}
