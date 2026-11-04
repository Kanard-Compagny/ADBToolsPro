using System;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class RadarTests
    {
        private const string CmdWifiStatusConnecte =
            "Wifi is enabled\nWifi is connected to \"ReseauTest\"\n" +
            "WifiInfo: SSID: \"ReseauTest\", BSSID: 02:00:00:00:00:01, MAC: 02:00:00:00:00:02, Supplicant state: COMPLETED, " +
            "RSSI: -51, Link speed: 433Mbps, Tx Link speed: 433Mbps, Frequency: 5500MHz, Net ID: 0\n";

        [TestMethod]
        public void ParseWifiDump_CmdWifiStatus()
        {
            var s = RadarService.ParseWifiDump(CmdWifiStatusConnecte);

            Assert.IsTrue(s.IsConnected);
            Assert.AreEqual("ReseauTest", s.Ssid);
            Assert.AreEqual("02:00:00:00:00:01", s.Bssid);
            Assert.AreEqual(-51, s.Rssi);
            Assert.AreEqual("433Mbps", s.LinkSpeed);
            Assert.AreEqual("5 GHz", s.Band);
            Assert.AreEqual(100, s.Channel);
        }

        [TestMethod]
        public void ParseWifiDump_IgnoreLHistoriqueAvantLaConnexionCourante()
        {
            // Cas du mode avion : une ancienne entrée à 00:00... apparaît avant la ligne de la connexion courante.
            var s = RadarService.ParseWifiDump("historique BSSID: 00:00:00:00:00:00\nmWifiInfo SSID: \"Net\", BSSID: 02:00:00:00:00:01, RSSI: -55, Frequency: 2437MHz\n");

            Assert.IsTrue(s.IsConnected);
            Assert.AreEqual(6, s.Channel);
        }

        [TestMethod]
        public void ParseWifiDump_Canal14()
        {
            var s = RadarService.ParseWifiDump("WifiInfo: SSID: \"x\", BSSID: 11:22:33:44:55:66, RSSI: -60, Frequency: 2484MHz\n");
            Assert.AreEqual(14, s.Channel);
        }

        [TestMethod]
        public void CmdWifiStatus_Deconnecte()
        {
            Assert.IsTrue(RadarService.IsCmdWifiStatusDisconnected("Wifi is enabled\nWifi is not connected\n"));
            Assert.IsTrue(RadarService.IsCmdWifiStatusDisconnected("Wifi is disabled\n"));
        }

        [TestMethod]
        public void PingSequence_CompteLesPaquetsPerdus()
        {
            var tracker = new PingSequenceTracker();
            int lost = 0;
            foreach (var line in new[]
            {
                "64 bytes from 8.8.8.8: icmp_seq=1 ttl=117 time=12.3 ms",
                "64 bytes from 8.8.8.8: icmp_seq=2 ttl=117 time=11 ms",
                "64 bytes from 8.8.8.8: icmp_seq=5 ttl=117 time=20 ms",
                "From 10.0.0.1: icmp_seq=6 Destination Host Unreachable",
                "64 bytes from 8.8.8.8: icmp_seq=7 ttl=117 time=9 ms",
            })
            {
                lost += tracker.RegisterAndCountMissing(RadarService.ParsePingLine(line).Value.Sequence);
            }

            Assert.AreEqual(2, lost); // icmp_seq 3 et 4
        }

        [TestMethod]
        public void ParsePingLine_ResumeFinalIgnore()
        {
            Assert.IsNull(RadarService.ParsePingLine("2 packets transmitted, 1 received, 50% packet loss"));
        }

        [TestMethod]
        public void RadarStats_DureeEtVerdict()
        {
            Assert.AreEqual("1 h 05 min 12 s", RadarStats.FormatDuration(TimeSpan.FromMinutes(65.2)));

            var stats = new RadarStats();
            for (int i = 0; i < 94; i++) stats.RecordPing(new PingSample { Success = true, LatencyMs = 40 });
            for (int i = 0; i < 6; i++) stats.RecordPing(new PingSample { Success = false, LatencyMs = 200 });

            Assert.AreEqual(6.0, stats.LossPercent);
            Assert.AreEqual(40, stats.AveragePingMs);
            Assert.AreEqual(RadarVerdict.Critique, stats.Verdict);
            Assert.AreEqual("CRITIQUE", RadarStats.VerdictLabel(stats.Verdict));
        }

        [TestMethod]
        public void RadarStats_SansMesure_AfficheTirets()
        {
            var stats = new RadarStats();
            Assert.AreEqual("--", stats.MinPingText);
            Assert.AreEqual("--", stats.MaxRssiText);
            Assert.AreEqual(RadarVerdict.Excellent, stats.Verdict);
        }

        [TestMethod]
        public void RadarStats_RssiInvalideIgnore()
        {
            var stats = new RadarStats();
            stats.RecordRssi(-127);
            stats.RecordRssi(-60);
            Assert.AreEqual(1, stats.RssiCount);
            Assert.AreEqual(-60, stats.AverageRssi);
        }

        [TestMethod]
        public void RadarStats_DureeFigeeApresArret()
        {
            var stats = new RadarStats();
            stats.MarkStarted();
            stats.MarkStopped();
            var duration = stats.Duration;
            System.Threading.Thread.Sleep(20);
            Assert.AreEqual(duration, stats.Duration);
        }
    }
}
