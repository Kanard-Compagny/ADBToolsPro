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
    public sealed class RecoveryKeyEntry
    {
        public string Brand { get; set; } = "";
        /// <summary>Modèles concernés, séparés par des virgules, avec * (ex. « ET40*, ET45* ») ; « * » = tous.</summary>
        public string Models { get; set; } = "*";
        /// <summary>recovery, fastboot.</summary>
        public string Mode { get; set; } = "recovery";
        public List<string> Steps { get; set; } = new List<string>();
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

        public static readonly List<RecoveryKeyEntry> BuiltIn = new List<RecoveryKeyEntry>
        {
            new RecoveryKeyEntry { Brand = "Zebra", Models = "ET40*, ET45*", Mode = "recovery", Verified = true,
                Source = "https://docs.zebra.com/us/en/tablets/et4-series/bm-et401-product-reference-guide-ditamap/c-application-deployment/c-android-development-tools/c-adb-usb-setup/t-et40-45-entering-android-recovery-manually.html",
                Steps = { "Maintenir Marche jusqu'au menu, puis toucher « Redémarrer ».", "Au redémarrage, maintenir Volume + et Marche jusqu'au logo Zebra.", "Relâcher Marche en gardant Volume + jusqu'à l'écran System Recovery." } },
            new RecoveryKeyEntry { Brand = "Zebra", Models = "WS50*", Mode = "recovery", Verified = true,
                Source = "https://docs.zebra.com/us/en/mobile-computers/wearable/ws50/ws50-ws50rfid-prg/application-deployment/adb-usb-setup/entering-android-recovery-manually.html",
                Steps = { "Brancher le câble USB avant de commencer.", "Maintenir Marche jusqu'au menu, puis toucher « Redémarrer ».", "Maintenir la touche Retour jusqu'au logo Zebra, puis la relâcher : l'écran System Recovery s'affiche." } },
            new RecoveryKeyEntry { Brand = "Zebra", Models = "WS50*", Mode = "fastboot", Verified = true,
                Source = "https://docs.zebra.com/us/en/mobile-computers/wearable/ws50/ws50-ws50rfid-prg/application-deployment/adb-usb-setup/entering-android-recovery-manually.html",
                Steps = { "Brancher le câble USB, puis redémarrer le terminal.", "Au redémarrage, appuyer sur la gâchette de lecture : mode Fastboot." } },
            new RecoveryKeyEntry { Brand = "Zebra", Models = "*", Mode = "recovery", Verified = false,
                Source = "https://docs.zebra.com",
                Steps = { "Avec le débogage USB : bouton « Redémarrer en recovery » ci-dessus (adb reboot recovery), c'est la méthode de la documentation Zebra.", "Sans débogage USB : chaque modèle a sa procédure, dans son guide de référence (Product Reference Guide), chapitre « Entering Android Recovery Manually » : en général redémarrer puis maintenir une touche (Volume +, PTT ou Retour selon le modèle) jusqu'au logo Zebra." } },
            new RecoveryKeyEntry { Brand = "Datalogic", Models = "Joya Touch A6*", Mode = "recovery", Verified = true,
                Source = "https://discussion.datalogic.com/t/recovery-mode-on-joya-touch-a6/1250",
                Steps = { "Éteindre le terminal.", "Appuyer sur la touche Marche (au dos) avec un trombone métallique, puis aussitôt sur la touche de lecture (dans les 0,2 à 0,3 s).", "Dans le menu Recovery : faire défiler au toucher, valider avec la touche de lecture." } },
            new RecoveryKeyEntry { Brand = "Samsung", Models = "*", Mode = "recovery", Verified = true,
                Source = "https://www.att.com/device-support/article/145684/samsung/xcover7-pro/",
                Steps = { "Éteindre le terminal (Marche + Volume −, puis « Éteindre »).", "Maintenir Volume + et Marche jusqu'à l'allumage de l'écran." } },
            new RecoveryKeyEntry { Brand = "Google", Models = "Pixel*", Mode = "fastboot", Verified = false,
                Steps = { "Terminal éteint : maintenir Marche et Volume − : écran du bootloader (fastboot).", "Pour la recovery : choisir « Recovery mode » avec les touches de volume, valider avec Marche." } },
            new RecoveryKeyEntry { Brand = "Android (générique)", Models = "*", Mode = "recovery", Verified = false,
                Steps = { "Combinaison la plus courante : terminal éteint, maintenir Volume + et Marche jusqu'au logo.", "À confirmer pour chaque modèle : cherchez « marque modèle recovery mode » sur le web." } },
            new RecoveryKeyEntry { Brand = "Android (générique)", Models = "*", Mode = "fastboot", Verified = false,
                Steps = { "Combinaison la plus courante : terminal éteint, maintenir Volume − et Marche.", "Beaucoup de terminaux professionnels n'ouvrent pas le fastboot (bootloader verrouillé) : passez alors par la recovery." } },
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
            foreach (var b in new[] { "Zebra", "Honeywell", "Datalogic", "Samsung", "Google", "Motorola", "Sony", "Lenovo", "Xiaomi", "Huawei", "Nokia", "CipherLab", "Urovo", "Newland", "Unitech", "Point Mobile", "Bluebird", "M3" })
                if (m.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return b;
            if (m.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase) || m.StartsWith("Motorola Solutions", StringComparison.OrdinalIgnoreCase)) return "Zebra";
            if (m.StartsWith("Intermec", StringComparison.OrdinalIgnoreCase)) return "Honeywell";
            return m;
        }

        /// <summary>
        /// Combinaisons pour une marque et un modèle : celles de l'utilisateur d'abord, puis les plus précises (modèle nommé),
        /// puis celles de la marque, puis les génériques.
        /// </summary>
        public static List<RecoveryKeyEntry> Find(IEnumerable<RecoveryKeyEntry> all, string brand, string model)
        {
            brand = NormalizeBrand(brand);
            return all.Where(e => (brand.Length == 0 || e.Brand.Equals(brand, StringComparison.OrdinalIgnoreCase) || e.Brand.StartsWith("Android", StringComparison.OrdinalIgnoreCase))
                                  && (e.Models.Trim() == "*" || ModelMatches(e.Models, model)))
                      .OrderBy(e => e.Brand.StartsWith("Android", StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                      .ThenBy(e => e.Custom ? 0 : 1)
                      .ThenBy(e => e.Models.Trim() == "*" ? 1 : 0)
                      .ThenBy(e => e.Mode)
                      .ToList();
        }

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
            new RecoveryKeyEntry { Brand = "Honeywell", Models = "CT45*, CT47*", Mode = "recovery", Source = "https://…",
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
