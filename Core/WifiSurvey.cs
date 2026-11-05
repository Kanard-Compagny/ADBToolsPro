using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Relevé de la mesure Wi-Fi autonome (terminal débranché, voir DiagApp/SurveyService.java) : une ligne JSON
    /// par élément — en-tête, mesure (« s », chaque seconde), changement de borne, coupure, reconnexion, repère,
    /// fin. Converti ici en statistiques et graphiques de l'Analyse Wi-Fi, pour le même rapport Word.
    /// </summary>
    public sealed class WifiSurvey
    {
        public sealed class Sample
        {
            public DateTime Time;
            public bool Connected;
            public int? Rssi;
            public string Bssid = "";
            public string Ssid = "";
            public int Frequency;
            public int LinkMbps;
            public double? PingMs;
        }

        public sealed class Mark
        {
            public DateTime Time;
            public string Label = "";
            public int Rssi;
        }

        public string Manufacturer { get; private set; } = "";
        public string Model { get; private set; } = "";
        public string Android { get; private set; } = "";
        public string Target { get; private set; } = "";
        public string Ip { get; private set; } = "";
        public string Gateway { get; private set; } = "";
        public string Dns { get; private set; } = "";
        public DateTime Start { get; private set; }
        public DateTime End { get; private set; }
        public string EndReason { get; private set; } = "";
        public List<Sample> Samples { get; } = new List<Sample>();
        public List<Mark> Marks { get; } = new List<Mark>();
        /// <summary>Événements datés, déjà rédigés (changements de borne, coupures, repères).</summary>
        public List<string> Events { get; } = new List<string>();
        public RadarStats Stats { get; } = new RadarStats();
        public string SourceFile { get; set; } = "";

        public List<int?> PingHistory => Samples.Where(s => s.Connected).Select(s => s.PingMs.HasValue ? (int?)(int)Math.Round(s.PingMs.Value) : null).ToList();
        public List<int> RssiHistory => Samples.Where(s => s.Rssi.HasValue).Select(s => s.Rssi.Value).ToList();
        public Sample Last => Samples.LastOrDefault(s => s.Connected);

        private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

        public static WifiSurvey Load(string path)
        {
            var s = Parse(File.ReadAllLines(path, Encoding.UTF8));
            s.SourceFile = path;
            return s;
        }

        public static WifiSurvey Parse(IEnumerable<string> lines)
        {
            var w = new WifiSurvey();
            w.Stats.Reset();
            bool any = false;
            foreach (var raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                JsonElement e;
                try { using (var doc = JsonDocument.Parse(line)) e = doc.RootElement.Clone(); }
                catch { continue; } // dernière ligne coupée (batterie vide, appli arrêtée…) : ignorée
                string type = Str(e, "type");
                DateTime t = FromMs(Long(e, "t"));
                if (!any && type != "header") { w.Start = t; }
                any = true;
                switch (type)
                {
                    case "header":
                        w.Start = t;
                        w.Manufacturer = Str(e, "manufacturer"); w.Model = Str(e, "model"); w.Android = Str(e, "android");
                        w.Target = Str(e, "target"); w.Ip = Str(e, "ip"); w.Gateway = Str(e, "gateway"); w.Dns = Str(e, "dns");
                        break;
                    case "s":
                    {
                        var smp = new Sample
                        {
                            Time = t,
                            Connected = e.TryGetProperty("conn", out var c) && c.ValueKind == JsonValueKind.True,
                            Bssid = Str(e, "bssid"),
                            Ssid = Str(e, "ssid"),
                            Frequency = (int)Long(e, "freq"),
                            LinkMbps = (int)Long(e, "link"),
                        };
                        if (e.TryGetProperty("rssi", out var r) && r.ValueKind == JsonValueKind.Number) smp.Rssi = r.GetInt32();
                        if (e.TryGetProperty("ping", out var p) && p.ValueKind == JsonValueKind.Number) smp.PingMs = p.GetDouble();
                        w.Samples.Add(smp);
                        if (smp.Rssi.HasValue) w.Stats.RecordRssi(smp.Rssi.Value);
                        if (smp.Connected)
                            w.Stats.RecordPing(new PingSample { Success = smp.PingMs.HasValue, LatencyMs = smp.PingMs.HasValue ? (int)Math.Round(smp.PingMs.Value) : 0 });
                        w.End = t;
                        break;
                    }
                    case "roam":
                        w.Stats.IncrementRoaming();
                        w.Events.Add($"[{t:HH:mm:ss}] " + Loc.F("🔁 Changement de borne : {0} → {1} ({2} dBm)", Str(e, "from"), Str(e, "to"), Long(e, "rssi")));
                        break;
                    case "disc":
                        w.Stats.IncrementDisconnect();
                        w.Events.Add($"[{t:HH:mm:ss}] " + Loc.F("❌ Coupure Wi-Fi (dernière borne {0})", Str(e, "bssid")));
                        break;
                    case "conn":
                        w.Events.Add($"[{t:HH:mm:ss}] " + Loc.F("✅ Reconnecté à {0}", Str(e, "bssid")));
                        break;
                    case "mark":
                        var mk = new Mark { Time = t, Label = Str(e, "label"), Rssi = (int)Long(e, "rssi") };
                        w.Marks.Add(mk);
                        w.Events.Add($"[{t:HH:mm:ss}] " + Loc.F("📍 Repère « {0} » ({1} dBm)", mk.Label, mk.Rssi));
                        break;
                    case "end":
                        w.End = t;
                        w.EndReason = Str(e, "reason");
                        break;
                }
            }
            if (w.End < w.Start) w.End = w.Start;
            w.Stats.StartTime = w.Start;
            w.Stats.StopTime = w.End;
            return w;
        }

        /// <summary>
        /// Bilan par zone : de chaque repère au suivant (ou à la fin), signal moyen et minimal, pertes de ping.
        /// C'est ce qui permet de dire « au quai 3, le signal tombe à -82 dBm ».
        /// </summary>
        public List<string> ZoneSummary()
        {
            var list = new List<string>();
            for (int i = 0; i < Marks.Count; i++)
            {
                DateTime from = Marks[i].Time, to = i + 1 < Marks.Count ? Marks[i + 1].Time : End.AddSeconds(1);
                var zone = Samples.Where(s => s.Time >= from && s.Time < to).ToList();
                if (zone.Count == 0) { list.Add(Loc.F("📍 {0} : pas de mesure", Marks[i].Label)); continue; }
                var rssi = zone.Where(s => s.Rssi.HasValue).Select(s => s.Rssi.Value).ToList();
                int pings = zone.Count(s => s.Connected), lost = zone.Count(s => s.Connected && !s.PingMs.HasValue);
                int cuts = zone.Count(s => !s.Connected);
                string verdict = rssi.Count == 0 || cuts > 0 || rssi.Min() < -80 ? "❌" : rssi.Average() < -70 || lost > 0 ? "⚠️" : "✅";
                list.Add(verdict + " " + Loc.F("{0} : signal moyen {1} dBm (min {2}), pertes {3}/{4}{5}",
                    Marks[i].Label,
                    rssi.Count == 0 ? "--" : Math.Round(rssi.Average()).ToString(CultureInfo.InvariantCulture),
                    rssi.Count == 0 ? "--" : rssi.Min().ToString(CultureInfo.InvariantCulture),
                    lost, pings,
                    cuts > 0 ? Loc.F(", {0} s sans Wi-Fi", cuts) : ""));
            }
            return list;
        }

        /// <summary>Texte du journal du rapport : bilan par zone, puis événements datés.</summary>
        public string JournalText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Loc.F("Mesure autonome du {0:dd/MM/yyyy HH:mm} au {1:HH:mm} — {2} mesure(s), cible du ping : {3}",
                Start, End, Samples.Count, string.IsNullOrEmpty(Target) ? "?" : Target));
            var zones = ZoneSummary();
            if (zones.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(Loc.T("Bilan par zone (d'un repère au suivant) :"));
                foreach (var z in zones) sb.AppendLine(z);
            }
            if (Events.Count > 0)
            {
                sb.AppendLine();
                foreach (var ev in Events) sb.AppendLine(ev);
            }
            return sb.ToString().TrimEnd();
        }

        public static string BandText(int freqMhz)
        {
            if (freqMhz <= 0) return "";
            if (freqMhz < 3000) return "2.4 GHz";
            if (freqMhz < 5925) return "5 GHz";
            return "6 GHz";
        }

        public static int Channel(int freqMhz)
        {
            if (freqMhz == 2484) return 14;
            if (freqMhz >= 2412 && freqMhz < 2484) return (freqMhz - 2407) / 5;
            if (freqMhz >= 5955) return (freqMhz - 5950) / 5;
            if (freqMhz >= 5000) return (freqMhz - 5000) / 5;
            return 0;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? "" : v.ToString()) : "";

        private static long Long(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l
            : e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
    }
}
