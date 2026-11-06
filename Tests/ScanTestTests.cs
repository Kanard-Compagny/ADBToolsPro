using System;
using System.Linq;
using System.Text;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class ScanTestTests
    {
        private static string Line(string source, string sym, string data, long ms = 1762370000000) =>
            $"I/ADBToolsDiag( 1234): SCAN\t{ms}\t{source}\t{sym}\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(data))}";

        [TestMethod]
        public void Journal_LectureDecodee()
        {
            var t0 = new DateTime(2026, 11, 5, 20, 0, 0);
            var r = ScanText.ParseLogLine(Line("datawedge", "LABEL-TYPE-EAN13", "3017620422003"), t0);
            Assert.AreEqual("3017620422003", r.Data);
            Assert.AreEqual("EAN-13", r.Symbology);
            Assert.AreEqual("DataWedge", r.SourceText);
            Assert.AreEqual(1762370000000, r.DeviceMs);
            Assert.IsNull(ScanText.ParseLogLine("I/ADBToolsDiag( 1234): READY\tZebra\tTC52\t11", t0));
            Assert.IsNull(ScanText.ParseLogLine("I/Autre( 1): SCAN\t1\tk\t\tAAAA", t0));
        }

        [TestMethod]
        public void CaracteresInvisiblesEtSymbologies()
        {
            Assert.AreEqual("]C101034531200000111719112510ABCD1234⟨GS⟩2110⟨TAB⟩", ScanText.Visible("]C101034531200000111719112510ABCD1234\u001d2110\t"));
            Assert.AreEqual("QR Code", ScanText.FriendlySymbology("s / ]Q1"));
            Assert.AreEqual("Code 128", ScanText.FriendlySymbology("LABEL-TYPE-CODE128"));
            StringAssert.StartsWith(ScanText.GuessSymbology("3017620422003"), "EAN-13");
            StringAssert.StartsWith(ScanText.GuessSymbology("96385074"), "EAN-8");
            Assert.AreEqual("non transmise (mode clavier)", ScanText.GuessSymbology("ABC-123"));
            Assert.IsFalse(ScanText.CheckDigitOk("3017620422004"));
        }

        [TestMethod]
        public void Session_GachetteDelaiRatesEtCodesAttendus()
        {
            var s = new ScanTestSession();
            s.SetExpected(ScanText.ParseExpected("3017620422003\r\n\r\nABC-123\n"));
            var t0 = new DateTime(2026, 11, 5, 20, 0, 0);

            s.AddTrigger(t0);
            s.AddRead(new ScanRead { Time = t0.AddMilliseconds(180), Data = "3017620422003", Source = "keyboard" });
            s.AddTrigger(t0.AddSeconds(10));                       // gâchette sans lecture
            s.AddTrigger(t0.AddSeconds(20));
            s.AddRead(new ScanRead { Time = t0.AddSeconds(20.3), Data = "XYZ", Source = "keyboard" });

            var now = t0.AddSeconds(60);
            Assert.AreEqual(2, s.Reads.Count);
            Assert.AreEqual(180, s.Reads[0].TriggerDelayMs.Value, 0.5);
            Assert.AreEqual(1, s.MissedTriggers(now));
            Assert.AreEqual(66.7, s.SuccessRate(now).Value, 0.1);
            Assert.AreEqual(true, s.Reads[0].Expected);
            Assert.AreEqual(false, s.Reads[1].Expected);
            Assert.AreEqual(1, s.ExpectedFound);
            Assert.AreEqual(1, s.Mismatches);

            var (icon, details, comment) = s.Verdict(now);
            Assert.AreEqual("⚠️", icon);
            StringAssert.Contains(details, "Codes attendus lus : 1 / 2");
            StringAssert.Contains(details, "Appuis sur la gâchette : 3 — sans lecture : 1");
            Assert.IsTrue(s.ToCsv().Split('\n')[1].StartsWith("1;20:00:00;3017620422003;13;EAN-13"));
        }

        [TestMethod]
        public void Session_SansLecture()
        {
            var s = new ScanTestSession();
            Assert.AreEqual("❌", s.Verdict(DateTime.Now).Icon);
            s.AddRead(new ScanRead { Time = DateTime.Now, Data = "OK" });
            Assert.AreEqual("✅", s.Verdict(DateTime.Now).Icon);
        }

        [TestMethod]
        public void Gachette_NomInconnu_ApprisAvantLaPremiereLecture()
        {
            var s = new ScanTestSession();
            var t0 = new DateTime(2026, 11, 6, 8, 0, 0);
            s.AddKeyPress("KEY_VOLUMEUP", t0.AddMilliseconds(-500));      // ignorée pour l'apprentissage
            s.AddKeyPress("KEY_F12", t0);
            s.AddRead(new ScanRead { Time = t0.AddMilliseconds(250), Data = "A" });
            s.AddKeyPress("KEY_F12", t0.AddSeconds(10));                 // appui sans lecture
            s.AddKeyPress("KEY_F12", t0.AddSeconds(20));
            s.AddRead(new ScanRead { Time = t0.AddSeconds(20.2), Data = "B" });
            Assert.AreEqual("KEY_F12", s.TriggerKey);
            Assert.AreEqual(3, s.TriggerCount);
            Assert.AreEqual(250, s.Reads[0].TriggerDelayMs.Value, 0.5);
            Assert.AreEqual(1, s.MissedTriggers(t0.AddSeconds(60)));
            Assert.AreEqual(66.7, s.SuccessRate(t0.AddSeconds(60)).Value, 0.1);
        }
    }
}
