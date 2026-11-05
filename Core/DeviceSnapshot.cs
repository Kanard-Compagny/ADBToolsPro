using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ADBToolsNew.Core
{
    /// <summary>Un élément de configuration relevé sur un terminal (propriété, réglage, application…).</summary>
    public sealed class SnapshotItem
    {
        /// <summary>Clé de catégorie stable : identity, settings, apps, sysapps, disabled, props.</summary>
        public string Category { get; set; } = "";
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>
    /// État de configuration d'un terminal à un instant donné : identité et version du système, réglages
    /// Android (global / system / secure), applications et leurs versions, applications système désactivées,
    /// propriétés. Sert au comparateur (deux terminaux, terminal ↔ référence, terminal ↔ son historique).
    /// Les valeurs propres à chaque appareil (numéro de série, adresses, compteurs, horodatages) sont écartées :
    /// elles différeraient toujours et masqueraient les vraies différences de configuration.
    /// </summary>
    public sealed class DeviceSnapshot
    {
        public int Format { get; set; } = 1;
        /// <summary>Nom libre (« Référence TC52 entrepôt »), vide pour un relevé simple.</summary>
        public string Name { get; set; } = "";
        public DateTime Date { get; set; } = DateTime.Now;
        public string Serial { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string Android { get; set; } = "";
        public List<SnapshotItem> Items { get; set; } = new List<SnapshotItem>();

        [JsonIgnore] public string FilePath { get; set; }

        /// <summary>« TC52 · 21345522 · 05/11/2026 16:30 » (ou le nom donné à une référence).</summary>
        [JsonIgnore]
        public string Title
        {
            get
            {
                string device = string.Join(" ", new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Name)) parts.Add(Name);
                if (device.Length > 0) parts.Add(device);
                if (!string.IsNullOrWhiteSpace(Serial)) parts.Add(Serial);
                parts.Add(Date.ToString("dd/MM/yyyy HH:mm"));
                return string.Join(" · ", parts);
            }
        }

        /// <summary>Libellé court pour une liste d'états enregistrés.</summary>
        [JsonIgnore] public string HistoryLabel => Date.ToString("dd/MM/yyyy HH:mm") + (string.IsNullOrWhiteSpace(Android) ? "" : " · Android " + Android)
                                                   + (string.IsNullOrWhiteSpace(Name) ? "" : " · " + Name);

        public const string FileExtension = ".adbref";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            FilePath = path;
        }

        public static DeviceSnapshot Load(string path)
        {
            DeviceSnapshot s;
            try { s = JsonSerializer.Deserialize<DeviceSnapshot>(File.ReadAllText(path)); }
            catch (Exception ex) { throw new InvalidDataException(Loc.F("Fichier de référence illisible : {0}", ex.Message)); }
            if (s == null || s.Items == null) throw new InvalidDataException(Loc.T("Ce fichier n'est pas un relevé de terminal ADB Tools."));
            s.FilePath = path;
            return s;
        }

        // ------------------------------------------------------------------ États enregistrés (historique)

        /// <summary>Dossier des états d'un terminal : celui de l'historique des diagnostics (par numéro de série).</summary>
        public static string HistoryDir(string serial) =>
            Path.Combine(DiagHistory.Folder, FileNameHelper.Sanitize(string.IsNullOrWhiteSpace(serial) ? "Inconnu" : serial));

        /// <summary>
        /// Range l'état dans l'historique du terminal, sauf s'il est identique au dernier état enregistré
        /// (inutile d'accumuler des copies). Renvoie le chemin, ou null si rien n'a été écrit.
        /// </summary>
        public static string SaveToHistory(DeviceSnapshot s, bool force = false)
        {
            if (s == null) return null;
            if (!force)
            {
                var last = LoadHistory(s.Serial).FirstOrDefault();
                if (last != null && SameContent(last, s)) return null;
            }
            string dir = HistoryDir(s.Serial);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{s.Date:yyyyMMdd_HHmmss}_Etat{FileExtension}");
            s.Save(path);
            return path;
        }

        /// <summary>États enregistrés d'un terminal, du plus récent au plus ancien (fichiers illisibles ignorés).</summary>
        public static List<DeviceSnapshot> LoadHistory(string serial)
        {
            var list = new List<DeviceSnapshot>();
            string dir = HistoryDir(serial);
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*" + FileExtension))
            {
                try { list.Add(Load(f)); } catch { }
            }
            return list.OrderByDescending(x => x.Date).ToList();
        }

        public static bool SameContent(DeviceSnapshot a, DeviceSnapshot b)
        {
            if (a == null || b == null || a.Items.Count != b.Items.Count) return false;
            return SnapshotComparer.Compare(a, b).DifferenceCount == 0;
        }

        // ------------------------------------------------------------------ Relevé sur le terminal

        public static async Task<DeviceSnapshot> CaptureAsync(AdbManager adb, string serial)
        {
            var props = await adb.GetAllPropsAsync(serial);
            var sections = await adb.RunShellSectionsAsync(serial, TimeSpan.FromSeconds(120),
                "settings list global",
                "settings list system",
                "settings list secure",
                "pm list packages -3",
                "pm list packages -d",
                "dumpsys package packages | grep -E '^ *Package \\[|versionName='");
            return Build(serial, props, sections[0], sections[1], sections[2], sections[3], sections[4], sections[5]);
        }

        /// <summary>Construit le relevé à partir des sorties ADB (fonction pure, testable).</summary>
        public static DeviceSnapshot Build(string serial, Dictionary<string, string> props, string settingsGlobal, string settingsSystem,
                                           string settingsSecure, string userPackages, string disabledPackages, string packageVersions)
        {
            props = props ?? new Dictionary<string, string>();
            string P(string k) => props.TryGetValue(k, out var v) ? v : "";
            var s = new DeviceSnapshot
            {
                Serial = serial ?? "",
                Manufacturer = P("ro.product.manufacturer"),
                Model = P("ro.product.model"),
                Android = P("ro.build.version.release"),
            };

            foreach (var key in IdentityProps)
            {
                string v = P(key);
                if (v.Length > 0) s.Items.Add(new SnapshotItem { Category = "identity", Key = key, Value = v });
            }

            AddSettings(s, "global", settingsGlobal);
            AddSettings(s, "system", settingsSystem);
            AddSettings(s, "secure", settingsSecure);

            var versions = ParsePackageVersions(packageVersions);
            var user = new HashSet<string>(ParsePackageList(userPackages), StringComparer.Ordinal);
            foreach (var pkg in user.OrderBy(p => p, StringComparer.Ordinal))
                s.Items.Add(new SnapshotItem { Category = "apps", Key = pkg, Value = versions.TryGetValue(pkg, out var v) && v.Length > 0 ? v : "?" });
            foreach (var kv in versions.Where(kv => !user.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                s.Items.Add(new SnapshotItem { Category = "sysapps", Key = kv.Key, Value = kv.Value.Length > 0 ? kv.Value : "?" });
            foreach (var pkg in ParsePackageList(disabledPackages).OrderBy(p => p, StringComparer.Ordinal))
                s.Items.Add(new SnapshotItem { Category = "disabled", Key = pkg, Value = "désactivée" });

            foreach (var kv in props.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (IdentityProps.Contains(kv.Key)) continue;
                if (!(kv.Key.StartsWith("ro.", StringComparison.Ordinal) || kv.Key.StartsWith("persist.", StringComparison.Ordinal))) continue;
                if (VolatileProp.IsMatch(kv.Key)) continue;
                s.Items.Add(new SnapshotItem { Category = "props", Key = kv.Key, Value = kv.Value ?? "" });
            }
            return s;
        }

        internal static readonly string[] IdentityProps =
        {
            "ro.product.manufacturer", "ro.product.brand", "ro.product.model", "ro.product.device",
            "ro.build.version.release", "ro.build.version.sdk", "ro.build.version.security_patch",
            "ro.build.display.id", "ro.build.version.incremental", "ro.build.fingerprint", "ro.build.type",
            "gsm.version.baseband", "ro.bootloader", "ro.device.patch.version",
            "persist.sys.locale", "ro.product.locale", "persist.sys.timezone", "ro.crypto.state",
        };

        // Valeurs propres à chaque appareil ou qui changent d'elles-mêmes : jamais comparées.
        private static readonly Regex VolatileSetting = new Regex(
            @"android_id|bluetooth_address|bluetooth_name|device_name|timestamp|last_|_last|boot_count|_count$|count_|uuid|session|nonce|random|time_remaining|_millis|_ms$|_time$|token|serial|adb_keys|data_activity|_seen|dropbox|uptime|usage_stats|network_watchlist|ntp_timestamp|wifi_networks_available|zen_duration|charging_started|recent|^db_|_epoch|alarm_|next_alarm|screensaver_components",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex VolatileProp = new Regex(
            @"serial|imei|meid|iccid|mac|boottime|bootreason|bootcount|boot\.reason|\.date|build\.host|build\.user|uuid|random|ro\.boot\.(?:serialno|uid|hardware\.revision|bootdevice)|vendor\.boot|persist\.sys\.boot|persist\.sys\.usb|persist\.vendor\.radio|persist\.radio|\.time|_time|lastknown|ro\.runtime|persist\.sys\.dalvik\.vm\.lib|persist\.sys\.ssr|vold|persist\.sys\.fuse|wifi\.direct|bt\.|bluetooth\.(?:address|name)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static void AddSettings(DeviceSnapshot s, string ns, string output)
        {
            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw.Substring(0, eq).Trim(), value = raw.Substring(eq + 1).Trim();
                if (key.Length == 0 || key.Contains(" ") || VolatileSetting.IsMatch(key)) continue;
                s.Items.Add(new SnapshotItem { Category = "settings", Key = ns + "/" + key, Value = value == "null" ? "" : value });
            }
        }

        public static List<string> ParsePackageList(string output) =>
            (output ?? "").Replace("\r", "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("package:", StringComparison.Ordinal))
                .Select(l => l.Substring(8).Trim())
                .Where(p => p.Length > 0)
                .Distinct()
                .ToList();

        private static readonly Regex PackageHeader = new Regex(@"^\s*Package \[([^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex VersionName = new Regex(@"versionName=(.*)$", RegexOptions.Compiled);

        /// <summary>« Package [com.x] (abc): » suivi de « versionName=1.2 » → com.x = 1.2.</summary>
        public static Dictionary<string, string> ParsePackageVersions(string output)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string current = null;
            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                var h = PackageHeader.Match(raw);
                if (h.Success) { current = h.Groups[1].Value; if (!map.ContainsKey(current)) map[current] = ""; continue; }
                var v = VersionName.Match(raw);
                if (v.Success && current != null && map[current].Length == 0) map[current] = v.Groups[1].Value.Trim();
            }
            return map;
        }
    }

    // ====================================================================== Comparaison

    public enum DiffKind { Same = 0, Different = 1, OnlyLeft = 2, OnlyRight = 3 }

    public sealed class SnapshotDiffRow
    {
        public string CategoryKey { get; set; } = "";
        public string Category { get; set; } = "";
        public string Label { get; set; } = "";
        public string Key { get; set; } = "";
        public string Left { get; set; } = "";
        public string Right { get; set; } = "";
        public DiffKind Kind { get; set; }
        internal int Order;

        public string KindKey => Kind == DiffKind.Same ? "same" : Kind == DiffKind.Different ? "diff" : "only";
        public string KindIcon => Kind == DiffKind.Same ? "＝" : Kind == DiffKind.Different ? "≠" : Kind == DiffKind.OnlyLeft ? "◀" : "▶";
    }

    public sealed class SnapshotComparison
    {
        public DeviceSnapshot Left { get; set; }
        public DeviceSnapshot Right { get; set; }
        public List<SnapshotDiffRow> Rows { get; } = new List<SnapshotDiffRow>();
        public int DifferenceCount => Rows.Count(r => r.Kind != DiffKind.Same);

        /// <summary>« Identité et système : 2 · Réglages : 5 · … » (catégories avec au moins une différence).</summary>
        public string Summary
        {
            get
            {
                var parts = Rows.Where(r => r.Kind != DiffKind.Same).GroupBy(r => r.Category)
                    .Select(g => g.Key + " : " + g.Count()).ToList();
                return parts.Count == 0 ? Loc.T("Aucune différence : les deux configurations sont identiques.")
                                        : Loc.F("{0} différence(s) — {1}", DifferenceCount, string.Join(" · ", parts));
            }
        }

        /// <summary>Export tableur (séparateur « ; », compatible Excel en français).</summary>
        public string ToCsv(bool includeSame)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(";", Csv(Loc.T("Catégorie")), Csv(Loc.T("Élément")), Csv(Loc.T("Clé")), Csv(Left?.Title ?? "A"), Csv(Right?.Title ?? "B"), Csv(Loc.T("Résultat"))));
            foreach (var r in Rows.Where(r => includeSame || r.Kind != DiffKind.Same))
                sb.AppendLine(string.Join(";", Csv(r.Category), Csv(r.Label), Csv(r.Key), Csv(r.Left), Csv(r.Right), Csv(KindText(r.Kind))));
            return sb.ToString();
        }

        public static string KindText(DiffKind k)
        {
            switch (k)
            {
                case DiffKind.Same: return Loc.T("Identique");
                case DiffKind.Different: return Loc.T("Différent");
                case DiffKind.OnlyLeft: return Loc.T("Seulement à gauche");
                default: return Loc.T("Seulement à droite");
            }
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }

    public static class SnapshotComparer
    {
        private static readonly string[] CategoryOrder = { "identity", "apps", "sysapps", "disabled", "settings", "props" };

        public static string CategoryLabel(string key)
        {
            switch (key)
            {
                case "identity": return Loc.T("Identité et système");
                case "apps": return Loc.T("Applications installées");
                case "sysapps": return Loc.T("Applications système");
                case "disabled": return Loc.T("Applications désactivées");
                case "settings": return Loc.T("Réglages Android");
                case "props": return Loc.T("Propriétés avancées");
                default: return key;
            }
        }

        /// <summary>Libellé lisible d'un élément (propriétés et réglages courants traduits).</summary>
        public static string ItemLabel(string category, string key)
        {
            if (category == "identity" || category == "settings")
            {
                string k = category == "settings" ? key.Substring(key.IndexOf('/') + 1) : key;
                if (Labels.TryGetValue(k, out var fr)) return Loc.T(fr);
            }
            return key;
        }

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            ["ro.product.manufacturer"] = "Fabricant",
            ["ro.product.brand"] = "Marque",
            ["ro.product.model"] = "Modèle",
            ["ro.product.device"] = "Nom de code de l'appareil",
            ["ro.build.version.release"] = "Version Android",
            ["ro.build.version.sdk"] = "Niveau d'API (SDK)",
            ["ro.build.version.security_patch"] = "Correctif de sécurité",
            ["ro.build.display.id"] = "Version du système (build)",
            ["ro.build.version.incremental"] = "Numéro de build",
            ["ro.build.fingerprint"] = "Empreinte du build",
            ["ro.build.type"] = "Type de build",
            ["gsm.version.baseband"] = "Bande de base (modem)",
            ["ro.bootloader"] = "Bootloader",
            ["ro.device.patch.version"] = "Correctif constructeur (LifeGuard)",
            ["persist.sys.locale"] = "Langue du système",
            ["ro.product.locale"] = "Langue d'origine",
            ["persist.sys.timezone"] = "Fuseau horaire",
            ["ro.crypto.state"] = "Chiffrement",
            ["screen_off_timeout"] = "Mise en veille de l'écran (ms)",
            ["screen_brightness"] = "Luminosité de l'écran",
            ["screen_brightness_mode"] = "Luminosité automatique",
            ["accelerometer_rotation"] = "Rotation automatique",
            ["font_scale"] = "Taille du texte",
            ["auto_time"] = "Date et heure automatiques",
            ["auto_time_zone"] = "Fuseau horaire automatique",
            ["time_12_24"] = "Format de l'heure",
            ["stay_on_while_plugged_in"] = "Écran allumé en charge",
            ["wifi_on"] = "Wi-Fi activé",
            ["wifi_sleep_policy"] = "Wi-Fi en veille",
            ["bluetooth_on"] = "Bluetooth activé",
            ["airplane_mode_on"] = "Mode avion",
            ["mobile_data"] = "Données mobiles",
            ["data_roaming"] = "Itinérance des données",
            ["adb_enabled"] = "Débogage USB",
            ["development_settings_enabled"] = "Options pour les développeurs",
            ["install_non_market_apps"] = "Sources inconnues",
            ["location_mode"] = "Localisation",
            ["location_providers_allowed"] = "Sources de localisation",
            ["default_input_method"] = "Clavier par défaut",
            ["enabled_input_methods"] = "Claviers activés",
            ["ntp_server"] = "Serveur de temps (NTP)",
            ["http_proxy"] = "Proxy HTTP",
            ["global_http_proxy_host"] = "Proxy global (hôte)",
            ["global_http_proxy_port"] = "Proxy global (port)",
            ["private_dns_mode"] = "DNS privé (mode)",
            ["private_dns_specifier"] = "DNS privé (serveur)",
            ["sound_effects_enabled"] = "Sons des touches",
            ["haptic_feedback_enabled"] = "Retour haptique",
            ["vibrate_when_ringing"] = "Vibrer pour les appels",
            ["lockscreen.disabled"] = "Écran de verrouillage désactivé",
            ["show_ime_with_hard_keyboard"] = "Clavier virtuel avec clavier physique",
            ["usb_mass_storage_enabled"] = "Stockage USB",
            ["window_animation_scale"] = "Échelle d'animation des fenêtres",
            ["transition_animation_scale"] = "Échelle d'animation des transitions",
            ["animator_duration_scale"] = "Durée des animations",
            ["package_verifier_enable"] = "Vérification des applications",
            ["captive_portal_mode"] = "Détection de portail captif",
            ["captive_portal_server"] = "Serveur de portail captif",
        };

        public static SnapshotComparison Compare(DeviceSnapshot left, DeviceSnapshot right)
        {
            var c = new SnapshotComparison { Left = left, Right = right };
            if (left == null || right == null) return c;
            var l = left.Items.GroupBy(i => i.Category + "\u0001" + i.Key).ToDictionary(g => g.Key, g => g.First());
            var r = right.Items.GroupBy(i => i.Category + "\u0001" + i.Key).ToDictionary(g => g.Key, g => g.First());

            foreach (var key in l.Keys.Union(r.Keys))
            {
                l.TryGetValue(key, out var a);
                r.TryGetValue(key, out var b);
                var any = a ?? b;
                var kind = a == null ? DiffKind.OnlyRight : b == null ? DiffKind.OnlyLeft
                         : string.Equals(a.Value ?? "", b.Value ?? "", StringComparison.Ordinal) ? DiffKind.Same : DiffKind.Different;
                string none = any.Category == "disabled" ? Loc.T("active")
                            : any.Category == "apps" || any.Category == "sysapps" ? Loc.T("(non installée)") : Loc.T("(absent)");
                c.Rows.Add(new SnapshotDiffRow
                {
                    CategoryKey = any.Category,
                    Category = CategoryLabel(any.Category),
                    Key = any.Key,
                    Label = ItemLabel(any.Category, any.Key),
                    Left = a == null ? none : DisplayValue(a),
                    Right = b == null ? none : DisplayValue(b),
                    Kind = kind,
                    Order = Array.IndexOf(CategoryOrder, any.Category) < 0 ? 99 : Array.IndexOf(CategoryOrder, any.Category),
                });
            }

            var sorted = c.Rows.OrderBy(x => x.Order).ThenBy(x => x.Kind == DiffKind.Same ? 1 : 0)
                .ThenBy(x => x.CategoryKey == "identity" ? Array.IndexOf(DeviceSnapshot.IdentityProps, x.Key) : 0)
                .ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            c.Rows.Clear();
            c.Rows.AddRange(sorted);
            return c;
        }

        private static string DisplayValue(SnapshotItem i) =>
            i.Category == "disabled" ? Loc.T("désactivée") : (i.Value ?? "");
    }
}
