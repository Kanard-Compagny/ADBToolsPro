using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ADBToolsNew.Core
{
    public enum MasterSettingKind { Toggle, Choice, Number, Text }

    /// <summary>Un réglage Android connu : nom compréhensible, thème, type de valeur et valeurs possibles.</summary>
    public sealed class MasterSettingDef
    {
        public string Namespace = "";
        public string Key = "";
        public string Label = "";
        public string Theme = "";
        public MasterSettingKind Kind;
        /// <summary>Valeurs proposées (valeur Android, libellé français) pour un choix.</summary>
        public (string Value, string Label)[] Options = new (string, string)[0];
        public int Min, Max;
        public string Hint = "";
        /// <summary>Valeur proposée quand le réglage est ajouté à la main.</summary>
        public string Default = "";
    }

    /// <summary>
    /// Catalogue des réglages Android utiles en entreprise, pour les afficher et les modifier sans connaître leur nom
    /// technique. Les réglages capturés hors catalogue restent disponibles en « réglages avancés », non cochés.
    /// </summary>
    public static class MasterSettingsCatalog
    {
        public const string ThemeScreen = "Écran";
        public const string ThemeSound = "Son et vibrations";
        public const string ThemeLanguage = "Langue, clavier et heure";
        public const string ThemeNetwork = "Réseau et localisation";
        public const string ThemeSystem = "Sécurité et système";
        public static readonly string[] Themes = { ThemeScreen, ThemeSound, ThemeLanguage, ThemeNetwork, ThemeSystem };

        private static readonly (string, string)[] OnOff = { ("1", "Activé"), ("0", "Désactivé") };

        private static MasterSettingDef Toggle(string ns, string key, string label, string theme, string def = "1", string hint = "") =>
            new MasterSettingDef { Namespace = ns, Key = key, Label = label, Theme = theme, Kind = MasterSettingKind.Toggle, Options = OnOff, Default = def, Hint = hint };

        private static MasterSettingDef Choice(string ns, string key, string label, string theme, string def, params (string, string)[] options) =>
            new MasterSettingDef { Namespace = ns, Key = key, Label = label, Theme = theme, Kind = MasterSettingKind.Choice, Options = options, Default = def };

        private static MasterSettingDef Number(string ns, string key, string label, string theme, int min, int max, string def, string hint = "") =>
            new MasterSettingDef { Namespace = ns, Key = key, Label = label, Theme = theme, Kind = MasterSettingKind.Number, Min = min, Max = max, Default = def, Hint = hint };

        private static MasterSettingDef Text(string ns, string key, string label, string theme, string def, string hint) =>
            new MasterSettingDef { Namespace = ns, Key = key, Label = label, Theme = theme, Kind = MasterSettingKind.Text, Default = def, Hint = hint };

        private static readonly (string, string)[] Animations = { ("0", "Désactivées"), ("0.5", "Rapides (×0,5)"), ("1.0", "Normales"), ("1.5", "Lentes (×1,5)") };

        public static readonly List<MasterSettingDef> All = new List<MasterSettingDef>
        {
            // Écran
            Choice("system", "screen_off_timeout", "Mise en veille de l'écran", ThemeScreen, "600000",
                ("15000", "15 secondes"), ("30000", "30 secondes"), ("60000", "1 minute"), ("120000", "2 minutes"), ("300000", "5 minutes"),
                ("600000", "10 minutes"), ("1800000", "30 minutes"), ("2147483646", "Jamais")),
            Choice("global", "stay_on_while_plugged_in", "Écran toujours allumé en charge", ThemeScreen, "3",
                ("0", "Non"), ("1", "Sur secteur"), ("2", "Sur USB"), ("3", "Secteur et USB"), ("4", "Sans fil"), ("7", "Toutes les charges")),
            Toggle("system", "screen_brightness_mode", "Luminosité automatique", ThemeScreen),
            Number("system", "screen_brightness", "Luminosité", ThemeScreen, 0, 255, "128", "0 à 255 (sans effet si la luminosité est automatique)"),
            Toggle("system", "accelerometer_rotation", "Rotation automatique", ThemeScreen),
            Choice("system", "user_rotation", "Orientation (rotation automatique désactivée)", ThemeScreen, "0",
                ("0", "Portrait"), ("1", "Paysage"), ("2", "Portrait inversé"), ("3", "Paysage inversé")),
            Choice("system", "font_scale", "Taille du texte", ThemeScreen, "1.0",
                ("0.85", "Petite"), ("1.0", "Normale"), ("1.15", "Grande"), ("1.3", "Très grande")),
            Toggle("system", "status_bar_show_battery_percent", "Pourcentage de batterie dans la barre d'état", ThemeScreen),
            Toggle("secure", "double_tap_to_wake", "Double appui pour réveiller l'écran", ThemeScreen),
            Toggle("secure", "screensaver_enabled", "Économiseur d'écran", ThemeScreen, "0"),
            Toggle("system", "show_touches", "Afficher les appuis à l'écran", ThemeScreen, "0"),
            Choice("global", "window_animation_scale", "Animations des fenêtres", ThemeScreen, "1.0", Animations),
            Choice("global", "transition_animation_scale", "Animations des transitions", ThemeScreen, "1.0", Animations),
            Choice("global", "animator_duration_scale", "Durée des animations", ThemeScreen, "1.0", Animations),

            // Son et vibrations
            Choice("global", "mode_ringer", "Mode de sonnerie", ThemeSound, "2", ("2", "Normal"), ("1", "Vibreur"), ("0", "Silencieux")),
            Choice("global", "zen_mode", "Ne pas déranger", ThemeSound, "0", ("0", "Désactivé"), ("1", "Prioritaires seulement"), ("3", "Alarmes seulement"), ("2", "Silence total")),
            Number("system", "volume_ring", "Volume de la sonnerie", ThemeSound, 0, 15, "5", "0 à 15 selon le terminal"),
            Number("system", "volume_notification", "Volume des notifications", ThemeSound, 0, 15, "5", "0 à 15 selon le terminal"),
            Number("system", "volume_music", "Volume des médias", ThemeSound, 0, 15, "8", "0 à 15 selon le terminal"),
            Number("system", "volume_alarm", "Volume des alarmes", ThemeSound, 0, 15, "6", "0 à 15 selon le terminal"),
            Number("system", "volume_system", "Volume du système", ThemeSound, 0, 15, "7", "0 à 15 selon le terminal"),
            Toggle("system", "sound_effects_enabled", "Sons des touches et appuis", ThemeSound),
            Toggle("system", "haptic_feedback_enabled", "Vibration au toucher", ThemeSound),
            Toggle("system", "vibrate_when_ringing", "Vibrer pour les appels", ThemeSound),
            Toggle("system", "lockscreen_sounds_enabled", "Son de verrouillage de l'écran", ThemeSound),
            Toggle("secure", "charging_sounds_enabled", "Son et vibration de mise en charge", ThemeSound),
            Toggle("system", "dtmf_tone", "Tonalité du clavier de numérotation", ThemeSound),

            // Langue, clavier et heure
            Text("system", "system_locales", "Langue du système", ThemeLanguage, "fr-FR", "code langue-pays, ex. fr-FR, en-GB (selon le terminal, un redémarrage peut être nécessaire)"),
            Text("secure", "default_input_method", "Clavier par défaut", ThemeLanguage, "", "identifiant du clavier (paquet/classe), tel que capturé"),
            Toggle("secure", "show_ime_with_hard_keyboard", "Clavier à l'écran avec un clavier physique", ThemeLanguage),
            Toggle("global", "auto_time", "Date et heure automatiques", ThemeLanguage),
            Toggle("global", "auto_time_zone", "Fuseau horaire automatique", ThemeLanguage),
            Choice("system", "time_12_24", "Format de l'heure", ThemeLanguage, "24", ("24", "24 heures"), ("12", "12 heures (AM/PM)")),
            Text("global", "ntp_server", "Serveur de temps (NTP)", ThemeLanguage, "pool.ntp.org", "nom ou adresse du serveur NTP de l'entreprise"),
            Choice("secure", "long_press_timeout", "Durée de l'appui long", ThemeLanguage, "400", ("400", "Courte"), ("1000", "Moyenne"), ("1500", "Longue")),

            // Réseau et localisation
            Toggle("global", "wifi_on", "Wi-Fi", ThemeNetwork),
            Choice("global", "wifi_sleep_policy", "Wi-Fi pendant la veille", ThemeNetwork, "2", ("2", "Toujours connecté"), ("1", "Seulement en charge"), ("0", "Coupé en veille")),
            Toggle("global", "wifi_scan_always_enabled", "Recherche Wi-Fi toujours active (localisation)", ThemeNetwork),
            Toggle("global", "wifi_networks_available_notification_on", "Notifier les réseaux ouverts", ThemeNetwork, "0"),
            Toggle("global", "wifi_wakeup_enabled", "Réactiver le Wi-Fi automatiquement", ThemeNetwork),
            Toggle("global", "bluetooth_on", "Bluetooth", ThemeNetwork),
            Toggle("global", "mobile_data", "Données mobiles", ThemeNetwork),
            Toggle("global", "data_roaming", "Itinérance des données", ThemeNetwork, "0"),
            Choice("secure", "location_mode", "Localisation", ThemeNetwork, "3", ("3", "Activée (haute précision)"), ("2", "Économie de batterie"), ("1", "Capteurs seulement"), ("0", "Désactivée")),
            Toggle("global", "assisted_gps_enabled", "GPS assisté (A-GPS)", ThemeNetwork),

            // Sécurité et système
            Toggle("secure", "lockscreen.disabled", "Écran de verrouillage désactivé", ThemeSystem, "1", "seulement si aucun code n'est défini"),
            Toggle("secure", "lock_screen_show_notifications", "Notifications sur l'écran de verrouillage", ThemeSystem),
            Toggle("global", "heads_up_notifications_enabled", "Notifications en bannière", ThemeSystem),
            Toggle("secure", "notification_badging", "Pastilles de notification sur les icônes", ThemeSystem),
            Toggle("global", "low_power", "Économiseur de batterie", ThemeSystem, "0"),
            Toggle("global", "verifier_verify_adb_installs", "Vérifier les applis installées par câble", ThemeSystem),
            Toggle("global", "package_verifier_user_consent", "Analyse des applis par Google Play Protect", ThemeSystem),
            Toggle("secure", "backup_enabled", "Sauvegarde Google", ThemeSystem, "0"),
            Text("secure", "sysui_qs_tiles", "Tuiles des réglages rapides", ThemeSystem, "", "liste séparée par des virgules, telle que capturée"),
        };

        private static readonly Dictionary<string, MasterSettingDef> ByKey =
            All.ToDictionary(d => d.Namespace + "|" + d.Key, StringComparer.Ordinal);

        public static MasterSettingDef Find(string ns, string key) =>
            ByKey.TryGetValue((ns ?? "") + "|" + (key ?? ""), out var d) ? d : null;

        /// <summary>Valeur affichée : libellé de l'option correspondante, sinon la valeur brute.</summary>
        public static string DisplayValue(MasterSettingDef def, string value)
        {
            if (def == null) return value ?? "";
            var o = def.Options.FirstOrDefault(x => SameValue(x.Value, value));
            return o.Label ?? value ?? "";
        }

        /// <summary>Compare deux valeurs, en tolérant « 1 » / « 1.0 » et « 2147483647 » pour « jamais ».</summary>
        public static bool SameValue(string a, string b)
        {
            a = (a ?? "").Trim(); b = (b ?? "").Trim();
            if (a == b) return true;
            if ((a == "2147483646" || a == "2147483647") && (b == "2147483646" || b == "2147483647")) return true;
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 1e-9;
        }

        /// <summary>Valeur acceptable pour ce réglage (nombre dans les bornes, texte non vide…).</summary>
        public static bool IsValid(MasterSettingDef def, string value)
        {
            if (def == null) return true;
            switch (def.Kind)
            {
                case MasterSettingKind.Number:
                    return int.TryParse((value ?? "").Trim(), out int n) && n >= def.Min && n <= def.Max;
                case MasterSettingKind.Toggle:
                    return value == "0" || value == "1";
                default:
                    return value != null;
            }
        }
    }
}
