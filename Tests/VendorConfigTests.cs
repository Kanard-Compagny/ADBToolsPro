using System;
using System.IO;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class VendorConfigTests
    {
        private const string Profile =
@"<wap-provisioningdoc>
  <characteristic type=""Wi-Fi"" version=""11.3"">
    <parm name=""UseRegulatory"" value=""1""/>
    <characteristic type=""Profiles"">
      <characteristic type=""Profile"">
        <parm name=""SSID"" value=""Reseau test""/>
        <parm name=""PassphraseWPAClear"" value=""secret""/>
      </characteristic>
    </characteristic>
  </characteristic>
  <characteristic type=""AppMgr"" version=""4.2"">
    <parm name=""Action"" value=""Install""/>
    <parm name=""APK"" value=""/sdcard/EHS.apk""/>
  </characteristic>
  <characteristic type=""FileMgr"" version=""6.0"">
    <parm name=""FileAction"" value=""1""/>
    <parm name=""TargetAccessMethod"" value=""2""/>
    <parm name=""TargetPathAndFileName"" value=""/enterprise/usr/enterprisehomescreen.xml""/>
    <parm name=""SourceAccessMethod"" value=""2""/>
    <parm name=""SourcePathAndFileName"" value=""/sdcard/enterprisehomescreen.xml""/>
  </characteristic>
  <characteristic type=""DisplayMgr"" version=""4.3"">
    <parm name=""TimeoutInterval"" value=""600""/>
  </characteristic>
  <characteristic type=""PowerMgr"" version=""4.2"">
    <parm name=""ResetAction"" value=""5""/>
  </characteristic>
  <characteristic type=""SomethingNewMgr"">
    <parm name=""X"" value=""1""/>
  </characteristic>
</wap-provisioningdoc>";

        [TestMethod]
        public void ReadsStepsThemesAndLabels()
        {
            var p = StageNowReader.Read(Profile);
            Assert.IsTrue(p.Ok, p.Error);
            Assert.AreEqual(6, p.Steps.Count);
            Assert.AreEqual("11.3", p.MinVersion);
            var wifi = p.Steps[0];
            Assert.AreEqual(StageNowReader.ThemeWifi, wifi.Theme);
            StringAssert.Contains(wifi.Label, "Reseau test");
            Assert.AreEqual(2, wifi.Line);
            Assert.AreEqual(StageNowReader.ThemeEhs, p.Steps[1].Theme, "installation de l'apk EHS rangée sous EHS");
            Assert.AreEqual(StageNowReader.ThemeEhs, p.Steps[2].Theme);
            StringAssert.Contains(p.Steps[3].Label, "10 min");
            Assert.IsFalse(p.Steps[5].Recognized);
            Assert.AreEqual(StageNowReader.ThemeOther, p.Steps[5].Theme);
        }

        [TestMethod]
        public void SensitiveStepsAreFlaggedAndUncheckedByDefault()
        {
            var p = StageNowReader.Read(Profile);
            var reset = p.Steps.Single(s => s.Type == "PowerMgr");
            Assert.IsTrue(reset.Sensitive);
            Assert.IsFalse(p.Steps.Where(s => s.Type != "PowerMgr").Any(s => s.Sensitive));
            var block = new MasterVendorBlock { Xml = Profile, OriginalXml = Profile };
            Assert.IsFalse(block.IsChecked(reset), "sensible : décochée au départ");
            Assert.IsTrue(block.IsChecked(p.Steps[0]), "ordinaire : cochée au départ");
            block.SetChecked(reset, true);
            block.SetChecked(p.Steps[0], false);
            Assert.IsTrue(block.IsChecked(reset));
            Assert.IsFalse(block.IsChecked(p.Steps[0]));
        }

        [TestMethod]
        public void UsbDebuggingParmIsSensitiveButLoadBalancingIsNot()
        {
            var p = StageNowReader.Read(@"<wap-provisioningdoc>
<characteristic type=""UsbMgr""><parm name=""UsbADBUsage"" value=""2""/></characteristic>
<characteristic type=""Wi-Fi""><parm name=""LoadBalancing"" value=""1""/></characteristic>
</wap-provisioningdoc>");
            Assert.IsTrue(p.Steps[0].Sensitive);
            Assert.IsFalse(p.Steps[1].Sensitive);
        }

        [TestMethod]
        public void ListsFilesTheProfileReadsOnTheDevice()
        {
            var p = StageNowReader.Read(Profile);
            var files = p.Steps.SelectMany(s => s.DeviceFiles).ToList();
            CollectionAssert.AreEquivalent(new[] { "/sdcard/EHS.apk", "/sdcard/enterprisehomescreen.xml" }, files);
        }

        [TestMethod]
        public void FileDownloadedByTheProfileIsNotToAttach()
        {
            var p = StageNowReader.Read(@"<?xml version=""1.0"" encoding=""utf-8""?>
<wap-provisioningdoc>
  <!--SettingID=242-->
  <characteristic version=""11.3"" type=""FileMgr"">
    <parm name=""FileAction"" value=""1"" />
    <characteristic type=""file-details"">
      <parm name=""TargetAccessMethod"" value=""2"" />
      <parm name=""TargetPathAndFileName"" value=""/sdcard/appli.apk"" />
      <parm name=""SourceAccessMethod"" value=""1"" />
      <parm name=""SourceURI"" value=""&amp;#1272&amp;#127chiffre=="" />
    </characteristic>
  </characteristic>
  <characteristic version=""13.5"" type=""AppMgr"">
    <parm name=""Action"" value=""Install"" />
    <parm name=""APK"" value=""/sdcard/appli.apk"" />
  </characteristic>
  <characteristic version=""13.5"" type=""AppMgr"">
    <parm name=""Action"" value=""SetDefaultLauncher"" />
    <parm name=""Package"" value=""com.exemple.appli"" />
  </characteristic>
</wap-provisioningdoc>
<!--{""profile_name"":""Config""}-->");
            Assert.IsTrue(p.Ok, p.Error);
            Assert.AreEqual(3, p.Steps.Count);
            Assert.AreEqual(0, p.Steps.Sum(s => s.DeviceFiles.Count), "apk téléchargée par l'étape FileMgr : rien à joindre");
            StringAssert.Contains(p.Steps[2].Label, "com.exemple.appli");
            Assert.AreEqual("/sdcard/appli.apk", p.Steps[0].DownloadTarget, "téléchargement repéré : le fichier peut être fourni à la place");
            Assert.IsTrue(p.Steps[0].DownloadEncrypted);
        }

        [TestMethod]
        public void MergeAddsNewStepsSkipsDuplicatesAndFlagsDoubleSettings()
        {
            string second = @"<?xml version=""1.0"" encoding=""utf-8""?>
<wap-provisioningdoc>
  <characteristic type=""DisplayMgr"" version=""4.3"">
    <parm name=""TimeoutInterval"" value=""600""/>
  </characteristic>
  <!--SettingID=9-->
  <characteristic type=""DisplayMgr"" version=""13.5"">
    <parm name=""TimeoutInterval"" value=""120""/>
  </characteristic>
  <characteristic type=""Clock"" version=""4.2"">
    <parm name=""TimeZone"" value=""Europe/Paris""/>
  </characteristic>
</wap-provisioningdoc>
<!--{""profile_name"":""Deux""}-->";
            var r = StageNowReader.Merge(Profile, second);
            Assert.AreEqual(2, r.Added);
            Assert.AreEqual(1, r.Duplicates, "étape identique non recopiée");
            CollectionAssert.AreEqual(new[] { "DisplayMgr" }, r.SameTypes);
            var p = StageNowReader.Read(r.Xml);
            Assert.IsTrue(p.Ok, p.Error);
            Assert.AreEqual(8, p.Steps.Count);
            Assert.AreEqual("Clock", p.Steps.Last().Type);
            StringAssert.Contains(r.Xml, "SettingID=9", "commentaire de l'étape gardé");
        }

        [TestMethod]
        public void InvalidXmlGivesTheLine()
        {
            var p = StageNowReader.Read("<wap-provisioningdoc>\n  <characteristic type=\"Clock\">\n    <parm name=\"TimeZone\" value=\"Europe/Paris\">\n  </characteristic>\n</wap-provisioningdoc>");
            Assert.IsFalse(p.Ok);
            Assert.IsTrue(p.ErrorLine >= 3, "ligne " + p.ErrorLine);
        }

        [TestMethod]
        public void StepKeysSurviveUnrelatedEdits()
        {
            var before = StageNowReader.Read(Profile);
            var after = StageNowReader.Read(Profile.Replace("value=\"600\"", "value=\"300\""));
            Assert.AreEqual(before.Steps[0].Key, after.Steps[0].Key, "étape non touchée : même clé (son choix est gardé)");
            Assert.AreNotEqual(before.Steps[3].Key, after.Steps[3].Key);
        }

        [TestMethod]
        public void HelperResultAndMxErrorsAreReadable()
        {
            string response = "<wap-provisioningdoc><characteristic-error type=\"Clock\" desc=\"Fuseau inconnu\"/></wap-provisioningdoc>";
            string data = "SUCCESS|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(response));
            var (status, detail) = StageNowReader.ParseHelperData(data);
            Assert.AreEqual("SUCCESS", status);
            Assert.AreEqual(response, detail);
            Assert.AreEqual("Fuseau inconnu", StageNowReader.ErrorSummary(detail));
            Assert.AreEqual(1, MasterProfile.ParseBroadcastResult("Broadcast completed: result=1, data=\"" + data + "\"").Code);
        }

        [TestMethod]
        public void BrandsAndVersions()
        {
            Assert.AreEqual(VendorModules.Zebra, VendorModules.BrandOf("Zebra Technologies"));
            Assert.AreEqual(VendorModules.Zebra, VendorModules.BrandOf("Symbol Technologies"));
            Assert.AreEqual("Honeywell", VendorModules.BrandOf("Honeywell"));
            Assert.IsTrue(VendorModules.CompareVersions("13.4.0.10", "11.3") > 0);
            Assert.IsTrue(VendorModules.CompareVersions("10.5", "11.3") < 0);
            Assert.AreEqual("13.4.0.10", VendorModules.ParseMxVersion("  versionCode=1340010 minSdk=30\n    versionName=13.4.0.10\n"));
        }

        [TestMethod]
        public void VendorBlocksSurviveSaveAndOpen()
        {
            string dir = Path.Combine(Path.GetTempPath(), "adbt_vendor_" + Guid.NewGuid().ToString("N"));
            string archive = dir + ".adbmaster";
            try
            {
                var m = new MasterProfile { Name = "Test" };
                var block = new MasterVendorBlock { Name = "Entrepôt", Xml = Profile, OriginalXml = Profile };
                block.Unchecked.Add("Clock:abc");
                block.Files.Add(new MasterVendorFile { DevicePath = "/sdcard/EHS.apk", Local = "vendor/x/EHS.apk" });
                m.Vendor.Add(block);
                m.SaveTo(dir, archive);
                var back = MasterProfile.Open(archive, dir + "_open");
                Assert.AreEqual(1, back.Vendor.Count);
                Assert.AreEqual("Entrepôt", back.Vendor[0].Name);
                Assert.AreEqual(StageNowReader.Read(Profile).Steps.Count, StageNowReader.Read(back.Vendor[0].Xml).Steps.Count);
                CollectionAssert.AreEqual(new[] { "Clock:abc" }, back.Vendor[0].Unchecked);
                Assert.AreEqual("vendor/x/EHS.apk", back.Vendor[0].Files[0].Local);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
                try { Directory.Delete(dir + "_open", true); } catch { }
                try { File.Delete(archive); } catch { }
            }
        }
    }
}
