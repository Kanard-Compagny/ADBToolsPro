using System;
using System.Linq;
using System.Text;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class DeviceTestsTests
    {
        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        private static DiagMessage Result(string test, string status, string json) =>
            DiagMessage.Parse($"I/ADBToolsDiag( 4321): RESULT\t{test}\t{status}\t{B64(json)}");

        [TestMethod]
        public void Inventaire_DuMateriel()
        {
            var m = DiagMessage.Parse("I/ADBToolsDiag( 4321): FEATURES\t" + B64(
                "{\"manufacturer\":\"Zebra Technologies\",\"model\":\"TC52\",\"android\":\"11\",\"sdk\":30,\"camera_back\":true," +
                "\"camera_front\":false,\"flash\":true,\"nfc\":true,\"gps\":true,\"keyboard\":false,\"sensors\":[\"accelerometer\",\"light\"],\"scanner\":\"Zebra DataWedge\"}"));
            Assert.AreEqual("FEATURES", m.Type);
            var f = DeviceFeatures.From(m);
            Assert.AreEqual("TC52", f.Model);
            Assert.IsTrue(f.Has("camera_back"));
            Assert.IsFalse(f.Has("camera_front"));
            Assert.IsFalse(f.Has("keyboard"));
            Assert.IsFalse(f.Has("earpiece"), "clé absente = matériel absent");
            Assert.IsTrue(f.Has("sensors"));
            Assert.IsTrue(f.Has("scanner"));
            Assert.AreEqual("Zebra DataWedge", f.Scanner);
        }

        [TestMethod]
        public void Resultats_TextesLisibles()
        {
            StringAssert.Contains(DeviceTestText.Result(Result("microphone", "ok", "{\"peak_db\":-12.5,\"file\":\"/x.wav\"}")), "-12");
            StringAssert.Contains(DeviceTestText.Result(Result("gps", "warn", "{\"reason\":\"timeout\",\"satellites\":7,\"best_cn0\":31}")), "7 satellite(s)");
            Assert.AreEqual("Badge lu : UID 04A1B2C3 (NfcA, MifareUltralight)",
                DeviceTestText.Result(Result("nfc", "ok", "{\"uid\":\"04A1B2C3\",\"techs\":[\"NfcA\",\"MifareUltralight\"]}")));
            Assert.AreEqual("Non disponible sur ce terminal.", DeviceTestText.Result(Result("nfc", "na", "{\"reason\":\"absent\"}")));
            var sensors = DeviceTestText.Result(Result("sensors", "warn",
                "{\"accelerometer\":{\"status\":\"ok\"},\"light\":{\"status\":\"warn\"},\"proximity\":{\"status\":\"bad\"}}"));
            StringAssert.Contains(sensors, "Accéléromètre : ✅");
            StringAssert.Contains(sensors, "Capteur de lumière : ⚠️");
            StringAssert.Contains(sensors, "Capteur de proximité : ❌");
            StringAssert.Contains(DeviceTestText.Result(Result("keys", "confirm", "{\"keys\":[{\"key\":\"A\",\"count\":2},{\"key\":\"ENTER\",\"count\":1}]}")), "A ×2, ENTER ×1");
            Assert.AreEqual("❓", DeviceTestText.StatusIcon("confirm"));
            Assert.AreEqual("➖", DeviceTestText.StatusIcon("na"));
        }

        [TestMethod]
        public void Messages_EtatsEtDirect()
        {
            var st = DiagMessage.Parse("I/ADBToolsDiag( 1): STATE\tgps\tstopped");
            Assert.AreEqual("gps", st.Test);
            Assert.AreEqual("stopped", st.Status);
            var live = DiagMessage.Parse("I/ADBToolsDiag( 1): LIVE\tbluetooth\t" + B64("{\"devices\":3,\"best_rssi\":-58}"));
            Assert.AreEqual("3 appareil(s) trouvé(s), meilleur signal -58 dBm", DeviceTestText.Live(live));
            Assert.IsNull(DiagMessage.Parse("I/Autre( 1): RESULT\tx\tok\te30="));
            Assert.IsNull(DiagMessage.Parse("I/ADBToolsDiag( 1): SOMETHING\tx"));
        }

        [TestMethod]
        public void Catalogue_IdentifiantsUniquesEtElement()
        {
            Assert.AreEqual(DeviceTestCatalog.All.Count, DeviceTestCatalog.All.Select(d => d.Id).Distinct().Count());
            var item = new DeviceTestItem(DeviceTestCatalog.Get("flash")) { Available = true };
            Assert.IsFalse(item.IsReported);
            item.Status = "❓";
            Assert.IsFalse(item.IsReported, "à confirmer : pas encore dans le rapport");
            item.Status = "⚠️";
            Assert.IsTrue(item.IsReported);
            item.Available = false;
            Assert.AreEqual("Absent sur ce terminal", item.SubText);
        }

        [TestMethod]
        public void Confirmation_LaQuestionDevientUnConstat()
        {
            Assert.AreEqual("Le flash a clignoté normalement (3 fois).", DeviceTestText.Confirmed("flash", "✅", "Le flash a-t-il clignoté 3 fois ?"));
            Assert.AreEqual("Défaut constaté par le technicien (voir commentaire).", DeviceTestText.Confirmed("speaker", "❌", "Les 3 bips…"));
            Assert.AreEqual("Résultat constaté par le technicien.", DeviceTestText.Confirmed("gps", "✅", ""));
            Assert.AreEqual("Photo 10 × 10", DeviceTestText.Confirmed("camera_back", "✅", "Photo 10 × 10"));
            Assert.AreEqual("Touches détectées (1) : A ×1\nToutes les touches répondent.",
                DeviceTestText.Confirmed("keys", "✅", "Touches détectées (1) : A ×1\nToutes les touches du clavier sont-elles dans la liste ?"));
        }
    }
}
