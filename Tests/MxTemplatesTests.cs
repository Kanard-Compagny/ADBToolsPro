using System.Collections.Generic;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class MxTemplatesTests
    {
        private static Dictionary<string, string> Sample(MxTemplate t)
        {
            var v = t.Defaults();
            void Set(string k, string val) { if (v.ContainsKey(k)) v[k] = val; }
            Set("ssid", "Entrepot-Nord");
            Set("password", "motdepasse1");
            Set("apk", "/sdcard/Download/appli.apk");
            Set("package", "com.societe.appli");
            Set("source", "/sdcard/Download/conf.xml");
            Set("url", "https://serveur/fichier.apk");
            Set("target", "/sdcard/Download/fichier.apk");
            Set("bt", "1");
            return v;
        }

        private const string StageNow =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<wap-provisioningdoc>\r\n" +
            "  <!--SettingID=1-->\r\n" +
            "  <characteristic version=\"13.5\" type=\"DisplayMgr\">\r\n" +
            "    <parm name=\"TimeoutInterval\" value=\"300\" />\r\n" +
            "  </characteristic>\r\n" +
            "  <!--SettingID=2-->\r\n" +
            "  <characteristic version=\"13.5\" type=\"AppMgr\">\r\n" +
            "    <parm name=\"Action\" value=\"Install\" />\r\n" +
            "    <parm name=\"APK\" value=\"/sdcard/a.apk\" />\r\n" +
            "  </characteristic>\r\n" +
            "</wap-provisioningdoc>\r\n" +
            "<!--{\"profile_name\":\"Config\"}-->";

        [TestMethod]
        public void EveryTemplateBuildsAStepItRecognizes()
        {
            foreach (var t in MxTemplates.All)
            {
                var v = Sample(t);
                Assert.IsNull(MxTemplates.Validate(t, v), t.Id);
                var r = MxTemplates.Insert(MxTemplates.EmptyProfile(), MxTemplates.BuildXml(t, v), 0, t.MustBeLast);
                var p = StageNowReader.Read(r.Xml);
                Assert.IsTrue(p.Ok, t.Id + " : " + p.Error);
                Assert.AreEqual(1, p.Steps.Count, t.Id);
                Assert.AreEqual(1, MxTemplates.CountIn(t, p), t.Id);
                Assert.IsFalse(p.Steps[0].Sensitive, t.Id + " ne doit pas être une étape sensible");
            }
        }

        [TestMethod]
        public void TemplatesDoNotMatchEachOther()
        {
            foreach (var t in MxTemplates.All)
            {
                var p = StageNowReader.Read(MxTemplates.Insert(MxTemplates.EmptyProfile(), MxTemplates.BuildXml(t, Sample(t)), 0).Xml);
                foreach (var other in MxTemplates.All.Where(o => o != t))
                    Assert.AreEqual(0, MxTemplates.CountIn(other, p), t.Id + " compté comme " + other.Id);
            }
        }

        [TestMethod]
        public void WifiFollowsTheStageNowStructure()
        {
            var t = MxTemplates.Find("wifi-add");
            string xml = MxTemplates.BuildXml(t, Sample(t));
            StringAssert.Contains(xml, "name=\"NetworkAction\" value=\"Add\"");
            StringAssert.Contains(xml, "name=\"WPAModePersonal\" value=\"3\"");
            StringAssert.Contains(xml, "name=\"EncryptionWPA2Personal\" value=\"3\"");
            StringAssert.Contains(xml, "name=\"PassphraseWPAClear\" value=\"motdepasse1\"");

            var open = Sample(t);
            open["security"] = "open";
            open["password"] = "";
            Assert.IsNull(MxTemplates.Validate(t, open), "pas de mot de passe pour un réseau ouvert");
            Assert.IsFalse(MxTemplates.BuildXml(t, open).Contains("PassphraseWPAClear"));
        }

        [TestMethod]
        public void ValidationExplainsWhatIsWrong()
        {
            var t = MxTemplates.Find("wifi-add");
            var v = Sample(t);
            v["password"] = "court";
            Assert.IsNotNull(MxTemplates.Validate(t, v));
            v = Sample(t);
            v["ssid"] = "a&b";
            Assert.IsNotNull(MxTemplates.Validate(t, v));
            var u = MxTemplates.Find("app-uninstall");
            var w = Sample(u);
            w["package"] = "pas un paquet";
            Assert.IsNotNull(MxTemplates.Validate(u, w));
            var r = MxTemplates.Find("radios");
            Assert.IsNotNull(MxTemplates.Validate(r, r.Defaults()), "au moins une radio");
        }

        [TestMethod]
        public void InsertsAtTheEndAndKeepsComments()
        {
            var t = MxTemplates.Find("display-timeout");
            var r = MxTemplates.Insert(StageNow, MxTemplates.BuildXml(t, t.Defaults()), 0);
            var p = StageNowReader.Read(r.Xml);
            Assert.IsTrue(p.Ok, p.Error);
            Assert.AreEqual(3, p.Steps.Count);
            Assert.AreEqual("DisplayMgr", p.Steps[2].Type);
            StringAssert.Contains(r.Xml, "<!--SettingID=2-->");
            StringAssert.EndsWith(r.Xml, "<!--{\"profile_name\":\"Config\"}-->");
            Assert.IsTrue(r.Xml.Contains("\r\n  <characteristic type=\"DisplayMgr\""), "même indentation que les autres étapes");
            Assert.AreEqual(12, r.FirstLine);
        }

        [TestMethod]
        public void InsertsAfterTheStepUnderTheCursor()
        {
            var t = MxTemplates.Find("locale");
            // Curseur ligne 5 (dans la première étape) : l'étape va avant le commentaire de la deuxième.
            var r = MxTemplates.Insert(StageNow, MxTemplates.BuildXml(t, t.Defaults()), 5);
            var p = StageNowReader.Read(r.Xml);
            Assert.AreEqual("UiMgr", p.Steps[1].Type);
            var lines = r.Xml.Split('\n');
            StringAssert.Contains(lines[r.FirstLine + r.LineCount - 1], "SettingID=2");
        }

        [TestMethod]
        public void RebootStaysLast()
        {
            var reboot = MxTemplates.Find("reboot");
            var withReboot = MxTemplates.Insert(StageNow, MxTemplates.BuildXml(reboot, reboot.Defaults()), 0, true).Xml;
            var t = MxTemplates.Find("clock");
            foreach (int line in new[] { 0, 5, 999 })
            {
                var p = StageNowReader.Read(MxTemplates.Insert(withReboot, MxTemplates.BuildXml(t, t.Defaults()), line).Xml);
                Assert.IsTrue(MxTemplates.IsReboot(p.Steps.Last()), "ligne " + line);
            }
        }

        [TestMethod]
        public void EmptyProfileIsAcceptedOnlyWhenAsked()
        {
            Assert.IsFalse(StageNowReader.Read(MxTemplates.EmptyProfile()).Ok);
            Assert.IsTrue(StageNowReader.Read(MxTemplates.EmptyProfile(), true).Ok);
        }

        [TestMethod]
        public void SplitsAClosingTagOnTheSameLine()
        {
            string xml = "<wap-provisioningdoc><characteristic type=\"Clock\"><parm name=\"AutoTime\" value=\"true\"/></characteristic></wap-provisioningdoc>";
            var t = MxTemplates.Find("reboot");
            var p = StageNowReader.Read(MxTemplates.Insert(xml, MxTemplates.BuildXml(t, t.Defaults()), 0, true).Xml);
            Assert.IsTrue(p.Ok, p.Error);
            Assert.AreEqual(2, p.Steps.Count);
        }
    }
}
