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
        /// <summary>Version du format : les Masters plus anciens sont remis au propre à l'ouverture (voir Normalize).</summary>
        public int FormatVersion { get; set; }
        public const int CurrentFormat = 3;

        public List<MasterApp> Apps { get; set; } = new List<MasterApp>();
        public List<MasterItem> DisabledApps { get; set; } = new List<MasterItem>();
        public List<MasterSetting> Settings { get; set; } = new List<MasterSetting>();
        public List<MasterWifi> WifiNetworks { get; set; } = new List<MasterWifi>();
        public MasterItem Launcher { get; set; }
        public MasterItem DeviceOwner { get; set; }
        public List<MasterFile> Files { get; set; } = new List<MasterFile>();
        public List<string> Commands { get; set; } = new List<string>();
        /// <summary>Ce qui ne se réinjecte pas par ADB (compte Google, Wi-Fi d'entreprise, certificat…) : étapes à faire à la main,
        /// rappelées dans le bilan, avec l'écran du terminal ouvert au bon endroit.</summary>
        public List<MasterStep> Steps { get; set; } = new List<MasterStep>();

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
            MasterProfile m;
            using (var fs = File.OpenRead(manifest))
                m = (MasterProfile)new XmlSerializer(typeof(MasterProfile)).Deserialize(fs);
            m.Normalize();
            return m;
        }

        /// <summary>
        /// Remet au propre un Master d'un format antérieur : réglages techniques propres au terminal retirés, réglages hors
        /// catalogue décochés, réseaux Wi-Fi en double fusionnés, comptes créés par le MDM retirés des étapes manuelles.
        /// </summary>
        public void Normalize()
        {
            if (FormatVersion >= CurrentFormat) return;
            if (FormatVersion < 2)
            {
                Settings.RemoveAll(st => IsDeviceSpecificSetting(st.Key));
                foreach (var st in Settings)
                    if (MasterSettingsCatalog.Find(st.Namespace, st.Key) == null) st.Include = false;
                WifiNetworks = MergeWifi(WifiNetworks);
                Steps.RemoveAll(st => st.Title.Contains("(com.google.work)") || st.Title.Contains("android-for-work"));
            }
            if (FormatVersion < 3)
            {
                // Origine des applis inconnue avant le format 3 : celles du fabricant ou de Google, déjà présentes sur les terminaux, sont décochées.
                foreach (var a in Apps.Where(x => !x.FromPc && x.Origin.Length == 0))
                {
                    a.Origin = AppOrigin("", "", a.Package, "/data/app/");
                    if (a.Origin == "vendor") a.Include = false;
                }
            }
            FormatVersion = CurrentFormat;
        }

        // Applis livrées avec les terminaux (fabricant, Google) : déjà présentes, à ne réinstaller qu'en connaissance de cause.
        private static readonly Regex VendorPackage = new Regex(
            @"^(com\.zebra\.|com\.symbol\.|com\.motorolasolutions\.|com\.honeywell\.|com\.intermec\.|com\.datalogic\.|com\.cipherlab\.|com\.urovo\.|com\.newland\.|" +
            @"com\.sec\.|com\.samsung\.|com\.google\.android\.(safetycore|verifier|apps\.|gms|gsf|webview|trichromelibrary|tts|inputmethod|googlequicksearchbox|marvin|projection)|com\.google\.ar\.core$|com\.android\.)",
            RegexOptions.Compiled);
        private static readonly Regex MdmInstaller = new Regex(@"soti|airwatch|intune|companyportal|mobileiron|ivanti|jamf|miradore|hexnode|stagenow|mdna|clyd|42gears|scalefusion|knox|managedprovisioning|esper", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Origine d'une appli : system (appli système mise à jour), vendor (livrée par le fabricant ou Google), store (Play Store),
        /// mdm (installée par le MDM), manual (installée à la main : ADB, fichier APK).
        /// </summary>
        public static string AppOrigin(string pkgFlags, string installer, string package, string codePath)
        {
            installer = installer ?? "";
            if (Regex.IsMatch(pkgFlags ?? "", @"\bSYSTEM\b|UPDATED_SYSTEM_APP") || ((codePath ?? "").Length > 0 && !(codePath ?? "").StartsWith("/data/"))) return "system";
            if (MdmInstaller.IsMatch(installer)) return "mdm";
            if (VendorPackage.IsMatch(package ?? "")) return "vendor";
            if (installer == "com.android.vending" || installer == "com.google.android.feedback") return "store";
            return "manual";
        }

        /// <summary>« dumpsys package &lt;paquet&gt; » → indicateurs, installeur, chemin et nom de version.</summary>
        public static (string Flags, string Installer, string CodePath, string VersionName) ParsePackageInfo(string dumpsys)
        {
            string d = dumpsys ?? "";
            string Get(string rx) { var m = Regex.Match(d, rx); return m.Success ? m.Groups[1].Value.Trim() : ""; }
            string installer = Get(@"installerPackageName=(\S+)");
            if (installer == "null") installer = "";
            return (Get(@"pkgFlags=\[([^\]]*)\]") + " " + Get(@"\bprivateFlags=\[([^\]]*)\]"), installer, Get(@"codePath=(\S+)"), Get(@"versionName=(\S+)"));
        }

        /// <summary>Compare deux versionCode (nombres) : négatif si a &lt; b, 0 si égaux ou non comparables.</summary>
        public static int CompareVersionCodes(string a, string b) =>
            long.TryParse((a ?? "").Trim(), out var x) && long.TryParse((b ?? "").Trim(), out var y) ? x.CompareTo(y) : 0;

        /// <summary>Compte créé automatiquement par le MDM (Google Play d'entreprise…) : rien à refaire à la main.</summary>
        public static bool IsMdmManagedAccount(string name, string type) =>
            (type ?? "") == "com.google.work" || (name ?? "").Contains("android-for-work");

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
            @"ble_scan_.*|.*_count|.*_counter|setup_wizard_.*|.*dropbox.*|.*_version|" +
            // Données techniques du système ou de Google, propres au terminal ou recalculées par Android :
            @"database_creation_buildid|.*_url|.*_uri|config_update_certificate|pubkey_blacklist|serial_blacklist|.*phenotype.*|" +
            @"rfid_.*|display_default_.*|smart_docking_.*|smart_default_density_values|xternal_display_.*|.*_state_flag|.*_tracker_flag|" +
            @"managed_provisioning_.*|.*trust_agents_initialized|pai_selection_page_complete|user_setup_personalization_state|migrated_.*|" +
            @"nas_settings_updated|zen_mode_config_etag|theft_.*|device_owner_type|setup_skipped|setup_type|init_.*|.*_constants|" +
            @"wear_.*|clockwork_.*|.*_package|paired_device_.*|play_determined_.*|.*_cache|.*_sound|android_wear_.*|tethered_config_state)$",
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
                list.Add(new MasterWifi { Ssid = m.Groups[2].Value.Trim(), Security = m.Groups[3].Value.Trim().TrimEnd('^'), Include = true });
            }
            return MergeWifi(list);
        }

        /// <summary>Un même réseau listé plusieurs fois (WPA2 et WPA3 en mode transition) → une seule ligne, un seul mot de passe.</summary>
        public static List<MasterWifi> MergeWifi(List<MasterWifi> networks) =>
            (networks ?? new List<MasterWifi>()).GroupBy(w => w.Ssid).Select(g =>
            {
                var first = g.First();
                return new MasterWifi
                {
                    Ssid = g.Key,
                    Security = string.Join("/", g.Select(w => (w.Security ?? "").TrimEnd('^')).Where(x => x.Length > 0).Distinct()),
                    Password = g.Select(w => w.Password).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "",
                    Include = g.Any(w => w.Include),
                    Hidden = g.Any(w => w.Hidden),
                };
            }).ToList();

        /// <summary>Type attendu par « cmd wifi connect-network » (open, wpa2, wpa3), ou vide si non réinjectable (WEP, entreprise).</summary>
        public static string WifiConnectType(string security)
        {
            string s = (security ?? "").ToLowerInvariant();
            if (s.Contains("eap") || s.Contains("802") || s.Contains("wep") || s.Contains("enterprise")) return "";
            // WPA2 d'abord : un réseau en mode transition (wpa2-psk/wpa3-sae) accepte la connexion WPA2.
            if (s.Contains("psk") || s.Contains("wpa2")) return "wpa2";
            if (s.Contains("sae") || s.Contains("wpa3")) return "wpa3";
            if (s.Contains("wpa")) return "wpa2";
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

        /// <summary>« dumpsys account » → comptes du terminal (nom, type), sans doublon.</summary>
        public static List<KeyValuePair<string, string>> ParseAccounts(string dumpsys) =>
            Regex.Matches(dumpsys ?? "", @"Account \{name=([^,}]+), type=([^}]+)\}").Cast<Match>()
                .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()))
                .Distinct().ToList();

        /// <summary>Saisie d'un réglage : « system screen_off_timeout=600000 » → (espace, clé, valeur), ou null.</summary>
        public static MasterSetting ParseSettingInput(string input)
        {
            var m = Regex.Match((input ?? "").Trim(), @"^(system|secure|global)\s+([A-Za-z0-9_.:\-]+)\s*=\s*(.*)$", RegexOptions.IgnoreCase);
            return m.Success ? new MasterSetting { Namespace = m.Groups[1].Value.ToLowerInvariant(), Key = m.Groups[2].Value, Value = m.Groups[3].Value } : null;
        }

        /// <summary>Écrans du terminal qu'une étape manuelle peut ouvrir (code → libellé français).</summary>
        public static readonly string[][] StepScreens =
        {
            new[] { "", "Aucun écran" },
            new[] { "google", "Ajout d'un compte Google" },
            new[] { "accounts", "Ajout d'un compte" },
            new[] { "wifi", "Wi-Fi" },
            new[] { "security", "Sécurité / certificats" },
            new[] { "date", "Date et heure" },
            new[] { "locale", "Langue" },
            new[] { "settings", "Paramètres" },
        };

        /// <summary>Arguments de « am start » pour ouvrir l'écran d'une étape, ou vide.</summary>
        public static string StepScreenCommand(string screen)
        {
            switch (screen ?? "")
            {
                case "google": return "am start -a android.settings.ADD_ACCOUNT_SETTINGS --esa account_types com.google";
                case "accounts": return "am start -a android.settings.ADD_ACCOUNT_SETTINGS";
                case "wifi": return "am start -a android.settings.WIFI_SETTINGS";
                case "security": return "am start -a android.settings.SECURITY_SETTINGS";
                case "date": return "am start -a android.settings.DATE_SETTINGS";
                case "locale": return "am start -a android.settings.LOCALE_SETTINGS";
                case "settings": return "am start -a android.settings.SETTINGS";
                default: return "";
            }
        }

        private static readonly Dictionary<string, string> KnownApps = new Dictionary<string, string>
        {
            { "net.soti.mobicontrol.androidwork", "SOTI MobiControl" }, { "net.soti.settingsmanager", "SOTI Settings Manager" },
            { "net.soti.mobicontrol.elm", "SOTI MobiControl" }, { "net.soti.surf", "SOTI Surf" }, { "net.soti.hub", "SOTI Hub" },
            { "com.zebra.oemconfig.release", "Zebra OEMConfig" }, { "com.zebra.oemconfig.common", "Zebra OEMConfig" },
            { "com.symbol.datawedge", "DataWedge" }, { "com.symbol.mxmf.csp.enterprisekeyboard", "Zebra Enterprise Keyboard" },
            { "com.zebra.mdna.enterprisebrowser", "Zebra Enterprise Browser" }, { "com.zebra.devicediagnostics", "Zebra Device Diagnostics" },
            { "com.honeywell.oemconfig", "Honeywell OEMConfig" }, { "com.datalogic.settings", "Datalogic Settings" },
            { "com.google.android.safetycore", "Android System SafetyCore" }, { "com.google.android.verifier", "Google Play Protect (vérification)" },
            { "com.google.ar.core", "Services Google Play pour la RA" }, { "com.android.chrome", "Chrome" },
            { "com.microsoft.teams", "Microsoft Teams" }, { "com.microsoft.emmx", "Microsoft Edge" }, { "com.microsoft.windowsintune.companyportal", "Portail d'entreprise Intune" },
            { "com.android.launcher3", "Lanceur Android standard" }, { "com.zebra.mdna.els", "Zebra Enterprise Home Screen" },
            { "com.airwatch.androidagent", "Workspace ONE Intelligent Hub" }, { "com.google.android.inputmethod.latin", "Gboard" },
            { "com.android.inputmethod.latin", "Clavier Android (AOSP)" }, { "com.android.contacts", "Contacts" },
        };

        /// <summary>Nom lisible d'une appli connue (« net.soti… » → « SOTI MobiControl »), ou vide.</summary>
        public static string KnownAppName(string packageOrComponent)
        {
            string pkg = (packageOrComponent ?? "").Split('/')[0];
            return KnownApps.TryGetValue(pkg, out var n) ? Loc.T(n) : "";
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
        /// <summary>Origine sur le terminal de référence : system, vendor, store, mdm, manual (voir MasterProfile.AppOrigin).</summary>
        public string Origin { get; set; } = "";
        public string Installer { get; set; } = "";
        public string VersionName { get; set; } = "";
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
        /// <summary>Réseau masqué (SSID non diffusé).</summary>
        public bool Hidden { get; set; }
        public bool Include { get; set; } = true;
    }

    public sealed class MasterStep
    {
        public string Title { get; set; } = "";
        /// <summary>Précision pour le technicien (identifiant à saisir, où trouver le certificat…).</summary>
        public string Detail { get; set; } = "";
        /// <summary>Écran à ouvrir sur le terminal (voir MasterProfile.StepScreens).</summary>
        public string Screen { get; set; } = "";
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
