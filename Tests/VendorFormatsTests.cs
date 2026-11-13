using System.Linq;
using System.Xml.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class VendorFormatsTests
    {
        private const string S2d = @"{
  ""layout"": { ""description"": ""Préparation entrepôt"" },
  ""network"": { ""essid"": ""Entrepot-Nord"", ""mode"": ""wpa2-psk"", ""mode-key"": ""motdepasse1"" },
  ""settings"": { ""status-bar"": false, ""auto-time"": true, ""debug-bridge"": false },
  ""global"": { ""script"": [""reboot-after""], ""action"": ""reset"", ""auto-scan"": true },
  ""blobs"": [ { ""file"": ""/sdcard/conf.txt"", ""content"": ""SGVsbG8="" } ],
  ""mystere"": { ""a"": 1 }
}";

        private const string Hw = @"<?xml version=""1.0"" standalone=""yes""?>
<ConfigDoc name=""DeviceConfig"">
   <Section name=""Wireless and networks"">
      <Section name=""WiFi"">
         <Section name=""Supplicant"">
            <Section name=""WiFiProfile1"">
               <Key name=""SSID"">DEMO-SSID</Key>
               <Key name=""PSK"" flags=""1024"">secret</Key>
            </Section>
         </Section>
         <Section name=""WiFi Settings"">
            <Key name=""WifiEnable"">3</Key>
         </Section>
      </Section>
   </Section>
   <Section name=""Display"">
      <Key name=""Sleep"">60</Key>
      <Key name=""Brightness"">80</Key>
   </Section>
   <Section name=""Developer options"">
      <Key name=""USB debugging"">1</Key>
   </Section>
   <SettingsPackage>pkg/HON660/</SettingsPackage>
   <HHPReserved name=""HHPReserved"">
      <Section>
         <Key name=""EXMVersion"">1</Key>
      </Section>
   </HHPReserved>
</ConfigDoc>";

        [TestMethod]
        public void DetectsTheFormat()
        {
            Assert.AreEqual(VendorKinds.Scan2Deploy, VendorKinds.Detect("﻿  " + S2d));
            Assert.AreEqual(VendorKinds.DeviceConfig, VendorKinds.Detect(Hw));
            Assert.AreEqual(VendorKinds.StageNow, VendorKinds.Detect("<wap-provisioningdoc/>"));
            Assert.AreEqual("Datalogic", VendorKinds.BrandOf(VendorKinds.Scan2Deploy));
            Assert.AreEqual("Honeywell", VendorKinds.BrandOf(VendorKinds.DeviceConfig));
        }

        [TestMethod]
        public void ReadsScan2Deploy()
        {
            var p = Scan2DeployReader.Read(S2d);
            Assert.IsTrue(p.Ok, p.Error);
            Assert.AreEqual("Préparation entrepôt", p.Name);
            var wifi = p.Steps.Single(s => s.Key == "s2d:network");
            Assert.AreEqual(StageNowReader.ThemeWifi, wifi.Theme);
            StringAssert.Contains(wifi.Label, "Entrepot-Nord");
            Assert.IsTrue(wifi.Warning.Length > 0, "purge par défaut : les autres réseaux sont effacés");
            var adb = p.Steps.Single(s => s.Key == "s2d:settings.debug-bridge");
            Assert.IsTrue(adb.Sensitive);
            Assert.IsFalse(p.Steps.Single(s => s.Key == "s2d:global.action").Sensitive, "reset = redémarrage, pas une remise à zéro");
            Assert.IsTrue(p.Steps.Single(s => s.Key == "s2d:global.script").Warning.Length > 0);
            Assert.IsNotNull(p.Steps.SingleOrDefault(s => s.Key == "s2d:global.options"));
            Assert.IsNotNull(p.Steps.SingleOrDefault(s => s.Key == "s2d:blobs[0]"));
            Assert.IsFalse(p.Steps.Single(s => s.Key == "s2d:mystere").Recognized);
            Assert.IsTrue(p.Steps.All(s => s.Line >= 1));
            Assert.IsTrue(Scan2DeployReader.EndsWithRestart(S2d));
        }

        [TestMethod]
        public void FactoryResetIsSensitive()
        {
            var p = Scan2DeployReader.Read(@"{ ""global"": { ""action"": ""factory-reset"" } }");
            Assert.IsTrue(p.Steps.Single().Sensitive);
        }

        [TestMethod]
        public void InvalidJsonGivesTheLine()
        {
            var p = Scan2DeployReader.Read("{\n  \"network\": {\n    \"essid\": \"x\",,\n  }\n}");
            Assert.IsFalse(p.Ok);
            Assert.AreEqual(3, p.ErrorLine);
        }

        [TestMethod]
        public void Scan2DeploySendsOnlyCheckedSteps()
        {
            string json = Scan2DeployReader.BuildChecked(S2d, s => s.Key != "s2d:settings.debug-bridge" && s.Key != "s2d:blobs[0]" && s.Key != "s2d:global.options");
            var p = Scan2DeployReader.Read(json);
            Assert.IsTrue(p.Ok, p.Error);
            Assert.IsFalse(p.Steps.Any(s => s.Key == "s2d:settings.debug-bridge"));
            Assert.IsFalse(p.Steps.Any(s => s.Key.StartsWith("s2d:blobs")));
            Assert.IsFalse(p.Steps.Any(s => s.Key == "s2d:global.options"));
            Assert.IsTrue(p.Steps.Any(s => s.Key == "s2d:settings.status-bar"));
            Assert.IsTrue(p.Steps.Any(s => s.Key == "s2d:global.script"));
        }

        [TestMethod]
        public void ReadsTheAgentStatus()
        {
            var ok = Scan2DeployReader.ParseStatus("{\"timestamp\":\"2026-11-13T10:00:00\",\"status\":\"success\",\"details\":\"done\"}");
            Assert.IsTrue(ok.Value.Success);
            var ko = Scan2DeployReader.ParseStatus("{\"timestamp\":\"2026-11-13T10:00:00\",\"status\":\"error\",\"details\":\"network\"}");
            Assert.IsFalse(ko.Value.Success);
            Assert.IsNull(Scan2DeployReader.ParseStatus(""));
            StringAssert.Contains(Scan2DeployReader.ApplyCommand("/sdcard/Download/a.json"), "--es json-path '/sdcard/Download/a.json'");
        }

        [TestMethod]
        public void ReadsDeviceConfig()
        {
            var p = DeviceConfigReader.Read(Hw);
            Assert.IsTrue(p.Ok, p.Error);
            Assert.AreEqual(4, p.Steps.Count, "HHPReserved ignoré");
            var wifi = p.Steps[0];
            StringAssert.Contains(wifi.Label, "DEMO-SSID");
            Assert.AreEqual(StageNowReader.ThemeWifi, wifi.Theme);
            Assert.IsTrue(wifi.Warning.Length > 0, "clé protégée (flags 1024)");
            Assert.AreEqual(StageNowReader.ThemeWifi, p.Steps[1].Theme);
            Assert.AreEqual(StageNowReader.ThemeScreen, p.Steps[2].Theme);
            Assert.IsTrue(p.Steps[3].Sensitive, "débogage USB");
            Assert.AreEqual(6, wifi.Line);
        }

        [TestMethod]
        public void DeviceConfigSendsOnlyCheckedSections()
        {
            var p = DeviceConfigReader.Read(Hw);
            string wifiKey = p.Steps[0].Key;
            string xml = DeviceConfigReader.BuildChecked(Hw, s => s.Key != wifiKey && !s.Sensitive);
            var q = DeviceConfigReader.Read(xml);
            Assert.IsTrue(q.Ok, q.Error);
            Assert.AreEqual(2, q.Steps.Count);
            var doc = XDocument.Parse(xml);
            Assert.IsFalse(doc.Descendants("Section").Any(s => (string)s.Attribute("name") == "Supplicant"), "sections devenues vides retirées");
            Assert.IsFalse(doc.Descendants("Section").Any(s => (string)s.Attribute("name") == "Developer options"));
            Assert.IsTrue(doc.Descendants("HHPReserved").Any(), "réservé Honeywell gardé");
            StringAssert.StartsWith(xml, "<?xml");
        }

        [TestMethod]
        public void ProvisionerTasksAreNotSupportedYet()
        {
            var p = DeviceConfigReader.Read("<ConfigDoc name='Provisioner'><Section name='Tasks'><Section name='Task'><Key name='Action'>Run</Key></Section></Section></ConfigDoc>");
            Assert.IsFalse(p.Ok);
            StringAssert.Contains(p.Error, "Provisioner");
        }

        // Profil réel exporté par Scan2Deploy Studio (schéma 1.13), valeurs anonymisées.
        private const string S2dStudio = @"{
  ""global"": {
    ""install-path"": ""/sdcard/Download/scan2deploy/abc"",
    ""auto-scan"": true,
    ""oemconfig"": true,
    ""script"": [ ""COPY \""/sdcard/a.json\"" \""/sdcard/b.json\"""", ""COPY \""/sdcard/c.txt\"" \""/sdcard/d.txt\"""" ],
    ""action"": ""intent:#Intent;action=com.exemple.ACTION;package=com.exemple.appli;end""
  },
  ""update-scan2deploy"": { ""update-version"": 12599 },
  ""$schema"": ""https://raw.githubusercontent.com/datalogic/scan2deploy-android-schema/v1.13.0/schema.json"",
  ""settings"": {
    ""default-home"": { ""package-name"": ""com.datalogic.launcher"", ""type"": ""custom-app"" },
    ""lock-screen"": false
  },
  ""network"": { ""essid"": ""Reseau"", ""mode"": ""wpa-psk"", ""mode-key"": ""secret123"" },
  ""additional-networks"": [ { ""essid"": ""Second"", ""mode"": ""open"" } ],
  ""deployment"": { ""working-archive"": ""/sdcard/testScan2deploy.tar"", ""storage-type"": ""internal-storage"" }
}";

        [TestMethod]
        public void ReadsAStudioExport()
        {
            var p = Scan2DeployReader.Read(S2dStudio);
            Assert.IsTrue(p.Ok, p.Error);
            Assert.IsTrue(p.Steps.All(s => s.Recognized), string.Join(", ", p.Steps.Where(s => !s.Recognized).Select(s => s.Key)));
            Assert.IsFalse(p.Steps.Any(s => s.Key.Contains("$schema")));
            StringAssert.Contains(p.Steps.Single(s => s.Key == "s2d:settings.default-home").Label, "com.datalogic.launcher");
            Assert.AreEqual(StageNowReader.ThemeApps, p.Steps.Single(s => s.Key == "s2d:settings.default-home").Theme);
            StringAssert.Contains(p.Steps.Single(s => s.Key == "s2d:global.action").Label, "com.exemple.appli");
            StringAssert.Contains(p.Steps.Single(s => s.Key == "s2d:global.script").Label, "COPY ×2");
            StringAssert.Contains(p.Steps.Single(s => s.Key == "s2d:global.options").Label, "oemconfig");
            Assert.IsNotNull(p.Steps.SingleOrDefault(s => s.Key == "s2d:update-scan2deploy"));
            StringAssert.Contains(p.Steps.Single(s => s.Key == "s2d:additional-networks[0]").Label, "Second");
            var dep = p.Steps.Single(s => s.Key == "s2d:deployment");
            Assert.IsFalse(dep.Label.Contains("http"), "déploiement local, pas de serveur");
            CollectionAssert.Contains(dep.DeviceFiles, "/sdcard/testScan2deploy.tar");
            Assert.IsFalse(Scan2DeployReader.EndsWithRestart(S2dStudio));

            string json = Scan2DeployReader.BuildChecked(S2dStudio, s => s.Key != "s2d:additional-networks[0]");
            Assert.IsFalse(json.Contains("Second"));
        }
    }
}
