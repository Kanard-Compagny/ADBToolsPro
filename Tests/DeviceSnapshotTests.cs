using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class DeviceSnapshotTests
    {
        private static DeviceSnapshot Make(string serial, string patch, string timeout, string appVersion, bool extraApp, bool disableCamera)
        {
            var props = new Dictionary<string, string>
            {
                ["ro.product.manufacturer"] = "Zebra Technologies",
                ["ro.product.model"] = "TC52",
                ["ro.build.version.release"] = "11",
                ["ro.build.version.security_patch"] = patch,
                ["ro.serialno"] = serial,
                ["ro.boot.serialno"] = serial,
                ["persist.sys.timezone"] = "Europe/Paris",
                ["ro.build.date"] = serial == "A" ? "lun. 1" : "mar. 2",
                ["ro.config.ringtone"] = "Ring.ogg",
                ["dev.bootcomplete"] = "1",
            };
            string global = "adb_enabled=1\nboot_count=" + (serial == "A" ? "12" : "40") + "\nwifi_sleep_policy=2\nntp_server=pool.ntp.org\n";
            string system = "screen_off_timeout=" + timeout + "\nscreen_brightness=128\nnext_alarm_formatted=\n";
            string secure = "android_id=" + serial + "abcdef\ndefault_input_method=com.android.inputmethod.latin/.LatinIME\n";
            string user = "package:com.exemple.inventaire\n" + (extraApp ? "package:com.exemple.extra\n" : "");
            string disabled = disableCamera ? "package:com.android.camera2\n" : "";
            string versions =
                "  Package [com.exemple.inventaire] (1a2b):\n    versionCode=12 minSdk=26 targetSdk=30\n    versionName=" + appVersion + "\n" +
                (extraApp ? "  Package [com.exemple.extra] (3c4d):\n    versionName=1.0\n" : "") +
                "  Package [com.symbol.datawedge] (5e6f):\n    versionName=11.2.14\n";
            return DeviceSnapshot.Build(serial, props, global, system, secure, user, disabled, versions);
        }

        [TestMethod]
        public void Releve_IgnoreLesValeursPropresAChaqueAppareil()
        {
            var s = Make("A", "2024-01-01", "60000", "2.1", false, false);
            Assert.AreEqual("TC52", s.Model);
            Assert.IsFalse(s.Items.Any(i => i.Key.Contains("serialno") || i.Key.Contains("android_id") || i.Key.Contains("boot_count") || i.Key.Contains("ro.build.date") || i.Key.Contains("next_alarm")));
            Assert.IsFalse(s.Items.Any(i => i.Key == "dev.bootcomplete"), "seules les propriétés ro.* et persist.* sont relevées");
            Assert.AreEqual("2.1", s.Items.Single(i => i.Category == "apps" && i.Key == "com.exemple.inventaire").Value);
            Assert.AreEqual("11.2.14", s.Items.Single(i => i.Category == "sysapps" && i.Key == "com.symbol.datawedge").Value);
            Assert.IsTrue(s.Items.Any(i => i.Category == "props" && i.Key == "ro.config.ringtone"));
            Assert.IsTrue(s.Items.Any(i => i.Category == "settings" && i.Key == "system/screen_off_timeout"));
        }

        [TestMethod]
        public void Comparaison_TrouveLesVraiesDifferences()
        {
            var a = Make("A", "2024-01-01", "60000", "2.1", false, false);
            var b = Make("B", "2024-06-01", "300000", "2.3", true, true);
            var c = SnapshotComparer.Compare(a, b);
            var diffs = c.Rows.Where(r => r.Kind != DiffKind.Same).ToList();

            var patch = diffs.Single(r => r.Key == "ro.build.version.security_patch");
            Assert.AreEqual("Correctif de sécurité", patch.Label);
            Assert.AreEqual("2024-01-01", patch.Left);
            Assert.AreEqual("2024-06-01", patch.Right);
            Assert.AreEqual("Mise en veille de l'écran (ms)", diffs.Single(r => r.Key == "system/screen_off_timeout").Label);
            Assert.AreEqual(DiffKind.Different, diffs.Single(r => r.Key == "com.exemple.inventaire").Kind);
            var extra = diffs.Single(r => r.Key == "com.exemple.extra" && r.CategoryKey == "apps");
            Assert.AreEqual(DiffKind.OnlyRight, extra.Kind);
            Assert.AreEqual("(non installée)", extra.Left);
            var cam = diffs.Single(r => r.CategoryKey == "disabled");
            Assert.AreEqual("active", cam.Left);
            Assert.AreEqual("désactivée", cam.Right);
            Assert.AreEqual(5, c.DifferenceCount, string.Join(" | ", diffs.Select(d => d.CategoryKey + ":" + d.Key)));
            Assert.AreEqual("identity", c.Rows[0].CategoryKey, "l'identité et le système d'abord");
            StringAssert.Contains(c.Summary, "5 différence(s)");
        }

        [TestMethod]
        public void Comparaison_IdentiqueEtCsv()
        {
            var a = Make("A", "2024-01-01", "60000", "2.1", false, false);
            var b = Make("B", "2024-01-01", "60000", "2.1", false, false);
            var c = SnapshotComparer.Compare(a, b);
            Assert.AreEqual(0, c.DifferenceCount);
            StringAssert.StartsWith(c.Summary, "Aucune différence");
            var csv = c.ToCsv(true).Split('\n');
            StringAssert.StartsWith(csv[0], "Catégorie;Élément;Clé;");
            Assert.IsTrue(csv.Length > 5);
            Assert.AreEqual(1, c.ToCsv(false).Split('\n').Count(l => l.Trim().Length > 0), "sans les identiques : seulement l'en-tête");
        }

        [TestMethod]
        public void Fichier_EnregistrerRelire_EtHistoriqueSansDoublon()
        {
            string dir = Path.Combine(Path.GetTempPath(), "adbt_snap_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string previous = AppSettings.Current.HistoryFolder;
            try
            {
                var a = Make("SN123", "2024-01-01", "60000", "2.1", false, false);
                a.Name = "Référence entrepôt";
                string path = Path.Combine(dir, "ref" + DeviceSnapshot.FileExtension);
                a.Save(path);
                var back = DeviceSnapshot.Load(path);
                Assert.AreEqual("Référence entrepôt", back.Name);
                Assert.AreEqual(a.Items.Count, back.Items.Count);
                Assert.AreEqual(0, SnapshotComparer.Compare(a, back).DifferenceCount);

                AppSettings.Current.HistoryFolder = dir;
                Assert.IsNotNull(DeviceSnapshot.SaveToHistory(a));
                var again = Make("SN123", "2024-01-01", "60000", "2.1", false, false);
                again.Date = a.Date.AddMinutes(5);
                Assert.IsNull(DeviceSnapshot.SaveToHistory(again), "configuration inchangée : pas de nouvel état");
                var changed = Make("SN123", "2024-06-01", "60000", "2.1", false, false);
                changed.Date = a.Date.AddMinutes(10);
                Assert.IsNotNull(DeviceSnapshot.SaveToHistory(changed));
                var hist = DeviceSnapshot.LoadHistory("SN123");
                Assert.AreEqual(2, hist.Count);
                Assert.AreEqual("2024-06-01", hist[0].Items.Single(i => i.Key == "ro.build.version.security_patch").Value);
                Assert.AreEqual(0, DiagHistory.LoadAll().Count, "les états ne sont pas pris pour des fiches de diagnostic");
            }
            finally
            {
                AppSettings.Current.HistoryFolder = previous;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [TestMethod]
        public void Fichier_Invalide()
        {
            string path = Path.Combine(Path.GetTempPath(), "adbt_bad_" + Guid.NewGuid().ToString("N") + ".adbref");
            File.WriteAllText(path, "pas du json");
            try { Assert.ThrowsException<InvalidDataException>(() => DeviceSnapshot.Load(path)); }
            finally { File.Delete(path); }
        }
    }
}
