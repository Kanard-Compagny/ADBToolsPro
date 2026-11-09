using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Ce qu'annonce un paquet de mise à jour Android (.zip) dans META-INF/com/android/metadata : modèles visés
    /// (pre-device), version de départ exigée pour une mise à jour différentielle (pre-build) et version
    /// obtenue (post-build). Sert à écarter, avant de flasher, un terminal pour lequel le paquet n'est pas prévu.
    /// Les paquets sans ce fichier (certains constructeurs) ne sont pas vérifiés : le recovery du terminal
    /// refusera de toute façon un paquet qui ne lui est pas destiné.
    /// </summary>
    public sealed class OsUpdatePackage
    {
        public string FilePath { get; private set; }
        public long Size { get; private set; }
        public bool HasMetadata { get; private set; }
        public List<string> PreDevices { get; private set; } = new List<string>();
        public List<string> PreBuilds { get; private set; } = new List<string>();
        public string PostBuild { get; private set; } = "";
        public string OtaType { get; private set; } = "";
        /// <summary>Date de construction de la version obtenue (secondes Unix, « post-timestamp » ou ro.build.date.utc du manifeste), 0 si inconnue.</summary>
        public long PostTimestamp { get; private set; }
        /// <summary>Paquet de retour à une version antérieure (« ota-downgrade=yes ») : Android l'accepte mais efface le terminal.</summary>
        public bool AllowsDowngrade { get; private set; }
        /// <summary>Le paquet efface les données du terminal (« ota-wipe=yes » ou retour arrière).</summary>
        public bool Wipes { get; private set; }
        /// <summary>Modèles listés par le DeviceManifest.xml des paquets Zebra (ro.product.name), vide sinon.</summary>
        public List<string> ManifestModels { get; private set; } = new List<string>();

        /// <summary>Mise à jour différentielle : ne s'installe que sur une version précise.</summary>
        public bool IsIncremental => PreBuilds.Count > 0;

        public static OsUpdatePackage Read(string zipPath)
        {
            var pkg = new OsUpdatePackage { FilePath = zipPath, Size = new FileInfo(zipPath).Length };
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.GetEntry("META-INF/com/android/metadata");
                if (entry != null)
                    using (var r = new StreamReader(entry.Open(), Encoding.UTF8))
                        pkg.ApplyMetadata(r.ReadToEnd());
                var manifest = zip.Entries.FirstOrDefault(e => e.Name.Equals("DeviceManifest.xml", StringComparison.OrdinalIgnoreCase) && e.Length < 1_000_000);
                if (manifest != null)
                    using (var r = new StreamReader(manifest.Open(), Encoding.UTF8))
                        pkg.ApplyDeviceManifest(r.ReadToEnd());
            }
            return pkg;
        }

        /// <summary>Fonction pure, testable : lit le contenu du fichier metadata (« clé=valeur » par ligne).</summary>
        public static OsUpdatePackage FromMetadata(string metadata)
        {
            var pkg = new OsUpdatePackage { FilePath = "" };
            pkg.ApplyMetadata(metadata);
            return pkg;
        }

        /// <summary>Fonction pure, testable : lit les modèles d'un DeviceManifest.xml Zebra (product/ro.product.name, _npi, _ng).</summary>
        public void ApplyDeviceManifest(string xml)
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(xml ?? "");
                ManifestModels = doc.Descendants()
                    .Where(e => e.Name.LocalName.StartsWith("ro.product.name", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(e => ((string)e.Attribute("value") ?? "").Split(','))
                    .Select(v => v.Trim()).Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (PostTimestamp == 0)
                {
                    var date = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ro.build.date.utc");
                    if (date != null && long.TryParse((string)date.Attribute("value"), out var t)) PostTimestamp = t;
                }
            }
            catch { ManifestModels = new List<string>(); }
        }

        private void ApplyMetadata(string metadata)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (metadata ?? "").Replace("\r", "").Split('\n'))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                map[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
            }
            HasMetadata = map.Count > 0;
            PreDevices = SplitList(map, "pre-device");
            PreBuilds = SplitList(map, "pre-build");
            PostBuild = map.TryGetValue("post-build", out var pb) ? pb : "";
            OtaType = map.TryGetValue("ota-type", out var ot) ? ot : "";
            if (map.TryGetValue("post-timestamp", out var ts) && long.TryParse(ts, out var t)) PostTimestamp = t;
            AllowsDowngrade = map.TryGetValue("ota-downgrade", out var dg) && dg.Equals("yes", StringComparison.OrdinalIgnoreCase);
            Wipes = AllowsDowngrade || (map.TryGetValue("ota-wipe", out var wp) && wp.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> SplitList(Dictionary<string, string> map, string key) =>
            map.TryGetValue(key, out var v)
                ? v.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
                : new List<string>();

        /// <summary>Résumé lisible pour le technicien.</summary>
        public string Describe()
        {
            var parts = new List<string> { Loc.F("{0:0.0} Mo", Size / 1048576.0) };
            if (!HasMetadata && ManifestModels.Count == 0) { parts.Add(Loc.T("pas de description dans le paquet : le terminal vérifiera lui-même")); return string.Join(" — ", parts); }
            if (PreDevices.Count > 0) parts.Add(Loc.F("pour {0}", string.Join(", ", PreDevices)));
            if (ManifestModels.Count > 0) parts.Add(Loc.F("{0} modèles listés par le fabricant", ManifestModels.Count));
            parts.Add(IsIncremental ? Loc.T("mise à jour différentielle (depuis une version précise)") : Loc.T("mise à jour complète"));
            if (AllowsDowngrade) parts.Add(Loc.T("retour arrière autorisé (efface le terminal)"));
            else if (Wipes) parts.Add(Loc.T("efface les données du terminal"));
            if (PostBuild.Length > 0) parts.Add(Loc.F("version obtenue : {0}", ShortBuild(PostBuild)));
            return string.Join(" — ", parts);
        }

        /// <summary>« Zebra/TC58/TC58:14/14-35-10.00-UG-U166-STD-ATH-04/1176:user/release-keys » → « 14-35-10.00-UG-U166-STD-ATH-04 ».</summary>
        public static string ShortBuild(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return "";
            var parts = fingerprint.Split('/');
            return parts.Length >= 5 ? parts[3] : fingerprint;
        }

        public enum Fit { Ok, AlreadyThere, Downgrade, Blocked }

        /// <summary>
        /// Propriétés où un terminal peut porter le nom visé par « pre-device ». Les constructeurs ne visent pas tous
        /// le même : Zebra vise la plateforme (« athena » pour TC53 / TC58…), alors que ro.product.device vaut « TC58 ».
        /// </summary>
        public static readonly string[] DeviceIdProps =
        {
            "ro.product.device", "ro.build.product", "ro.build.platform", "ro.product.platform", "ro.product.vendor.device", "ro.product.system.device",
            "ro.product.odm.device", "ro.product.board", "ro.board.platform", "ro.product.name", "ro.hardware", "ro.boot.hardware",
            "ro.boot.product.hardware.sku", "ro.product.vendor.name"
        };

        /// <summary>Fonction pure, testable : noms du terminal lus dans ses propriétés (sans doublon ni vide).</summary>
        public static List<string> DeviceIdsFrom(IDictionary<string, string> props)
        {
            var ids = DeviceIdProps.Select(k => props != null && props.TryGetValue(k, out var v) ? (v ?? "").Trim() : "").ToList();
            // ro.build.flavor = « athena-user » : la plateforme, sans le type de build.
            if (props != null && props.TryGetValue("ro.build.flavor", out var flavor) && !string.IsNullOrEmpty(flavor))
                ids.Add(Regex.Replace(flavor.Trim(), @"-(user|userdebug|eng)$", ""));
            return ids.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Le paquet convient-il à ce terminal ? <paramref name="deviceIds"/> : ses noms possibles (voir
        /// <see cref="DeviceIdsFrom"/>), le premier servant dans le message ; <paramref name="fingerprint"/> = ro.build.fingerprint.
        /// </summary>
        public (Fit Result, string Message) Check(IList<string> deviceIds, string fingerprint) => Check(deviceIds, fingerprint, 0);

        /// <param name="deviceBuildUtc">ro.build.date.utc du terminal : sert à repérer un retour à une version plus ancienne.</param>
        public (Fit Result, string Message) Check(IList<string> deviceIds, string fingerprint, long deviceBuildUtc)
        {
            if (!HasMetadata && ManifestModels.Count == 0) return (Fit.Ok, "");
            deviceIds = deviceIds ?? new List<string>();
            // Noms acceptés : « pre-device » du paquet, plus les modèles de son DeviceManifest.xml (Zebra).
            var accepted = PreDevices.Concat(ManifestModels).ToList();
            if (accepted.Count > 0 && deviceIds.Count > 0 &&
                !accepted.Any(d => deviceIds.Any(id => string.Equals(d, id, StringComparison.OrdinalIgnoreCase))))
                return (Fit.Blocked, Loc.F("Paquet prévu pour {0}, pas pour ce modèle ({1})",
                    PreDevices.Count > 0 ? string.Join(", ", PreDevices) : string.Join(", ", ManifestModels.Take(6)) + (ManifestModels.Count > 6 ? "…" : ""), deviceIds[0]));
            // Même version : empreinte identique, ou même numéro de build (un paquet commun à plusieurs modèles
            // porte l'empreinte d'un seul d'entre eux).
            if (PostBuild.Length > 0 && !string.IsNullOrEmpty(fingerprint) &&
                (string.Equals(PostBuild, fingerprint, StringComparison.Ordinal) ||
                 string.Equals(ShortBuild(PostBuild), ShortBuild(fingerprint), StringComparison.Ordinal)))
                return (Fit.AlreadyThere, Loc.T("Déjà à cette version"));
            // Version plus ancienne que celle du terminal : Android refuse le retour arrière (protection contre
            // la réinstallation d'une version vulnérable), sauf paquet de « downgrade » du fabricant, qui efface tout.
            if (PostTimestamp > 0 && deviceBuildUtc > 0 && PostTimestamp < deviceBuildUtc)
                return AllowsDowngrade
                    ? (Fit.Downgrade, Loc.T("Retour à une version antérieure : ce paquet efface le terminal (applications et données)"))
                    : (Fit.Blocked, Loc.T("Version plus ancienne que celle du terminal : Android refuse le retour arrière. Il faut le paquet de « downgrade » du fabricant (il efface le terminal)."));
            if (IsIncremental && !string.IsNullOrEmpty(fingerprint) &&
                !PreBuilds.Any(b => b == fingerprint || ShortBuild(b) == ShortBuild(fingerprint)))
                return (Fit.Blocked, Loc.F("Mise à jour différentielle prévue depuis {0}", string.Join(", ", PreBuilds.Select(ShortBuild).Distinct())));
            return (Fit.Ok, "");
        }

        /// <summary>Raccourci pour un seul nom de terminal (tests).</summary>
        public (Fit Result, string Message) Check(string device, string fingerprint) =>
            Check(string.IsNullOrEmpty(device) ? new List<string>() : new List<string> { device }, fingerprint);

        // « serving: 'update.zip'  (~47%) » : progression affichée par adb pendant un sideload.
        public static readonly Regex SideloadProgress = new Regex(@"\(~\s*(\d{1,3})%\)", RegexOptions.Compiled);

        /// <summary>Fonction pure, testable : niveau de batterie lu dans « dumpsys battery » (-1 si absent).</summary>
        public static int ParseBatteryLevel(string dumpsysBattery)
        {
            var m = Regex.Match(dumpsysBattery ?? "", @"^\s*level:\s*(\d+)", RegexOptions.Multiline);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }
    }
}
