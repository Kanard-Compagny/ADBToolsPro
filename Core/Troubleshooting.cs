using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ADBToolsNew.Core
{
    /// <summary>Un appareil Android vu par Windows sur l'USB (même si ADB ne le voit pas).</summary>
    public sealed class UsbAndroidDevice
    {
        public string Vid = "";
        public string Pid = "";
        public string Serial = "";
        public string Brand = "";
        /// <summary>Interfaces vues par Windows (nom, classe, code d'erreur du pilote).</summary>
        public readonly List<(string Name, string PnpClass, int Error)> Interfaces = new List<(string, string, int)>();

        public bool HasAdbInterface => Interfaces.Any(i => UsbDeviceScanner.IsAdbInterface(i.Name, i.PnpClass));
        public bool HasBootloaderInterface => Interfaces.Any(i => Regex.IsMatch(i.Name ?? "", "fastboot|bootloader", RegexOptions.IgnoreCase));
        public bool HasMtp => Interfaces.Any(i => (i.PnpClass ?? "").Equals("WPD", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(i.Name ?? "", @"\bMTP\b", RegexOptions.IgnoreCase));
        public bool DriverMissing => Interfaces.Any(i => i.Error == 28 || i.Error == 1 || i.Error == 10);
        public string DisplayName => Interfaces.Select(i => i.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n) && !Regex.IsMatch(n, "composite|USB Input|Périphérique USB composite", RegexOptions.IgnoreCase)) ?? Brand;
    }

    /// <summary>
    /// Lecture des appareils USB vus par Windows : sert à dire pourquoi ADB ne voit pas un terminal (câble de charge seule,
    /// débogage USB désactivé, pilote manquant, mode bootloader…). Toutes marques : les fabricants sont reconnus par leur
    /// identifiant USB (VID), et toute interface ADB est reconnue quel que soit le fabricant.
    /// </summary>
    public static class UsbDeviceScanner
    {
        // Identifiants USB (VID) des fabricants de terminaux Android courants.
        public static readonly Dictionary<string, string> KnownVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "05E0", "Zebra" }, { "0C2E", "Honeywell" }, { "05F9", "Datalogic" }, { "18D1", "Google / Android" }, { "04E8", "Samsung" },
            { "05C6", "Qualcomm" }, { "0E8D", "MediaTek" }, { "22B8", "Motorola" }, { "2717", "Xiaomi" }, { "12D1", "Huawei" },
            { "1004", "LG" }, { "0BB4", "HTC" }, { "2A70", "OnePlus" }, { "0FCE", "Sony" }, { "2D95", "Vivo" }, { "22D9", "Oppo" },
            { "1EBF", "Coolpad" }, { "17EF", "Lenovo" }, { "0B05", "Asus" }, { "1BBB", "Alcatel / TCL" }, { "2916", "Yota" },
        };

        /// <summary>« USB\VID_05E0&amp;PID_2101\21345678 » → (VID, PID, partie finale de l'identifiant).</summary>
        public static (string Vid, string Pid, string Tail) ParseDeviceId(string deviceId)
        {
            var m = Regex.Match(deviceId ?? "", @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(&MI_[0-9A-F]{2})?\\(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return ("", "", "");
            return (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant(), m.Groups[4].Value);
        }

        public static bool IsInterfaceId(string deviceId) => Regex.IsMatch(deviceId ?? "", @"&MI_[0-9A-F]{2}\\", RegexOptions.IgnoreCase);

        public static bool IsAdbInterface(string name, string pnpClass) =>
            Regex.IsMatch(name ?? "", @"\bADB\b|Android Composite|Android Debug", RegexOptions.IgnoreCase)
            || (pnpClass ?? "").Equals("AndroidUsbDeviceClass", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Regroupe les entrées Windows (appareil + ses interfaces) par appareil physique.
        /// entries : (DeviceID, Name, PNPClass, ConfigManagerErrorCode, ParentDeviceID ou vide).
        /// Ne garde que les fabricants connus ou les appareils qui exposent une interface ADB / MTP / bootloader.
        /// </summary>
        public static List<UsbAndroidDevice> Group(IEnumerable<(string DeviceId, string Name, string PnpClass, int Error)> entries)
        {
            var byKey = new Dictionary<string, UsbAndroidDevice>(StringComparer.OrdinalIgnoreCase);
            var list = entries.ToList();
            // 1. Appareils (sans &MI_) : la partie finale est le numéro de série.
            foreach (var e in list.Where(x => !IsInterfaceId(x.DeviceId)))
            {
                var id = ParseDeviceId(e.DeviceId);
                if (id.Vid.Length == 0) continue;
                string key = id.Vid + "|" + id.Pid;
                if (!byKey.TryGetValue(key + "|" + id.Tail, out var d))
                {
                    d = new UsbAndroidDevice { Vid = id.Vid, Pid = id.Pid, Serial = id.Tail.Contains("&") ? "" : id.Tail, Brand = KnownVendors.TryGetValue(id.Vid, out var b) ? b : "" };
                    byKey[key + "|" + id.Tail] = d;
                }
                d.Interfaces.Add((e.Name, e.PnpClass, e.Error));
            }
            // 2. Interfaces (&MI_xx) : rattachées à l'appareil de même VID/PID (le premier, faute de mieux).
            foreach (var e in list.Where(x => IsInterfaceId(x.DeviceId)))
            {
                var id = ParseDeviceId(e.DeviceId);
                if (id.Vid.Length == 0) continue;
                var d = byKey.Values.FirstOrDefault(x => x.Vid == id.Vid && x.Pid == id.Pid);
                if (d == null)
                {
                    d = new UsbAndroidDevice { Vid = id.Vid, Pid = id.Pid, Brand = KnownVendors.TryGetValue(id.Vid, out var b) ? b : "" };
                    byKey[id.Vid + "|" + id.Pid + "|mi"] = d;
                }
                d.Interfaces.Add((e.Name, e.PnpClass, e.Error));
            }
            return byKey.Values.Where(d => d.Brand.Length > 0 || d.HasAdbInterface || d.HasMtp || d.HasBootloaderInterface).ToList();
        }

        /// <summary>Lecture Windows (WMI). Vide si Windows refuse la lecture.</summary>
        public static List<UsbAndroidDevice> Scan()
        {
            var entries = new List<(string, string, string, int)>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT DeviceID, Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%'"))
                using (var results = searcher.Get())
                {
                    foreach (System.Management.ManagementObject o in results)
                    {
                        using (o)
                        {
                            int err = 0;
                            try { err = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0); } catch { }
                            entries.Add(((o["DeviceID"] as string) ?? "", (o["Name"] as string) ?? "", (o["PNPClass"] as string) ?? "", err));
                        }
                    }
                }
            }
            catch { }
            return Group(entries);
        }
    }

    /// <summary>Une façon d'entrer en recovery / bootloader (fastboot) pour une marque et des modèles donnés.</summary>
    /// <summary>Un appareil USB listé par « scrcpy --otg » quand il en voit plusieurs.</summary>
    public sealed class OtgUsbDevice
    {
        public string Serial = "";
        public string Vid = "";
        public string Pid = "";
        public string Label = "";
        public string Brand => UsbDeviceScanner.KnownVendors.TryGetValue(Vid, out var b) ? b : "";
    }

    /// <summary>
    /// Mode OTG de scrcpy : quand plusieurs appareils USB sont branchés (webcam, clavier, lecteur…), scrcpy refuse de choisir
    /// et liste les appareils. On retrouve ici le ou les terminaux dans cette liste, toutes marques confondues.
    /// </summary>
    public static class ScrcpyOtg
    {
        /// <summary>scrcpy demande de choisir un appareil (« Select a device via -s »).</summary>
        public static bool NeedsSerial(string output) =>
            Regex.IsMatch(output ?? "", @"Select a device via -s|Multiple \(\d+\) USB devices", RegexOptions.IgnoreCase);

        /// <summary>Lignes « --> 0001   (0408:5483)  Quanta HP HD Camera » → appareils.</summary>
        public static List<OtgUsbDevice> ParseDevices(string output)
        {
            var list = new List<OtgUsbDevice>();
            foreach (var raw in (output ?? "").Replace("\r", "").Split('\n'))
            {
                var m = Regex.Match(raw, @"(?:-->|\s)\s*(\S+)\s+\(([0-9A-Fa-f]{4}):([0-9A-Fa-f]{4})\)\s*(.*)$");
                if (!m.Success) continue;
                string serial = m.Groups[1].Value;
                if (list.Any(d => d.Serial == serial)) continue;
                list.Add(new OtgUsbDevice
                {
                    Serial = serial,
                    Vid = m.Groups[2].Value.ToUpperInvariant(),
                    Pid = m.Groups[3].Value.ToUpperInvariant(),
                    Label = m.Groups[4].Value.Trim(),
                });
            }
            return list;
        }

        // Périphériques du PC qui ne sont jamais un terminal Android.
        private static readonly Regex NotAPhone = new Regex(
            @"camera|webcam|cam\b|hub|keyboard|clavier|mouse|souris|bluetooth|fingerprint|empreinte|audio|headset|casque|receiver|récepteur|card reader|lecteur de carte|smart ?card|dock|ethernet|wireless|wi-?fi|unifying|touchpad|pen\b|stylet|printer|imprimante|ups\b|onduleur",
            RegexOptions.IgnoreCase);

        /// <summary>Appareils qui peuvent être le terminal : fabricant Android connu d'abord, sinon tout ce qui n'est pas un périphérique du PC.</summary>
        public static List<OtgUsbDevice> Candidates(IEnumerable<OtgUsbDevice> devices)
        {
            var all = devices.ToList();
            var known = all.Where(d => d.Brand.Length > 0).ToList();
            if (known.Count > 0) return known;
            return all.Where(d => !NotAPhone.IsMatch(d.Label)).ToList();
        }
    }

    public sealed class RecoveryKeyEntry
    {
        public string Brand { get; set; } = "";
        /// <summary>Modèles concernés, séparés par des virgules, avec * (ex. « ET40*, ET45* ») ; « * » = tous.</summary>
        public string Models { get; set; } = "*";
        /// <summary>recovery, fastboot.</summary>
        public string Mode { get; set; } = "recovery";
        public List<string> Steps { get; set; } = new List<string>();
        /// <summary>Origine de la procédure, en clair (nom du document), jamais un lien : la page fonctionne hors connexion.</summary>
        public string Source { get; set; } = "";
        /// <summary>Vrai si la procédure vient de la documentation du fabricant pour ces modèles.</summary>
        public bool Verified { get; set; }
        /// <summary>Ajoutée par l'utilisateur (fichier combinaisons-touches.json).</summary>
        public bool Custom { get; set; }
    }

    /// <summary>
    /// Combinaisons de touches pour entrer en recovery ou en fastboot, par marque et modèle. Une liste intégrée (sourcée) et
    /// un fichier de l'utilisateur, à côté du programme, qu'il complète avec ses propres modèles : il n'est jamais remplacé
    /// par les mises à jour.
    /// </summary>
    public static class RecoveryKeyGuide
    {
        public const string UserFileName = "combinaisons-touches.json";

        // Origine des procédures (affichée telle quelle, sans lien : la page fonctionne hors connexion).
        private const string SrcZebra = "Zebra, article d'assistance « Performing a factory reset » et guides de référence des modèles";
        private const string SrcZebraMc33 = "Zebra, article d'assistance « Enter Android Recovery for Pistol Grip and Brick MC33 devices »";
        private const string SrcHoneywell = "BIAS Technology (réparateur), d'après les guides utilisateur Honeywell";
        private const string SrcDatalogicKb = "Datalogic, base de connaissances (Memor 10)";
        private const string SrcDatalogicManual = "Datalogic, manuel utilisateur du Memor 20";
        private const string SrcDatalogicForum = "Datalogic, forum d'assistance";
        private const string SrcGoogle = "Google, aide Pixel";
        private const string SrcSamsung = "Opérateur AT&T (assistance XCover) et presse spécialisée Samsung";

        private const string NavVolume = "Dans le menu : Volume + / − pour se déplacer, Marche pour valider.";

        /// <summary>Zebra : « Redémarrer » puis maintenir une touche (tableau de l'article d'assistance Zebra).</summary>
        private static RecoveryKeyEntry Zebra(string models, string key, string offVariant = null, string note = null)
        {
            var e = new RecoveryKeyEntry { Brand = "Zebra", Models = models, Mode = "recovery", Verified = true, Source = SrcZebra };
            e.Steps.Add("Maintenir Marche jusqu'au menu, puis toucher « Redémarrer ».");
            e.Steps.Add("Pendant le redémarrage, maintenir " + key + " jusqu'à la vibration ou au logo Zebra : l'écran Android Recovery s'affiche.");
            if (offVariant != null) e.Steps.Add(offVariant);
            e.Steps.Add(note ?? NavVolume);
            return e;
        }

        private static RecoveryKeyEntry Usual(string brand, string mode, params string[] steps) =>
            new RecoveryKeyEntry { Brand = brand, Models = "*", Mode = mode, Verified = false, Steps = steps.ToList() };

        public static readonly List<RecoveryKeyEntry> BuiltIn = new List<RecoveryKeyEntry>
        {
            // ---------------------------------------------------------------- Zebra (documentation Zebra)
            Zebra("TC20*, TC21*, TC22*, TC25*, TC26*, TC27*, TC5*, TC7*, EC50*", "la touche PTT (bouton latéral)",
                "Terminal éteint : maintenir PTT et Marche jusqu'au logo Zebra."),
            new RecoveryKeyEntry { Brand = "Zebra", Models = "MC33*", Mode = "recovery", Verified = true, Source = SrcZebraMc33,
                Steps = { "Modèle pistolet : redémarrer en maintenant la gâchette ; la relâcher au logo Zebra.",
                          "Modèle droit (brique) : redémarrer en maintenant la touche de lecture droite ; la relâcher au logo Zebra.",
                          "Terminal éteint : maintenir la gâchette (ou la touche de lecture droite), puis Marche ; relâcher les deux au logo Zebra.",
                          NavVolume } },
            Zebra("MC22*, MC27*", "la touche P1"),
            Zebra("MC93*, MC94*", "la gâchette"),
            Zebra("ET40*, ET45*, ET51*, ET56*, KC50*", "Volume +", "Sur ET40 / ET45 : maintenir Volume + et Marche jusqu'au logo, puis relâcher Marche en gardant Volume +."),
            Zebra("ET60*, ET65*", "la touche PTT (ou P3 selon la version du guide Zebra)"),
            Zebra("L10*", "la touche Rotation d'écran (Android 8) ou Volume + (Android 10)"),
            Zebra("CC600*, CC6000*", "la touche 1"),
            Zebra("WT6000*, WT63*, WT64*", "la touche P1"),
            Zebra("VC80*", "la touche +"),
            Zebra("VC83*", "la touche bleue (modèle 10 pouces : touche bleue et Luminosité +)"),
            new RecoveryKeyEntry { Brand = "Zebra", Models = "WS50*", Mode = "recovery", Verified = true, Source = SrcZebra,
                Steps = { "Brancher le câble USB avant de commencer.", "Maintenir Marche jusqu'au menu, puis toucher « Redémarrer ».",
                          "Maintenir la touche Retour (bouton gauche) jusqu'au logo Zebra, puis la relâcher : l'écran System Recovery s'affiche." } },
            new RecoveryKeyEntry { Brand = "Zebra", Models = "WS50*", Mode = "fastboot", Verified = true, Source = SrcZebra,
                Steps = { "Brancher le câble USB, puis redémarrer le terminal.", "Au redémarrage, appuyer sur la gâchette de lecture : mode Fastboot." } },
            new RecoveryKeyEntry { Brand = "Zebra", Models = "PS20*", Mode = "recovery", Verified = true, Source = SrcZebra,
                Steps = { "Insérer l'outil de redémarrage Zebra (KT-MC18-REBOOT-05) dans le connecteur d'alimentation.",
                          "Maintenir la touche de lecture jusqu'à l'extinction de l'écran et au redémarrage : l'écran Android Recovery s'affiche." } },
            Usual("Zebra", "recovery",
                "Autres modèles Zebra : toucher « Redémarrer », puis maintenir la touche du modèle jusqu'au logo : PTT sur les terminaux tactiles, la gâchette sur les pistolets, Volume + sur les tablettes.",
                "Avec le débogage USB actif : « Redémarrer en recovery » (page Recovery) fait la même chose sans touche.",
                NavVolume),

            // ---------------------------------------------------------------- Honeywell (source tierce)
            new RecoveryKeyEntry { Brand = "Honeywell", Models = "CT45*, CT60*", Mode = "recovery", Source = SrcHoneywell,
                Steps = { "Maintenir Marche, puis choisir « Éteindre ».", "Terminal éteint : maintenir Volume −, puis appuyer sur Marche en gardant Volume −.",
                          "Relâcher Marche quand l'écran s'allume : le menu Recovery Mode s'affiche.",
                          "Volume + / − pour choisir (ex. « Full Factory Reset »), Marche pour valider." } },
            new RecoveryKeyEntry { Brand = "Honeywell", Models = "CN80*, EDA52*", Mode = "recovery", Source = SrcHoneywell,
                Steps = { "Maintenir Marche, puis choisir « Éteindre ».", "Terminal éteint : maintenir Volume −, puis appuyer sur Marche en gardant Volume − : le bootloader s'affiche.",
                          "Choisir « Recovery ».", "Dans la recovery, maintenir Volume − environ 10 secondes pour afficher toutes les options.", NavVolume } },
            new RecoveryKeyEntry { Brand = "Honeywell", Models = "CK65*", Mode = "recovery", Source = SrcHoneywell,
                Steps = { "Maintenir Marche, puis choisir « Éteindre ».", "Rallumer avec Marche.",
                          "À l'écran de démarrage Honeywell, appuyer en même temps sur Marche et la touche Shift verte ; relâcher quand le menu Recovery Mode s'affiche.",
                          "Touches Shift verte et orange pour choisir, Marche pour valider." } },
            Usual("Honeywell", "recovery",
                "Autres modèles Honeywell (famille Mobility Edge : CT40, CT47, EDA…) : terminal éteint, maintenir Volume −, puis appuyer sur Marche en gardant Volume −.",
                "Selon le modèle, le menu Recovery Mode s'affiche directement, ou un bootloader où choisir « Recovery ».",
                NavVolume),

            // ---------------------------------------------------------------- Datalogic
            new RecoveryKeyEntry { Brand = "Datalogic", Models = "Memor 10*", Mode = "recovery", Verified = true, Source = SrcDatalogicKb,
                Steps = { "Éteindre le terminal (maintenir Marche).", "Maintenir Marche et Volume + jusqu'au menu de démarrage.",
                          "Avec Volume + / −, choisir « Recovery Mode », valider avec Marche.", NavVolume } },
            new RecoveryKeyEntry { Brand = "Datalogic", Models = "Memor 20*", Mode = "recovery", Verified = true, Source = SrcDatalogicManual,
                Steps = { "Éteindre le terminal.", "Maintenir Marche et Volume + ; relâcher quand l'écran Recovery Mode s'affiche.", NavVolume } },
            new RecoveryKeyEntry { Brand = "Datalogic", Models = "Joya Touch A6*", Mode = "recovery", Verified = true, Source = SrcDatalogicForum,
                Steps = { "Éteindre le terminal.", "Appuyer sur la touche Marche (au dos) avec un trombone métallique, puis aussitôt sur la touche de lecture (dans les 0,2 à 0,3 s).",
                          "Dans le menu Recovery : faire défiler au toucher, valider avec la touche de lecture." } },
            Usual("Datalogic", "recovery",
                "Autres modèles Datalogic (Memor 11, Skorpio X5…) : terminal éteint, maintenir Marche et Volume + jusqu'au menu de démarrage ou à la recovery.",
                NavVolume),

            // ---------------------------------------------------------------- Google Pixel
            new RecoveryKeyEntry { Brand = "Google", Models = "*", Mode = "recovery", Verified = true, Source = SrcGoogle,
                Steps = { "Éteindre l'appareil.", "Maintenir Volume − et Marche 10 à 15 secondes, jusqu'au menu (trop longtemps : il redémarre, recommencer).",
                          "Avec Volume, choisir « Recovery mode », valider avec Marche.",
                          "Si « No command » s'affiche : maintenir Marche, appuyer une fois sur Volume +, puis relâcher les deux.", NavVolume } },
            new RecoveryKeyEntry { Brand = "Google", Models = "*", Mode = "fastboot", Verified = true, Source = SrcGoogle,
                Steps = { "Éteindre l'appareil.", "Maintenir Volume − et Marche : le premier menu affiché est le bootloader (fastboot)." } },

            // ---------------------------------------------------------------- Samsung (source tierce)
            new RecoveryKeyEntry { Brand = "Samsung", Models = "*", Mode = "recovery", Source = SrcSamsung,
                Steps = { "Éteindre l'appareil (Marche + Volume −, puis « Éteindre »).",
                          "Modèles récents (Android 11 et plus) : brancher d'abord le câble USB relié au PC.",
                          "Maintenir Volume + et Marche (touche latérale) jusqu'à l'allumage de l'écran.",
                          "Anciens modèles avec touche Accueil ou Bixby : Volume + et Accueil (ou Bixby) et Marche.", NavVolume } },

            // ---------------------------------------------------------------- Autres marques : procédures usuelles
            Usual("Motorola", "recovery", "Appareil éteint : maintenir Volume − et Marche : le bootloader s'affiche.", "Avec Volume, choisir « Recovery mode », valider avec Marche.",
                "Si « No command » s'affiche : maintenir Marche et appuyer une fois sur Volume +."),
            Usual("Xiaomi", "recovery", "Appareil éteint : maintenir Volume + et Marche jusqu'au logo.", NavVolume),
            Usual("Xiaomi", "fastboot", "Appareil éteint : maintenir Volume − et Marche jusqu'à l'écran Fastboot."),
            Usual("OnePlus", "recovery", "Appareil éteint : maintenir Volume − et Marche jusqu'au logo.", NavVolume),
            Usual("OnePlus", "fastboot", "Appareil éteint : maintenir Volume + et Marche."),
            Usual("Oppo / Realme", "recovery", "Appareil éteint : maintenir Volume − et Marche jusqu'au logo.", NavVolume),
            Usual("Huawei / Honor", "recovery", "Appareil éteint, câble USB débranché : maintenir Volume + et Marche jusqu'au logo.", NavVolume),
            Usual("Sony", "recovery", "Appareil éteint : maintenir Volume − et Marche jusqu'à la vibration.", NavVolume),
            Usual("Nokia", "recovery", "Appareil éteint, chargeur branché : maintenir Volume + et Marche jusqu'au logo.",
                "Si « No command » s'affiche : maintenir Marche et appuyer une fois sur Volume +."),
            Usual("Lenovo", "recovery", "Appareil éteint : maintenir Volume + et Marche jusqu'au logo.", NavVolume),

            // ---------------------------------------------------------------- Toutes marques
            Usual("Android (toutes marques)", "recovery",
                "Combinaison la plus courante : appareil éteint, maintenir Volume + et Marche jusqu'au logo.",
                "Si elle ne donne rien : Volume − et Marche (ouvre souvent un bootloader où choisir « Recovery »).",
                "Si « No command » s'affiche : maintenir Marche et appuyer une fois sur Volume +.",
                NavVolume),
            Usual("Android (toutes marques)", "fastboot",
                "Combinaison la plus courante : appareil éteint, maintenir Volume − et Marche.",
                "Beaucoup de terminaux professionnels n'ouvrent pas le fastboot (bootloader verrouillé) : passez alors par la recovery."),
        };

        /// <summary>« ET4* » correspond à « ET45 » ; « * » à tout ; comparaison sans casse ni espaces superflus.</summary>
        public static bool ModelMatches(string patterns, string model)
        {
            model = (model ?? "").Trim();
            foreach (var p in (patterns ?? "*").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                if (p == "*") return true;
                string rx = "^" + Regex.Escape(p).Replace("\\*", ".*") + "$";
                if (model.Length > 0 && Regex.IsMatch(model, rx, RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Marque du terminal ramenée à celle de la liste (« Zebra Technologies » → « Zebra »).</summary>
        public static string NormalizeBrand(string manufacturer)
        {
            string m = (manufacturer ?? "").Trim();
            if (m.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase) || m.StartsWith("Motorola Solutions", StringComparison.OrdinalIgnoreCase)) return "Zebra";
            if (m.StartsWith("Intermec", StringComparison.OrdinalIgnoreCase)) return "Honeywell";
            if (m.StartsWith("Honor", StringComparison.OrdinalIgnoreCase)) return "Huawei";
            if (m.StartsWith("Realme", StringComparison.OrdinalIgnoreCase)) return "Oppo";
            if (m.StartsWith("HMD", StringComparison.OrdinalIgnoreCase)) return "Nokia";
            foreach (var b in new[] { "Zebra", "Honeywell", "Datalogic", "Samsung", "Google", "Motorola", "Sony", "Lenovo", "Xiaomi", "Huawei", "Nokia", "OnePlus", "Oppo", "CipherLab", "Urovo", "Newland", "Unitech", "Point Mobile", "Bluebird", "M3" })
                if (m.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return b;
            return m;
        }

        private static bool IsGeneric(RecoveryKeyEntry e) => e.Brand.StartsWith("Android", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Combinaisons pour une marque et un modèle (l'un et l'autre facultatifs). Sans marque ni modèle : tout le catalogue.
        /// Avec un modèle : seules les procédures de ce modèle, celles « tous modèles » de sa marque et les génériques.
        /// Ordre : celles de l'utilisateur, puis les plus précises (modèle nommé), puis la marque, puis les génériques.
        /// </summary>
        public static List<RecoveryKeyEntry> Find(IEnumerable<RecoveryKeyEntry> all, string brand, string model)
        {
            brand = NormalizeBrand(brand);
            model = (model ?? "").Trim();
            var list = all.ToList();
            bool BrandOk(RecoveryKeyEntry e) => brand.Length == 0 || e.Brand.Equals(brand, StringComparison.OrdinalIgnoreCase)
                                                || e.Brand.StartsWith(brand + " ", StringComparison.OrdinalIgnoreCase) || IsGeneric(e);
            IEnumerable<RecoveryKeyEntry> r;
            if (model.Length == 0) r = list.Where(BrandOk);
            else
            {
                // Modèle nommé trouvé : on n'affiche pas les « tous modèles » des autres marques.
                var precise = list.Where(e => e.Models.Trim() != "*" && BrandOk(e) && ModelMatches(e.Models, model)).ToList();
                var brands = new HashSet<string>(precise.Select(e => e.Brand), StringComparer.OrdinalIgnoreCase);
                r = precise.Concat(list.Where(e => e.Models.Trim() == "*" && BrandOk(e) && (IsGeneric(e) || brand.Length > 0 || brands.Contains(e.Brand))));
            }
            return r.OrderBy(e => IsGeneric(e) ? 2 : 0)
                    .ThenBy(e => e.Brand, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Custom ? 0 : 1)
                    .ThenBy(e => e.Models.Trim() == "*" ? 1 : 0)
                    .ThenBy(e => e.Mode == "recovery" ? 0 : 1)
                    .ToList();
        }

        /// <summary>Marques du catalogue, dans l'ordre alphabétique (génériques exclues).</summary>
        public static List<string> Brands(IEnumerable<RecoveryKeyEntry> all) =>
            all.Where(e => !IsGeneric(e)).Select(e => e.Brand).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Fichier de l'utilisateur (liste JSON de RecoveryKeyEntry), à côté du programme.</summary>
        public static List<RecoveryKeyEntry> LoadUser(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<RecoveryKeyEntry>();
                var list = JsonSerializer.Deserialize<List<RecoveryKeyEntry>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? new List<RecoveryKeyEntry>();
                foreach (var e in list) e.Custom = true;
                return list.Where(e => e.Brand.Length > 0 && e.Steps.Count > 0).ToList();
            }
            catch { return new List<RecoveryKeyEntry>(); }
        }

        /// <summary>Modèle de fichier pour que l'utilisateur ajoute ses combinaisons.</summary>
        public static string UserTemplate() => JsonSerializer.Serialize(new List<RecoveryKeyEntry>
        {
            new RecoveryKeyEntry { Brand = "Honeywell", Models = "CT45*, CT47*", Mode = "recovery", Source = "Guide utilisateur du modèle",
                Steps = { "Exemple : première étape", "Exemple : deuxième étape" } },
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        /// <summary>« fastboot getvar all » → résumé lisible (produit, numéro de série, verrouillage, version du bootloader, slot).</summary>
        public static List<(string Label, string Value)> FastbootSummary(Dictionary<string, string> vars)
        {
            string Get(params string[] keys) { foreach (var k in keys) if (vars != null && vars.TryGetValue(k, out var v) && v.Length > 0) return v; return ""; }
            var r = new List<(string, string)>
            {
                ("Produit", Get("product")),
                ("N° de série", Get("serialno")),
                ("Bootloader déverrouillé", Get("unlocked") == "yes" ? "oui" : Get("unlocked") == "no" ? "non" : Get("unlocked")),
                ("Version du bootloader", Get("version-bootloader")),
                ("Slot actif", Get("current-slot")),
                ("Mode", Get("is-userspace") == "yes" ? "fastbootd (Android)" : "bootloader"),
            };
            return r.Where(x => x.Item2.Length > 0).ToList();
        }
    }
}
