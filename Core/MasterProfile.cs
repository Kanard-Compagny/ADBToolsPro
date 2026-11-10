using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Un « Master » : tout ce qu'il faut pour remettre un terminal en état après un wipe, capturé sur un terminal de
    /// référence puis réinjecté sur un ou plusieurs terminaux. Chaque élément peut être coché ou non avant d'appliquer.
    /// Fichier .adbmaster = archive zip : master.xml (cette description) + apks/ (applications) + files/ (fichiers).
    /// Ce qui ne se récupère pas sans root (données internes des applis, mots de passe Wi-Fi) n'est pas inventé :
    /// les mots de passe se saisissent dans le Master, et les données des applis restent l'affaire du MDM.
    /// </summary>
    [XmlRoot("Master")]
    public sealed class MasterProfile
    {
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string SourceModel { get; set; } = "";
        public string SourceManufacturer { get; set; } = "";
        public string SourceAndroid { get; set; } = "";
        public string SourceBuild { get; set; } = "";
        public string Notes { get; set; } = "";

        public List<MasterApp> Apps { get; set; } = new List<MasterApp>();
        public List<MasterItem> DisabledApps { get; set; } = new List<MasterItem>();
        public List<MasterSetting> Settings { get; set; } = new List<MasterSetting>();
        public List<MasterWifi> WifiNetworks { get; set; } = new List<MasterWifi>();
        public MasterItem Launcher { get; set; }
        public MasterItem DeviceOwner { get; set; }
        public List<MasterFile> Files { get; set; } = new List<MasterFile>();
        public List<string> Commands { get; set; } = new List<string>();

        public const string ManifestName = "master.xml";
        public const string FileExtension = ".adbmaster";

        // ------------------------------------------------------------------ Fichier

        /// <summary>Enregistre le dossier de travail (master.xml + apks/ + files/) dans une archive .adbmaster.</summary>
        public void SaveTo(string workDir, string archivePath)
        {
            Directory.CreateDirectory(workDir);
            using (var fs = File.Create(Path.Combine(workDir, ManifestName)))
                new XmlSerializer(typeof(MasterProfile)).Serialize(fs, this);
            string tmp = archivePath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            ZipFile.CreateFromDirectory(workDir, tmp, CompressionLevel.Fastest, false);
            if (File.Exists(archivePath)) File.Delete(archivePath);
            File.Move(tmp, archivePath);
        }

        /// <summary>Ouvre une archive .adbmaster dans un dossier de travail vide.</summary>
        public static MasterProfile Open(string archivePath, string workDir)
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);
            string root = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                foreach (var e in zip.Entries)
                {
                    // Chemins de l'archive gardés sous le dossier de travail (pas de « ..\ » qui en sortirait).
                    string dest = Path.GetFullPath(Path.Combine(workDir, e.FullName));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            }
            string manifest = Path.Combine(workDir, ManifestName);
            if (!File.Exists(manifest)) throw new InvalidDataException(Loc.T("Ce fichier n'est pas un Master ADB Tools (master.xml absent)."));
            using (var fs = File.OpenRead(manifest))
                return (MasterProfile)new XmlSerializer(typeof(MasterProfile)).Deserialize(fs);
        }

        // ------------------------------------------------------------------ Lecture du terminal (fonctions pures, testables)

        /// <summary>« settings list system » → (clé, valeur) ; la valeur peut contenir des « = ».</summary>
        public static List<KeyValuePair<string, string>> ParseSettingsList(string output) =>
            (output ?? "").Replace("\r", "").Split('\n')
                .Select(l => { int i = l.IndexOf('='); return i > 0 ? new KeyValuePair<string, string>(l.Substring(0, i).Trim(), l.Substring(i + 1)) : default(KeyValuePair<string, string>); })
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && Regex.IsMatch(kv.Key, @"^[A-Za-z0-9_.:\-]+$"))
                .ToList();

        // Réglages propres à chaque terminal ou à son historique : jamais recopiés d'un terminal à l'autre.
        private static readonly Regex DeviceSpecificSetting = new Regex(
            @"^(android_id|bluetooth_address|bluetooth_name|device_name|boot_count|user_setup_complete|device_provisioned|" +
            @"adb_enabled|adb_wifi_enabled|development_settings_enabled|install_non_market_apps|" +
            @"lock_screen_owner_info.*|.*_timestamp.*|.*last_.*|.*_uuid|.*_serial.*|.*_mac_address.*|" +
            @"wifi_networks_available_notification_on_.*|bugreport_in_power_menu|enabled_input_methods_for_.*|" +
            @"ble_scan_.*|.*_count|.*_counter|setup_wizard_.*|.*dropbox.*|.*_version)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsDeviceSpecificSetting(string key) => DeviceSpecificSetting.IsMatch(key ?? "");

        /// <summary>« pm list packages -3 --show-versioncode » → paquet et versionCode.</summary>
        public static List<KeyValuePair<string, string>> ParsePackagesWithVersion(string output) =>
            (output ?? "").Replace("\r", "").Split('\n')
                .Select(l => Regex.Match(l.Trim(), @"^package:(\S+?)(?:\s+versionCode:(\d+))?$"))
                .Where(m => m.Success)
                .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value))
                .ToList();

        /// <summary>« pm path » → chemins des APK (base + morceaux).</summary>
        public static List<string> ParsePmPath(string output) =>
            (output ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("package:")).Select(l => l.Substring(8)).Where(p => p.EndsWith(".apk")).ToList();

        /// <summary>Autorisations accordées à l'exécution, lues dans « dumpsys package &lt;paquet&gt; ».</summary>
        public static List<string> ParseGrantedRuntimePermissions(string dumpsys)
        {
            var result = new List<string>();
            bool inRuntime = false;
            foreach (var raw in (dumpsys ?? "").Replace("\r", "").Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("runtime permissions:", StringComparison.OrdinalIgnoreCase)) { inRuntime = true; continue; }
                if (!inRuntime) continue;
                var m = Regex.Match(l, @"^([A-Za-z0-9_.]+):\s*granted=(true|false)");
                if (m.Success) { if (m.Groups[2].Value == "true") result.Add(m.Groups[1].Value); continue; }
                if (l.Length == 0 || l.EndsWith(":")) inRuntime = false; // fin de la section (utilisateur suivant, autre bloc)
            }
            return result.Distinct().ToList();
        }

        /// <summary>« cmd wifi list-networks » (Android 11+) → réseaux enregistrés (nom, sécurité).</summary>
        public static List<MasterWifi> ParseWifiNetworks(string output)
        {
            var list = new List<MasterWifi>();
            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                var m = Regex.Match(raw, @"^\s*(\d+)\s+(.+?)\s{2,}(\S+)\s*$");
                if (!m.Success) continue;
                list.Add(new MasterWifi { Ssid = m.Groups[2].Value.Trim(), Security = m.Groups[3].Value.Trim(), Include = true });
            }
            return list;
        }

        /// <summary>Type attendu par « cmd wifi connect-network » (open, wpa2, wpa3), ou vide si non réinjectable (WEP, entreprise).</summary>
        public static string WifiConnectType(string security)
        {
            string s = (security ?? "").ToLowerInvariant();
            if (s.Contains("eap") || s.Contains("802") || s.Contains("wep") || s.Contains("enterprise")) return "";
            if (s.Contains("sae") || s.Contains("wpa3")) return "wpa3";
            if (s.Contains("psk") || s.Contains("wpa")) return "wpa2";
            if (s.Contains("open") || s.Contains("none") || s.Length == 0) return "open";
            return "";
        }

        /// <summary>« dumpsys device_policy » → composant propriétaire du terminal (paquet/classe), ou vide.</summary>
        public static string ParseDeviceOwner(string dumpsys)
        {
            var m = Regex.Match(dumpsys ?? "", @"Device Owner:[\s\S]{0,200}?admin=ComponentInfo\{([^}]+)\}");
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>« cmd package resolve-activity --brief … HOME » → lanceur par défaut (paquet/activité), ou vide.</summary>
        public static string ParseHomeActivity(string output)
        {
            var last = (output ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("/"));
            return last ?? "";
        }

        /// <summary>Valeur entre apostrophes pour le shell du terminal (« l'écran » → 'l'\''écran').</summary>
        public static string ShellQuote(string value) => "'" + (value ?? "").Replace("'", "'\\''") + "'";
    }

    public sealed class MasterItem
    {
        public string Name { get; set; } = "";
        public bool Include { get; set; } = true;
    }

    public sealed class MasterApp
    {
        public string Package { get; set; } = "";
        public string VersionCode { get; set; } = "";
        /// <summary>Fichiers APK, chemins relatifs au dossier du Master (base + morceaux).</summary>
        public List<string> Files { get; set; } = new List<string>();
        public List<string> Permissions { get; set; } = new List<string>();
        /// <summary>Ajoutée depuis le PC (pas capturée sur le terminal de référence).</summary>
        public bool FromPc { get; set; }
        public bool Include { get; set; } = true;
    }

    public sealed class MasterSetting
    {
        public string Namespace { get; set; } = "";   // system, secure, global
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Include { get; set; } = true;
    }

    public sealed class MasterWifi
    {
        public string Ssid { get; set; } = "";
        public string Security { get; set; } = "";
        public string Password { get; set; } = "";
        public bool Include { get; set; } = true;
    }

    public sealed class MasterFile
    {
        /// <summary>Chemin relatif au dossier du Master (fichier ou dossier).</summary>
        public string Local { get; set; } = "";
        /// <summary>Destination sur le terminal (dossier ou fichier).</summary>
        public string DevicePath { get; set; } = "";
        public bool IsFolder { get; set; }
        public bool Include { get; set; } = true;
    }
}
