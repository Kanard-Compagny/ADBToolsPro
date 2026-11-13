using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Onglet Outils avancés : état du terminal lu en UN seul appel adb, puis interprété ici (fonctions pures,
    /// testables). Chaque valeur illisible reste null : l'interface l'affiche alors comme inconnue au lieu
    /// d'inventer un état.
    /// </summary>
    public class TerminalToolsState
    {
        public bool? Wifi, Airplane, Bluetooth, Rotation, DarkMode, Nfc, Location, MobileData, AutoTime, AutoTimeZone;
        public bool HasNfc, HasTelephony, HasBluetooth, HasDataWedge;
        public bool SimAbsent;
        public int? BrightnessPercent, Dpi, PhysicalDpi, TimeoutMs, RingerMode, VolumeIndex, VolumeMax, StayOnPlugged, Sdk;
        public bool DpiOverridden;
        public string Resolution, TimeZone, Locale, Manufacturer, Model, AndroidVersion;
        public long? DeviceEpoch;

        /// <summary>Volume lu pour ce flux (3 = média par défaut).</summary>
        public int VolumeStream = 3;

        /// <summary>Les commandes, dans l'ordre attendu par <see cref="Parse"/>. "date" en premier : c'est elle
        /// qu'on compare à l'heure du PC prise juste avant l'appel.</summary>
        public static string[] Commands(int volumeStream) => new[]
        {
            "date +%s",
            "settings get global wifi_on",
            "settings get global airplane_mode_on",
            "settings get global bluetooth_on",
            "settings get system accelerometer_rotation",
            "cmd uimode night",
            "settings get system screen_brightness",
            "wm density",
            "wm size",
            "pm list features",
            "dumpsys nfc 2>/dev/null | grep -m1 mState",
            "settings get secure location_mode",
            "settings get global mobile_data",
            "getprop gsm.sim.state",
            "settings get system screen_off_timeout",
            "settings get global mode_ringer",
            $"cmd media_session volume --stream {volumeStream} --get 2>&1",
            "settings get global auto_time",
            "settings get global auto_time_zone",
            "getprop persist.sys.timezone",
            "l=$(getprop persist.sys.locale); [ -z \"$l\" ] && l=$(getprop ro.product.locale); echo $l",
            "settings get global stay_on_while_plugged_in",
            "getprop ro.build.version.sdk",
            "getprop ro.product.manufacturer",
            "pm list packages com.symbol.datawedge",
            "getprop ro.product.model",
            "getprop ro.build.version.release",
        };

        public static TerminalToolsState Parse(string[] s, int volumeStream = 3)
        {
            string At(int i) => s != null && i < s.Length ? (s[i] ?? "").Trim() : "";
            var t = new TerminalToolsState { VolumeStream = volumeStream };

            t.DeviceEpoch = long.TryParse(At(0), out long epoch) ? epoch : (long?)null;
            t.Wifi = OnOff(At(1));
            t.Airplane = Bool01(At(2));
            // bluetooth_on : 1 = actif, 0 = inactif, 2 = actif pour le BLE seul (vu comme inactif).
            t.Bluetooth = At(3) == "1" ? true : (At(3) == "0" || At(3) == "2") ? false : (bool?)null;
            t.Rotation = Bool01(At(4));
            string night = At(5).ToLowerInvariant();
            t.DarkMode = night.Contains("yes") ? true : night.Contains("no") ? false : (bool?)null;
            if (int.TryParse(At(6), out int b)) t.BrightnessPercent = Math.Max(0, Math.Min(100, (int)Math.Round(b / 2.55)));

            var over = Regex.Match(At(7), @"Override density:\s*(\d+)");
            var phys = Regex.Match(At(7), @"Physical density:\s*(\d+)");
            if (phys.Success) t.PhysicalDpi = int.Parse(phys.Groups[1].Value);
            if (over.Success) { t.Dpi = int.Parse(over.Groups[1].Value); t.DpiOverridden = true; }
            else t.Dpi = t.PhysicalDpi;
            var over2 = Regex.Match(At(8), @"Override size:\s*(\d+x\d+)");
            var size = Regex.Match(At(8), @"size:\s*(\d+x\d+)");
            t.Resolution = over2.Success ? over2.Groups[1].Value : size.Success ? size.Groups[1].Value : null;

            var features = new HashSet<string>(At(9).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("feature:"))
                .Select(l => l.Substring(8).Split('=')[0]), StringComparer.Ordinal);
            bool featuresRead = features.Count > 0;
            t.HasNfc = featuresRead ? features.Contains("android.hardware.nfc") : At(10).Length > 0;
            t.HasTelephony = featuresRead ? features.Contains("android.hardware.telephony") : true;
            t.HasBluetooth = !featuresRead || features.Contains("android.hardware.bluetooth");

            t.Nfc = ParseNfcState(At(10));
            if (int.TryParse(At(11), out int loc)) t.Location = loc != 0;
            t.MobileData = Bool01(At(12));
            string sim = At(13).ToUpperInvariant();
            t.SimAbsent = sim.Length == 0 || sim.Split(',').All(x => x == "ABSENT" || x == "NOT_READY" || x == "UNKNOWN" || x == "");
            if (int.TryParse(At(14), out int to)) t.TimeoutMs = to;
            if (int.TryParse(At(15), out int ring) && ring >= 0 && ring <= 2) t.RingerMode = ring;
            if (TryParseVolume(At(16), out int vol, out int max)) { t.VolumeIndex = vol; t.VolumeMax = max; }
            t.AutoTime = Bool01(At(17));
            t.AutoTimeZone = Bool01(At(18));
            t.TimeZone = NullIfEmpty(At(19));
            t.Locale = NullIfEmpty(At(20));
            if (int.TryParse(At(21), out int stay)) t.StayOnPlugged = stay;
            if (int.TryParse(At(22), out int sdk)) t.Sdk = sdk;
            t.Manufacturer = NullIfEmpty(At(23));
            t.HasDataWedge = At(24).Split('\n').Any(l => l.Trim() == "package:com.symbol.datawedge");
            t.Model = NullIfEmpty(At(25));
            t.AndroidVersion = NullIfEmpty(At(26));
            return t;
        }

        private static bool? Bool01(string v) => v == "1" ? true : v == "0" ? false : (bool?)null;

        /// <summary>"0" = éteint, tout autre entier = allumé (wifi_on vaut 2 quand le Wi-Fi survit au mode avion).</summary>
        public static bool? OnOff(string v) => int.TryParse((v ?? "").Trim(), out int n) ? n != 0 : (bool?)null;
        private static string NullIfEmpty(string v) => string.IsNullOrWhiteSpace(v) || v == "null" ? null : v.Trim();

        /// <summary>"mState=on" / "mState=off" (dumpsys nfc). Les états de transition comptent pour l'état visé.</summary>
        public static bool? ParseNfcState(string line)
        {
            var m = Regex.Match(line ?? "", @"mState\s*=\s*(\w+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string v = m.Groups[1].Value.ToLowerInvariant();
            if (v == "on" || v == "turning_on" || v == "3" || v == "2") return true;
            if (v == "off" || v == "turning_off" || v == "1" || v == "4") return false;
            return null;
        }

        /// <summary>"volume is 8 in range [0..15]" (cmd media_session volume --get).</summary>
        public static bool TryParseVolume(string output, out int index, out int max)
        {
            index = max = 0;
            var m = Regex.Match(output ?? "", @"volume is (\d+) in range \[(\d+)\.\.(\d+)\]");
            if (!m.Success) return false;
            index = int.Parse(m.Groups[1].Value);
            max = int.Parse(m.Groups[3].Value);
            return true;
        }

        // ---------------- Mise en veille ----------------

        /// <summary>Choix proposés, dans l'ordre des boutons. int.MaxValue = jamais.</summary>
        public static readonly int[] TimeoutChoices = { 30000, 60000, 300000, 1800000, int.MaxValue };

        /// <summary>Index du bouton qui correspond à la valeur lue, ou -1 pour une valeur personnalisée.</summary>
        public static int TimeoutIndex(int? ms)
        {
            if (!ms.HasValue) return -1;
            // Certains terminaux stockent « jamais » sous une très grande valeur autre que int.MaxValue.
            if (ms.Value >= 86400000) return TimeoutChoices.Length - 1;
            return Array.IndexOf(TimeoutChoices, ms.Value);
        }

        public static string FormatTimeout(int ms)
        {
            if (ms >= 86400000) return Loc.T("jamais");
            if (ms < 60000) return Loc.F("{0} s", ms / 1000);
            if (ms % 60000 == 0) return Loc.F("{0} min", ms / 60000);
            return Loc.F("{0} min {1} s", ms / 60000, (ms % 60000) / 1000);
        }

        // ---------------- Heure ----------------

        /// <summary>Écart (terminal − PC) en secondes, null si l'heure du terminal est illisible.</summary>
        public long? ClockOffsetSeconds(DateTime pcUtcAtCall)
        {
            if (!DeviceEpoch.HasValue) return null;
            long pc = (long)Math.Round((pcUtcAtCall - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            return DeviceEpoch.Value - pc;
        }

        /// <summary>Au-delà d'une minute, l'écart mérite d'être signalé (certificats, Wi-Fi entreprise, journaux).</summary>
        public const int ClockWarnSeconds = 60;

        public static string FormatDuration(long seconds)
        {
            seconds = Math.Abs(seconds);
            if (seconds < 60) return Loc.F("{0} s", seconds);
            if (seconds < 3600) return seconds % 60 == 0 ? Loc.F("{0} min", seconds / 60) : Loc.F("{0} min {1} s", seconds / 60, seconds % 60);
            if (seconds < 86400) return Loc.F("{0} h {1} min", seconds / 3600, (seconds % 3600) / 60);
            return Loc.F("{0} j {1} h", seconds / 86400, (seconds % 86400) / 3600);
        }

        /// <summary>"fr-FR" → "Français (France)" ; code tel quel s'il est inconnu.</summary>
        public static string LocaleName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            try
            {
                var ci = CultureInfo.GetCultureInfo(code.Replace('_', '-'));
                string n = ci.NativeName;
                return string.IsNullOrEmpty(n) ? code : char.ToUpper(n[0], ci) + n.Substring(1);
            }
            catch { return code; }
        }

        // ---------------- Touches et raccourcis ----------------

        /// <summary>
        /// Gâchette de scan logicielle, seulement là où elle a été vérifiée : Zebra avec DataWedge. null ailleurs
        /// (Honeywell, Datalogic : chaque marque a son propre service de lecture, à tester avant de le proposer).
        /// </summary>
        public string ScanTriggerCommand()
        {
            if (HasDataWedge && VendorModules.BrandOf(Manufacturer) == "Zebra")
                return "am broadcast -a com.symbol.datawedge.api.ACTION --es com.symbol.datawedge.api.SOFT_SCAN_TRIGGER TOGGLE_SCANNING";
            return null;
        }

        /// <summary>Une commande "am start" a-t-elle échoué ? (activité absente sur ce terminal, refus).</summary>
        public static bool StartFailed(string output) =>
            Regex.IsMatch(output ?? "", @"Error|Exception|unable to resolve|does not exist", RegexOptions.IgnoreCase);
    }
}
