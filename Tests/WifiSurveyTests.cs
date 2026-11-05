using System;
using System.Linq;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class WifiSurveyTests
    {
        private const long T0 = 1762372800000; // ms depuis 1970

        private static string S(int sec, int? rssi, string bssid, double? ping, bool conn = true) =>
            "{\"type\":\"s\",\"t\":" + (T0 + sec * 1000L) + ",\"conn\":" + (conn ? "true" : "false") +
            (rssi.HasValue ? ",\"rssi\":" + rssi + ",\"bssid\":\"" + bssid + "\",\"ssid\":\"Entrepot\",\"freq\":5180,\"link\":433" : "") +
            ",\"ping\":" + (ping.HasValue ? ping.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null") + "}";

        [TestMethod]
        public void Releve_StatistiquesEvenementsEtZones()
        {
            var lines = new[]
            {
                "{\"type\":\"header\",\"t\":" + T0 + ",\"manufacturer\":\"Zebra\",\"model\":\"TC52\",\"android\":\"11\",\"target\":\"192.168.1.1\",\"ip\":\"192.168.1.50\",\"gateway\":\"192.168.1.1\",\"dns\":\"192.168.1.1\"}",
                "{\"type\":\"mark\",\"t\":" + T0 + ",\"label\":\"Bureau\",\"rssi\":-50,\"bssid\":\"aa\"}",
                S(1, -50, "aa", 3.2), S(2, -52, "aa", 4.0),
                "{\"type\":\"mark\",\"t\":" + (T0 + 3000) + ",\"label\":\"Quai 3\",\"rssi\":-78,\"bssid\":\"aa\"}",
                S(3, -78, "aa", null), S(4, -84, "aa", 40),
                "{\"type\":\"roam\",\"t\":" + (T0 + 5000) + ",\"from\":\"aa\",\"to\":\"bb\",\"rssi\":-60}",
                S(5, -60, "bb", 5),
                "{\"type\":\"disc\",\"t\":" + (T0 + 6000) + ",\"bssid\":\"bb\"}",
                S(6, null, "", null, conn: false),
                "{\"type\":\"end\",\"t\":" + (T0 + 7000) + ",\"reason\":\"pc\",\"samples\":6}",
                "{\"type\":\"s\",\"t\":12", // dernière ligne coupée : ignorée
            };
            var w = WifiSurvey.Parse(lines);
            Assert.AreEqual("TC52", w.Model);
            Assert.AreEqual(6, w.Samples.Count);
            Assert.AreEqual(5, w.Stats.PingTotal, "pas de ping compté hors connexion");
            Assert.AreEqual(1, w.Stats.PingLost);
            Assert.AreEqual(1, w.Stats.RoamingCount);
            Assert.AreEqual(1, w.Stats.DisconnectCount);
            Assert.AreEqual(-84, w.Stats.MinRssi);
            Assert.AreEqual(TimeSpan.FromSeconds(7), w.Stats.Duration);
            Assert.AreEqual(5, w.PingHistory.Count);
            Assert.IsNull(w.PingHistory[2]);
            Assert.AreEqual("bb", w.Last.Bssid);
            Assert.AreEqual("5 GHz", WifiSurvey.BandText(w.Last.Frequency));
            Assert.AreEqual(36, WifiSurvey.Channel(5180));
            Assert.AreEqual(6, WifiSurvey.Channel(2437));

            var zones = w.ZoneSummary();
            Assert.AreEqual(2, zones.Count);
            StringAssert.StartsWith(zones[0], "✅ Bureau : signal moyen -51 dBm");
            StringAssert.StartsWith(zones[1], "❌ Quai 3");
            StringAssert.Contains(zones[1], "1 s sans Wi-Fi");
            string journal = w.JournalText();
            StringAssert.Contains(journal, "Changement de borne : aa → bb");
            StringAssert.Contains(journal, "Repère « Quai 3 »");
        }
    }
}
