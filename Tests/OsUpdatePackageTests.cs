using System;
using System.IO;
using System.IO.Compression;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class OsUpdatePackageTests
    {
        private const string Fp13 = "Zebra/TC58/TC58:13/13-20-18.00-TG-U01-STD-ATH-04/100:user/release-keys";
        private const string Fp14 = "Zebra/TC58/TC58:14/14-35-10.00-UG-U166-STD-ATH-04/1176:user/release-keys";

        [TestMethod]
        public void FullPackage_MatchingModel_IsOk()
        {
            var p = OsUpdatePackage.FromMetadata("ota-type=BLOCK\npre-device=TC58\npost-build=" + Fp14 + "\n");
            Assert.IsTrue(p.HasMetadata);
            Assert.IsFalse(p.IsIncremental);
            Assert.AreEqual(OsUpdatePackage.Fit.Ok, p.Check("TC58", Fp13).Result);
        }

        [TestMethod]
        public void OtherModel_IsBlocked()
        {
            var p = OsUpdatePackage.FromMetadata("pre-device=TC58,TC58E\npost-build=" + Fp14);
            var r = p.Check("TC53", Fp13);
            Assert.AreEqual(OsUpdatePackage.Fit.Blocked, r.Result);
            StringAssert.Contains(r.Message, "TC58");
            Assert.AreEqual(OsUpdatePackage.Fit.Ok, p.Check("tc58e", Fp13).Result);
        }

        [TestMethod]
        public void ZebraPlatformName_IsFoundInOtherProperties()
        {
            // Paquet Zebra commun TC53 / TC58 : pre-device = plateforme « athena », ro.product.device = « TC58 ».
            var p = OsUpdatePackage.FromMetadata("pre-device=athena\npost-build=Zebra/athena/athena:14/14-35-10.00-UG-U166-STD-ATH-04/1176:user/release-keys");
            var props = new System.Collections.Generic.Dictionary<string, string>
            {
                ["ro.product.device"] = "TC58", ["ro.product.name"] = "TC58", ["ro.build.platform"] = "athena", ["ro.product.board"] = "lahaina"
            };
            var ids = OsUpdatePackage.DeviceIdsFrom(props);
            Assert.AreEqual("TC58", ids[0]);
            Assert.AreEqual(OsUpdatePackage.Fit.Ok, p.Check(ids, Fp13).Result);
            // Même numéro de build que le paquet, même si l'empreinte porte un autre nom : déjà à jour.
            Assert.AreEqual(OsUpdatePackage.Fit.AlreadyThere, p.Check(ids, Fp14).Result);
            ids.Remove("athena");
            var blocked = p.Check(ids, Fp13);
            Assert.AreEqual(OsUpdatePackage.Fit.Blocked, blocked.Result);
            StringAssert.Contains(blocked.Message, "(TC58)");
        }

        [TestMethod]
        public void PlatformFromBuildFlavor_IsUsed()
        {
            var ids = OsUpdatePackage.DeviceIdsFrom(new System.Collections.Generic.Dictionary<string, string>
            { ["ro.product.device"] = "TC53", ["ro.build.flavor"] = "athena-user" });
            CollectionAssert.Contains(ids, "athena");
        }

        [TestMethod]
        public void ZebraDeviceManifest_ListsAcceptedModels()
        {
            var p = OsUpdatePackage.FromMetadata("");
            p.ApplyDeviceManifest("<?xml version=\"1.0\"?><Properties><product><ro.product.name value=\"TC53,TC58,TC73\"/><ro.product.name_ng value=\"TC22A\"/><ro.product.board value=\"lahaina\"/></product></Properties>");
            CollectionAssert.AreEquivalent(new[] { "TC53", "TC58", "TC73", "TC22A" }, p.ManifestModels);
            Assert.AreEqual(OsUpdatePackage.Fit.Ok, p.Check(new System.Collections.Generic.List<string> { "TC53" }, Fp13).Result);
            Assert.AreEqual(OsUpdatePackage.Fit.Blocked, p.Check(new System.Collections.Generic.List<string> { "TC21" }, Fp13).Result);
        }

        [TestMethod]
        public void AlreadyAtTargetVersion_IsReported()
        {
            var p = OsUpdatePackage.FromMetadata("pre-device=TC58\npost-build=" + Fp14);
            Assert.AreEqual(OsUpdatePackage.Fit.AlreadyThere, p.Check("TC58", Fp14).Result);
        }

        [TestMethod]
        public void Incremental_RequiresTheRightStartVersion()
        {
            var p = OsUpdatePackage.FromMetadata("pre-device=TC58\npre-build=" + Fp13 + "\npost-build=" + Fp14);
            Assert.IsTrue(p.IsIncremental);
            Assert.AreEqual(OsUpdatePackage.Fit.Ok, p.Check("TC58", Fp13).Result);
            Assert.AreEqual(OsUpdatePackage.Fit.Blocked, p.Check("TC58", "Zebra/TC58/TC58:13/13-11-00.00-TG-U00-STD-ATH-04/50:user/release-keys").Result);
        }

        [TestMethod]
        public void NoMetadata_IsNotChecked()
        {
            var p = OsUpdatePackage.FromMetadata("");
            Assert.IsFalse(p.HasMetadata);
            Assert.AreEqual(OsUpdatePackage.Fit.Ok, p.Check("ANY", Fp13).Result);
        }

        [TestMethod]
        public void Read_FindsMetadataInsideTheZip()
        {
            string dir = Path.Combine(Path.GetTempPath(), "adbt_ota_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string zip = Path.Combine(dir, "update.zip");
                using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                using (var w = new StreamWriter(z.CreateEntry("META-INF/com/android/metadata").Open()))
                    w.Write("pre-device=TC58\npost-build=" + Fp14 + "\n");
                var p = OsUpdatePackage.Read(zip);
                Assert.IsTrue(p.HasMetadata);
                CollectionAssert.AreEqual(new[] { "TC58" }, p.PreDevices);
                Assert.AreEqual("14-35-10.00-UG-U166-STD-ATH-04", OsUpdatePackage.ShortBuild(p.PostBuild));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void SideloadProgress_AndBattery_AreParsed()
        {
            var m = OsUpdatePackage.SideloadProgress.Match("serving: 'update.zip'  (~47%)    ");
            Assert.IsTrue(m.Success);
            Assert.AreEqual("47", m.Groups[1].Value);
            Assert.AreEqual(82, OsUpdatePackage.ParseBatteryLevel("Current Battery Service state:\n  AC powered: false\n  level: 82\n  scale: 100\n"));
            Assert.AreEqual(-1, OsUpdatePackage.ParseBatteryLevel(""));
        }
    }
}
