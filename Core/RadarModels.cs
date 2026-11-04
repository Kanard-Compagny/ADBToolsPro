using System;

namespace ADBToolsNew.Core
{
    /// <summary>Instantané de l'état Wi-Fi de l'appareil au moment du sondage.</summary>
    public class WifiSnapshot
    {
        public bool IsConnected;
        public string Ssid = "Inconnu";
        public string Bssid = "";
        public int? Rssi;
        public string LinkSpeed = "- Mbps";
        public string Band = "";
        public int Channel;

        /// <summary>Sortie brute de "dumpsys wifi" ayant servi à construire ce snapshot (utile pour les fallbacks MAC).</summary>
        public string RawDumpsysWifi = "";
    }

    /// <summary>Résultat d'une ligne "ping" individuelle.</summary>
    public struct PingSample
    {
        public bool Success;

        /// <summary>Latence en ms si Success, sinon une valeur conventionnelle (200) pour le graphique.</summary>
        public int LatencyMs;

        /// <summary>Numéro "icmp_seq" de la ligne, s'il est présent. Sert à détecter les paquets perdus.</summary>
        public int? Sequence;
    }

    /// <summary>
    /// Détecte les paquets perdus à partir des numéros "icmp_seq". Sur Android, "ping" n'écrit RIEN pour un
    /// paquet resté sans réponse : la seule trace d'une perte est un trou dans la numérotation
    /// (icmp_seq=4 puis icmp_seq=7 → 2 paquets perdus). Classe pure, testable isolément.
    /// </summary>
    public class PingSequenceTracker
    {
        // Au-delà, on considère que la numérotation a été réinitialisée (bouclage à 65535, process relancé)
        // plutôt que d'inventer des milliers de pertes.
        private const int MaxPlausibleGap = 3600;

        private int? _lastSequence;

        public void Reset() => _lastSequence = null;

        /// <summary>Retourne le nombre de paquets perdus entre la ligne précédente et celle-ci (0 si aucun).</summary>
        public int RegisterAndCountMissing(int? sequence)
        {
            if (!sequence.HasValue) return 0;

            int missing = 0;
            if (_lastSequence.HasValue)
            {
                int gap = sequence.Value - _lastSequence.Value - 1;
                if (gap > 0 && gap <= MaxPlausibleGap) missing = gap;
            }

            // Une réponse en retard (numéro inférieur) ne fait pas reculer le suivi.
            if (!_lastSequence.HasValue || sequence.Value > _lastSequence.Value || sequence.Value < _lastSequence.Value - MaxPlausibleGap)
                _lastSequence = sequence.Value;

            return missing;
        }
    }

    public enum RadarVerdict { Excellent, Moyen, Critique }

    /// <summary>
    /// Statistiques accumulées sur une session d'analyse Radar (RSSI, ping, roaming, déconnexions...).
    /// Regroupe la logique de remise à zéro/mise à jour auparavant dupliquée entre StartRadar et BtnResetRadar_Click.
    /// Les compteurs sont mis à jour depuis plusieurs threads (ping, logcat, sondage) : accès sous verrou.
    /// </summary>
    public class RadarStats
    {
        private readonly object _sync = new object();

        public int PingTotal;
        public int PingLost;
        public int MinRssi;
        public int MaxRssi;
        public double SumRssi;
        public int RssiCount;
        public int MinPing;
        public int MaxPing;
        public double SumPing;
        public int RoamingCount;
        public int DisconnectCount;
        public DateTime StartTime;

        /// <summary>Fin de la session (null tant qu'elle tourne). Sans elle, la durée affichée dans les
        /// rapports continuait d'augmenter après l'arrêt du Radar.</summary>
        public DateTime? StopTime;

        public RadarStats()
        {
            Reset();
        }

        /// <summary>Remet tout à zéro, session considérée comme arrêtée (durée nulle).</summary>
        public void Reset()
        {
            lock (_sync)
            {
                PingTotal = 0;
                PingLost = 0;
                MinRssi = 0;
                MaxRssi = -100;
                SumRssi = 0;
                RssiCount = 0;
                MinPing = 9999;
                MaxPing = 0;
                SumPing = 0;
                RoamingCount = 0;
                DisconnectCount = 0;
                StartTime = DateTime.Now;
                StopTime = StartTime;
            }
        }

        public void MarkStarted()
        {
            lock (_sync)
            {
                StartTime = DateTime.Now;
                StopTime = null;
            }
        }

        public void MarkStopped()
        {
            lock (_sync)
            {
                if (!StopTime.HasValue) StopTime = DateTime.Now;
            }
        }

        /// <summary>Durée de la session : jusqu'à maintenant si elle tourne, jusqu'à l'arrêt sinon.</summary>
        public TimeSpan Duration => (StopTime ?? DateTime.Now) - StartTime;

        /// <summary>Durée lisible, heures comprises (ex. "1 h 05 min 12 s"). Auparavant "duration.Minutes"
        /// faisait afficher "5 min" pour un test d'1 h 05.</summary>
        public static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            int hours = (int)duration.TotalHours;
            return hours > 0
                ? $"{hours} h {duration.Minutes:00} min {duration.Seconds:00} s"
                : $"{duration.Minutes} min {duration.Seconds:00} s";
        }

        public void RecordRssi(int rssi)
        {
            // -127 (et 0 ou plus) = valeur "invalide" remontée par certains pilotes quand le signal est
            // momentanément inconnu : elle fausserait le minimum et la moyenne.
            if (rssi <= -127 || rssi >= 0) return;

            lock (_sync)
            {
                if (rssi < MinRssi) MinRssi = rssi;
                if (rssi > MaxRssi) MaxRssi = rssi;
                SumRssi += rssi;
                RssiCount++;
            }
        }

        public void RecordPing(PingSample sample)
        {
            lock (_sync)
            {
                PingTotal++;
                if (!sample.Success)
                {
                    PingLost++;
                    return;
                }

                if (sample.LatencyMs < MinPing) MinPing = sample.LatencyMs;
                if (sample.LatencyMs > MaxPing) MaxPing = sample.LatencyMs;
                SumPing += sample.LatencyMs;
            }
        }

        // ------------------------------------------------------------------
        // Valeurs dérivées, calculées en UN seul endroit. Elles étaient recopiées dans l'affichage du Radar,
        // le rapport texte et le rapport Word (5 copies), avec un risque de divergence à chaque modification.
        // ------------------------------------------------------------------

        public int PingReceived => PingTotal - PingLost;

        public int AveragePingMs => PingReceived > 0 ? (int)Math.Round(SumPing / PingReceived) : 0;

        public int AverageRssi => RssiCount > 0 ? (int)Math.Round(SumRssi / RssiCount) : 0;

        public double LossPercent => PingTotal > 0 ? Math.Round(PingLost / (double)PingTotal * 100, 1) : 0;

        // Valeurs initiales (9999 / 0 / 0 / -100) = aucune mesure : affichées "--".
        public string MinPingText => MinPing == 9999 ? "--" : MinPing.ToString();
        public string MaxPingText => MaxPing == 0 ? "--" : MaxPing.ToString();
        public string MinRssiText => MinRssi == 0 ? "--" : MinRssi.ToString();
        public string MaxRssiText => MaxRssi == -100 ? "--" : MaxRssi.ToString();

        /// <summary>Verdict global de la session : CRITIQUE si plus de 5 % de pertes ou plus de 2 déconnexions,
        /// MOYEN si plus de 1 % de pertes, au moins une déconnexion ou une latence moyenne au-delà de 100 ms.</summary>
        public RadarVerdict Verdict
        {
            get
            {
                double loss = LossPercent;
                if (loss > 5 || DisconnectCount > 2) return RadarVerdict.Critique;
                if (loss > 1 || DisconnectCount > 0 || AveragePingMs > 100) return RadarVerdict.Moyen;
                return RadarVerdict.Excellent;
            }
        }

        public static string VerdictLabel(RadarVerdict verdict)
        {
            switch (verdict)
            {
                case RadarVerdict.Critique: return "CRITIQUE";
                case RadarVerdict.Moyen: return "MOYEN";
                default: return "EXCELLENT";
            }
        }

        public void IncrementRoaming()
        {
            lock (_sync) RoamingCount++;
        }

        public void IncrementDisconnect()
        {
            lock (_sync) DisconnectCount++;
        }
    }
}
