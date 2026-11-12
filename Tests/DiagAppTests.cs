using System;
using System.IO;
using System.Text.RegularExpressions;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class DiagAppTests
    {
        [TestMethod]
        public void ExportSaysWhatToRetrieve()
        {
            Assert.AreEqual("survey", DiagMessage.Parse("I/ADBToolsDiag( 12): EXPORT\tsurvey").Test);
            Assert.AreEqual("record", DiagMessage.Parse("I/ADBToolsDiag( 12): EXPORT\trecord").Test);
            Assert.AreEqual("tests", DiagMessage.Parse("I/ADBToolsDiag( 12): EXPORT\ttests\t5").Test);
            Assert.AreEqual("tests", DiagMessage.Parse("I/ADBToolsDiag( 12): EXPORT\t5").Test, "ancienne appli");
        }

        [TestMethod]
        public void ParseInstalledVersion_ReadsDumpsys()
        {
            string dump = "Packages:\n  Package [fr.kanard.adbtools.diag] (a1b2c3):\n    userId=10234\n    versionCode=6 minSdk=21 targetSdk=30\n    versionName=5.0";
            Assert.AreEqual(6, DiagApp.ParseInstalledVersion(dump));
            Assert.AreEqual(0, DiagApp.ParseInstalledVersion("Unable to find package: fr.kanard.adbtools.diag"));
            Assert.AreEqual(0, DiagApp.ParseInstalledVersion(""));
        }

        [TestMethod]
        public void VersionCode_MatchesManifest()
        {
            // Le programme décide de mettre à jour l'appli d'après DiagApp.VersionCode : il doit suivre le manifeste.
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string manifest = null;
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "DiagApp", "AndroidManifest.xml");
                if (File.Exists(p)) { manifest = p; break; }
            }
            if (manifest == null) Assert.Inconclusive("DiagApp/AndroidManifest.xml introuvable depuis le dossier des tests.");
            var m = Regex.Match(File.ReadAllText(manifest), "android:versionCode=\"(\\d+)\"");
            Assert.IsTrue(m.Success);
            Assert.AreEqual(int.Parse(m.Groups[1].Value), DiagApp.VersionCode);
        }

        [TestMethod]
        public void ParseSavedResults_ResultThenVerdict()
        {
            string json = "{\"version\":1,\"tests\":{\"storage_speed\":{\"result\":\"RESULT\\tstorage_speed\\tok\\teyJ3cml0ZV9tYnMiOjEyMC41LCJyZWFkX21icyI6ODAwLCJzaXplX21iIjo2NH0=\",\"at\":1762945200000},"
                        + "\"speaker\":{\"result\":\"RESULT\\tspeaker\\tconfirm\\te30=\",\"verdict\":\"VERDICT\\tspeaker\\tok\",\"at\":1762945300000}}}";
            var list = DiagApp.ParseSavedResults(json);
            Assert.AreEqual(3, list.Count);
            var storage = list.Find(x => x.Message.Test == "storage_speed").Message;
            Assert.AreEqual("RESULT", storage.Type);
            Assert.AreEqual("ok", storage.Status);
            Assert.AreEqual("120.5", storage.Str("write_mbs"));
            var verdict = list.Find(x => x.Message.Type == "VERDICT").Message;
            Assert.AreEqual("speaker", verdict.Test);
            Assert.AreEqual("ok", verdict.Status);
            Assert.AreEqual(0, DiagApp.ParseSavedResults("cat: No such file or directory").Count);
        }
    }
}
