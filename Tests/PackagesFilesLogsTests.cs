using System;
using System.IO;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class PackagesFilesLogsTests
    {
        [TestMethod]
        public void Paquets_TypeEtStatut()
        {
            var list = PackageManagerService.ParsePackageList(
                "package:/a/base.apk=com.a\npackage:/b/base.apk=com.b\n", "package:com.b\n", "package:com.a\n");

            Assert.AreEqual("com.a:Système:Désactivé,com.b:Utilisateur:Actif",
                string.Join(",", list.Select(p => p.PackageName + ":" + p.Type + ":" + p.Status)));
        }

        [TestMethod]
        public void Paquets_CheminsApkBaseEnPremier()
        {
            var paths = PackageManagerService.ParseApkPaths("package:/data/app/x/split_config.fr.apk\r\npackage:/data/app/x/base.apk\r\n");
            CollectionAssert.AreEqual(new[] { "/data/app/x/base.apk", "/data/app/x/split_config.fr.apk" }, paths);
        }

        [TestMethod]
        public void Paquets_ExplicationErreurInstallation()
        {
            StringAssert.Contains(PackageManagerService.DescribeInstallFailure("Failure [INSTALL_FAILED_VERIFICATION_FAILURE]"), "Vérifier les applis via USB");
            Assert.IsNull(PackageManagerService.DescribeInstallFailure("Success"));
        }

        [TestMethod]
        public void Paquets_ApkAvecDossierSplits()
        {
            string dir = Path.Combine(Path.GetTempPath(), "adbtools_tests_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "app_splits"));
                File.WriteAllText(Path.Combine(dir, "app.apk"), "x");
                File.WriteAllText(Path.Combine(dir, "app_splits", "split_config.arm64_v8a.apk"), "x");

                var fromApk = PackageManagerService.ResolveApkSet(Path.Combine(dir, "app.apk"));
                var fromFolder = PackageManagerService.ResolveApkSet(Path.Combine(dir, "app_splits"));

                Assert.AreEqual(2, fromApk.Count);
                Assert.AreEqual(2, fromFolder.Count);
                StringAssert.EndsWith(fromFolder[0], "app.apk"); // l'APK principal est ajouté en premier
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void Explorateur_LiensEtEspaces()
        {
            var files = FileExplorerService.ParseLsOutput(
                "total 8\n" +
                "lrwxrwxrwx 1 root root 21 2023-11-20 14:02 sdcard -> /storage/self/primary\n" +
                "drwxrwx--x 4 root sdcard_rw 3488 2023-11-20 14:02 Mon  Dossier\n" +
                "-rw-rw---- 1 u0 sdcard_rw 2048 2023-11-20 14:02 a.txt\n");

            CollectionAssert.AreEqual(new[] { "Mon  Dossier", "sdcard", "a.txt" }, files.Select(f => f.Name).ToArray());
            Assert.AreEqual("2 Ko", files.Single(f => f.Name == "a.txt").Size);
        }

        [TestMethod]
        public void Logcat_LectureLigneThreadtime()
        {
            var e = LogcatService.ParseLine("10-08 07:44:42.893  7908  7908 I wpa_supplicant: wlan0: CTRL-EVENT-CONNECTED");
            Assert.AreEqual("7908", e.PID);
            Assert.AreEqual("I", e.Level);
            Assert.AreEqual("wpa_supplicant", e.Tag);
        }

        [TestMethod]
        public void Logs_FiltreTexteRegexEtNiveau()
        {
            var e = new LogEntry { RawLine = "E Zebra Scanner FAILED", Level = "F", PID = "12" };

            Assert.IsTrue(LogFilter.Matches(e, new LogFilterCriteria { SearchText = "failed" }));
            Assert.IsTrue(LogFilter.Matches(e, new LogFilterCriteria { SearchText = "scan+er", UseRegex = true }));
            Assert.IsFalse(LogFilter.Matches(e, new LogFilterCriteria { SearchText = "([", UseRegex = true }));
            Assert.IsTrue(LogFilter.Matches(e, new LogFilterCriteria { LevelFilter = "E" })); // E couvre aussi F
            Assert.IsFalse(LogFilter.Matches(e, new LogFilterCriteria { ForegroundPid = "99" }));
        }
    }
}
