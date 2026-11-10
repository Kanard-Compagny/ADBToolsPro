using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Explication des réglages « techniques » (hors catalogue) : nom en français, catégorie et valeur lisible, pour
    /// décider en connaissance de cause de les envoyer ou non. Les réglages inconnus reçoivent un nom déduit de leur clé.
    /// </summary>
    public static class MasterSettingsGlossary
    {
        public const string CatSound = "Son";
        public const string CatDisplay = "Écran et affichage";
        public const string CatLock = "Écran de verrouillage";
        public const string CatNotif = "Notifications et Ne pas déranger";
        public const string CatInput = "Clavier, voix et saisie";
        public const string CatAccess = "Accessibilité";
        public const string CatGestures = "Gestes et boutons";
        public const string CatWifi = "Wi-Fi";
        public const string CatRadio = "Bluetooth, NFC et réseau mobile";
        public const string CatLocation = "Localisation";
        public const string CatApps = "Applications et sécurité";
        public const string CatGoogle = "Services Google";
        public const string CatHardware = "Matériel du terminal (Zebra…)";
        public const string CatInternal = "Interne à Android";
        public const string CatOther = "Autres";

        /// <summary>Ordre d'affichage des catégories (la catégorie « interne » en dernier).</summary>
        public static readonly string[] Categories =
            { CatSound, CatDisplay, CatLock, CatNotif, CatInput, CatAccess, CatGestures, CatWifi, CatRadio, CatLocation, CatApps, CatGoogle, CatHardware, CatOther, CatInternal };

        // Type de valeur : b = activé/désactivé, ms / s / min = durée, comp = appli/composant, snd = son, list = liste, n = nombre, t = texte.
        private static readonly Dictionary<string, (string Label, string Cat, string Type)> Known = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            // Son
            { "alarm_alert", ("Sonnerie des alarmes", CatSound, "snd") },
            { "ringtone", ("Sonnerie des appels", CatSound, "snd") },
            { "system_locales", ("Langue enregistrée par Android (sans effet : utilisez « Langue du système »)", CatInternal, "t") },
            { "notification_sound", ("Son des notifications", CatSound, "snd") },
            { "apply_ramping_ringer", ("Sonnerie progressive", CatSound, "b") },
            { "call_connected_tone_enabled", ("Bip à la connexion d'un appel", CatSound, "b") },
            { "dtmf_tone_type", ("Durée des tonalités du clavier d'appel", CatSound, "n") },
            { "volume_bluetooth_sco", ("Volume des appels en Bluetooth", CatSound, "n") },
            { "volume_music_ble_broadcast", ("Volume média (diffusion Bluetooth LE)", CatSound, "n") },
            { "volume_music_ble_headset", ("Volume média (casque Bluetooth LE)", CatSound, "n") },
            { "volume_music_bt_a2dp_hp", ("Volume média (casque Bluetooth)", CatSound, "n") },
            { "volume_music_speaker", ("Volume média (haut-parleur)", CatSound, "n") },
            { "volume_music_usb_headset", ("Volume média (casque USB)", CatSound, "n") },
            { "volume_notification_speaker", ("Volume des notifications (haut-parleur)", CatSound, "n") },
            { "volume_ring_speaker", ("Volume de la sonnerie (haut-parleur)", CatSound, "n") },
            { "volume_voice", ("Volume des appels", CatSound, "n") },
            { "mode_ringer_streams_affected", ("Sons coupés par le mode silencieux (code)", CatSound, "n") },
            { "mute_streams_affected", ("Sons coupés par la sourdine (code)", CatSound, "n") },
            { "charging_vibration_enabled", ("Vibration à la mise en charge", CatSound, "b") },
            { "power_sounds_enabled", ("Son au branchement du chargeur", CatSound, "b") },
            { "dock_sounds_enabled", ("Sons de la station d'accueil", CatSound, "b") },
            { "dock_sounds_enabled_when_accessbility", ("Sons de la station d'accueil (accessibilité)", CatSound, "b") },
            { "dock_audio_media_enabled", ("Audio par la station d'accueil", CatSound, "b") },
            { "emergency_tone", ("Tonalité d'urgence", CatSound, "n") },
            { "audio_safe_volume_state", ("Avertissement de volume élevé au casque (état)", CatSound, "n") },
            { "low_battery_sound_timeout", ("Délai du son de batterie faible", CatSound, "ms") },
            { "sync_parent_sounds", ("Sons du profil professionnel identiques au personnel", CatSound, "b") },
            { "tty_mode", ("Mode TTY (malentendants)", CatSound, "n") },
            { "hearing_aid", ("Compatibilité appareils auditifs", CatSound, "b") },
            { "status_bar_show_vibrate_icon", ("Icône vibreur dans la barre d'état", CatSound, "b") },
            { "volume_hush_gesture", ("Geste pour couper la sonnerie", CatSound, "n") },

            // Écran et affichage
            { "dim_screen", ("Assombrir l'écran avant la veille", CatDisplay, "b") },
            { "display_rotation_mode_setting", ("Mode de rotation de l'écran (Zebra)", CatDisplay, "n") },
            { "min_refresh_rate", ("Fréquence d'affichage minimale (Hz)", CatDisplay, "n") },
            { "peak_refresh_rate", ("Fréquence d'affichage maximale (Hz)", CatDisplay, "n") },
            { "display_density_forced", ("Taille d'affichage forcée (densité)", CatDisplay, "t") },
            { "display_size_forced", ("Résolution forcée", CatDisplay, "t") },
            { "device_state_rotation_lock", ("Verrouillage de la rotation par position", CatDisplay, "t") },
            { "hide_rotation_lock_toggle_for_accessibility", ("Masquer le bouton de rotation (accessibilité)", CatDisplay, "b") },
            { "screensaver_activate_on_dock", ("Économiseur d'écran sur station d'accueil", CatDisplay, "b") },
            { "screensaver_activate_on_sleep", ("Économiseur d'écran pendant la charge", CatDisplay, "b") },
            { "screensaver_default_component", ("Économiseur d'écran utilisé", CatDisplay, "comp") },
            { "ambient_enabled", ("Écran ambiant (affichage en veille)", CatDisplay, "b") },
            { "ambient_force_when_docked", ("Écran ambiant sur station d'accueil", CatDisplay, "b") },
            { "ambient_low_bit_enabled", ("Écran ambiant économe", CatDisplay, "b") },
            { "ambient_low_bit_enabled_dev", ("Écran ambiant économe (développeur)", CatDisplay, "b") },
            { "ambient_plugged_timeout_min", ("Délai de l'écran ambiant en charge", CatDisplay, "min") },
            { "ambient_tilt_to_bright", ("Incliner pour éclairer l'écran ambiant", CatDisplay, "b") },
            { "ambient_tilt_to_wake", ("Incliner pour réveiller", CatDisplay, "b") },
            { "ambient_touch_to_wake", ("Toucher pour réveiller", CatDisplay, "b") },
            { "burn_in_protection", ("Protection contre le marquage de l'écran", CatDisplay, "b") },
            { "night_display_forced_auto_mode_available", ("Éclairage nocturne automatique disponible", CatDisplay, "b") },
            { "user_disabled_hdr_formats", ("Formats HDR désactivés", CatDisplay, "t") },
            { "wifi_display_on", ("Affichage sans fil (Miracast)", CatDisplay, "b") },
            { "force_desktop_mode_on_external_displays", ("Mode bureau sur écran externe", CatDisplay, "b") },
            { "enable_freeform_support", ("Fenêtres libres", CatDisplay, "b") },
            { "enable_non_resizable_multi_window", ("Multi-fenêtre pour toutes les applis", CatDisplay, "b") },
            { "theater_mode_on", ("Mode cinéma", CatDisplay, "b") },
            { "smart_illuminate_enabled", ("Éclairage intelligent", CatDisplay, "b") },
            { "immersive_mode_confirmations", ("Messages « plein écran » déjà vus", CatDisplay, "t") },
            { "navigation_mode", ("Navigation (0 = 3 boutons, 1 = 2 boutons, 2 = gestes)", CatDisplay, "n") },
            { "one_handed_mode_enabled", ("Mode une main", CatDisplay, "b") },
            { "one_handed_mode_activated", ("Mode une main activé", CatDisplay, "b") },
            { "qs_auto_tiles", ("Tuiles ajoutées automatiquement aux réglages rapides", CatDisplay, "list") },

            // Écran de verrouillage
            { "lock_screen_allow_private_notifications", ("Contenu des notifications sur l'écran verrouillé", CatLock, "b") },
            { "lock_screen_show_only_unseen_notifications", ("Seulement les nouvelles notifications sur l'écran verrouillé", CatLock, "b") },
            { "lock_screen_show_qr_code_scanner", ("Lecteur de QR code sur l'écran verrouillé", CatLock, "b") },
            { "lockscreen.options", ("Options de déverrouillage", CatLock, "t") },
            { "lockscreen_imei_setting", ("IMEI affiché sur l'écran verrouillé (Zebra)", CatLock, "b") },
            { "lockscreen_show_controls", ("Commandes de la maison sur l'écran verrouillé", CatLock, "b") },
            { "lockscreen_show_wallet", ("Portefeuille sur l'écran verrouillé", CatLock, "b") },
            { "power_menu_locked_show_content", ("Menu marche/arrêt complet quand verrouillé", CatLock, "b") },
            { "sleep_timeout", ("Délai de mise en sommeil du terminal", CatLock, "ms") },
            { "offline_device_lock_setting", ("Verrouillage hors ligne en cas de vol", CatLock, "b") },
            { "active_unlock_on_wake", ("Déverrouillage actif au réveil", CatLock, "b") },
            { "active_unlock_on_unlock_intent", ("Déverrouillage actif sur demande", CatLock, "b") },
            { "active_unlock_on_biometric_fail", ("Déverrouillage actif après échec biométrique", CatLock, "b") },
            { "active_unlock_on_face_errors", ("Déverrouillage actif : erreurs du visage prises en compte", CatLock, "t") },
            { "active_unlock_on_face_acquire_info", ("Déverrouillage actif : infos du visage prises en compte", CatLock, "t") },
            { "active_unlock_wakeups_considered_unlock_intents", ("Déverrouillage actif : réveils pris en compte", CatLock, "t") },
            { "facelock_detection_threshold", ("Reconnaissance faciale : seuil de détection", CatLock, "n") },
            { "facelock_liveliness_recognition_threshold", ("Reconnaissance faciale : détection du vivant", CatLock, "n") },
            { "facelock_max_center_movement", ("Reconnaissance faciale : mouvement toléré", CatLock, "n") },
            { "mandatory_biometrics_requirements_satisfied", ("Exigences biométriques remplies", CatLock, "b") },
            { "aware_enabled", ("Détection de présence", CatLock, "b") },
            { "aware_lock_enabled", ("Verrouillage par détection de présence", CatLock, "b") },
            { "add_users_when_locked", ("Ajouter des utilisateurs depuis l'écran verrouillé", CatLock, "b") },

            // Notifications et Ne pas déranger
            { "notification_bubbles", ("Bulles de conversation", CatNotif, "b") },
            { "notification_light_pulse", ("Voyant clignotant pour les notifications", CatNotif, "b") },
            { "charging_led_notification", ("Voyant pendant la charge", CatNotif, "b") },
            { "notification_sound_set", ("Son de notification choisi", CatInternal, "b") },
            { "zen_duration", ("Durée de « Ne pas déranger » (0 = jusqu'à désactivation)", CatNotif, "min") },
            { "zen_mode_ringer_level", ("Niveau de sonnerie en « Ne pas déranger »", CatNotif, "n") },
            { "show_zen_settings_suggestion", ("Suggestion de régler « Ne pas déranger »", CatInternal, "b") },
            { "zen_settings_suggestion_viewed", ("Suggestion « Ne pas déranger » vue", CatInternal, "b") },
            { "zen_settings_updated", ("Réglages « Ne pas déranger » modifiés", CatInternal, "b") },
            { "snoozed_schedule_condition_provider", ("Programme « Ne pas déranger » suspendu", CatNotif, "t") },
            { "smart_replies_enabled", ("Réponses suggérées", CatNotif, "b") },
            { "smart_replies_in_notifications_flags", ("Réponses suggérées : paramètres", CatNotif, "t") },
            { "enabled_notification_listeners", ("Applis autorisées à lire les notifications", CatNotif, "list") },
            { "cdma_cell_broadcast_sms", ("Alertes d'urgence par SMS (CDMA)", CatNotif, "b") },
            { "multi_cb", ("Alertes d'urgence multiples", CatNotif, "b") },
            { "send_action_app_error", ("Proposer d'envoyer un rapport quand une appli plante", CatNotif, "b") },
            { "bug_report", ("Rapport de bug dans le menu marche/arrêt", CatNotif, "b") },

            // Clavier, voix et saisie
            { "enabled_input_methods", ("Claviers activés", CatInput, "list") },
            { "input_methods_subtype_history", ("Historique des langues de clavier", CatInput, "t") },
            { "selected_input_method_subtype", ("Langue du clavier choisie", CatInput, "n") },
            { "selected_spell_checker", ("Correcteur orthographique", CatInput, "comp") },
            { "selected_spell_checker_subtype", ("Langue du correcteur", CatInput, "n") },
            { "default_voice_input_method", ("Saisie vocale par défaut", CatInput, "comp") },
            { "voice_interaction_service", ("Assistant vocal", CatInput, "comp") },
            { "voice_recognition_service", ("Reconnaissance vocale", CatInput, "comp") },
            { "assistant", ("Assistant par défaut", CatInput, "comp") },
            { "hotword_detection_enabled", ("Détection de « Ok Google »", CatInput, "b") },
            { "autofill_service", ("Service de saisie automatique", CatInput, "comp") },
            { "autofill_field_classification", ("Saisie automatique : reconnaissance des champs", CatInput, "b") },
            { "autofill_compat_mode_allowed_packages", ("Saisie automatique : navigateurs compatibles", CatInput, "list") },
            { "credential_service", ("Gestionnaire de mots de passe", CatInput, "comp") },
            { "credential_service_primary", ("Gestionnaire de mots de passe principal", CatInput, "comp") },
            { "pointer_speed", ("Vitesse du pointeur (souris)", CatInput, "n") },
            { "multi_press_timeout", ("Délai entre deux appuis", CatInput, "ms") },
            { "speak_password", ("Lire les mots de passe à voix haute (accessibilité)", CatInput, "b") },

            // Accessibilité
            { "accessibility_enabled", ("Accessibilité activée", CatAccess, "b") },
            { "accessibility_button_mode", ("Bouton d'accessibilité (mode)", CatAccess, "n") },
            { "accessibility_display_magnification_enabled", ("Agrandissement par triple appui", CatAccess, "b") },
            { "accessibility_display_magnification_scale", ("Niveau d'agrandissement", CatAccess, "n") },
            { "accessibility_magnification_capability", ("Type d'agrandissement", CatAccess, "n") },
            { "accessibility_shortcut_target_service", ("Raccourci d'accessibilité", CatAccess, "comp") },
            { "touch_exploration_enabled", ("Explorer au toucher (TalkBack)", CatAccess, "b") },
            { "odi_captions_volume_ui_enabled", ("Sous-titres instantanés dans le volume", CatAccess, "b") },

            // Gestes et boutons
            { "end_button_behavior", ("Effet du bouton marche/arrêt pendant un appel", CatGestures, "n") },
            { "emergency_gesture_enabled", ("Appel d'urgence par 5 appuis", CatGestures, "b") },
            { "emergency_gesture_power_button_cooldown_period_ms", ("Appel d'urgence : délai entre deux déclenchements", CatGestures, "ms") },
            { "flip_gesture", ("Retourner pour mettre en silencieux", CatGestures, "b") },
            { "pick_up_gesture_enabled", ("Soulever pour réveiller", CatGestures, "b") },
            { "wake_gesture_enabled", ("Geste de réveil", CatGestures, "b") },
            { "silence_gesture", ("Geste pour couper le son", CatGestures, "b") },
            { "skip_gesture", ("Geste pour passer au morceau suivant", CatGestures, "b") },
            { "hush_gesture_used", ("Geste de silence déjà utilisé", CatInternal, "b") },
            { "swipe_bottom_to_notification_enabled", ("Glisser en bas pour les notifications", CatGestures, "b") },
            { "side_button", ("Bouton latéral", CatGestures, "n") },
            { "lid_behavior", ("Effet de la fermeture du rabat", CatGestures, "n") },
            { "flashlight_enabled", ("Lampe torche allumée", CatGestures, "b") },
            { "flashlight_available", ("Lampe torche disponible", CatGestures, "b") },

            // Wi-Fi
            { "auto_wifi", ("Wi-Fi automatique", CatWifi, "b") },
            { "wifi_power_save", ("Économie d'énergie du Wi-Fi", CatWifi, "n") },
            { "wifi_device_owner_configs_lockdown", ("Réseaux du MDM verrouillés pour l'utilisateur", CatWifi, "b") },
            { "wifi_migration_completed", ("Migration des réseaux Wi-Fi terminée", CatInternal, "b") },
            { "BSAllowWifiEnable", ("Autoriser l'activation du Wi-Fi (Zebra)", CatWifi, "b") },
            { "P2P_CAST_ENABLED", ("Diffusion Wi-Fi Direct", CatWifi, "b") },
            { "network_recommendations_enabled", ("Suggestions de réseaux", CatWifi, "b") },
            { "network_scoring_ui_enabled", ("Évaluation de la qualité des réseaux", CatWifi, "b") },
            { "alt_bypass_wifi_requirement_time_millis", ("Délai avant de se passer du Wi-Fi", CatWifi, "ms") },

            // Bluetooth, NFC et réseau mobile
            { "bluetooth_disabled_profiles", ("Profils Bluetooth désactivés (code)", CatRadio, "n") },
            { "bluetooth_sanitized_exposure_notification_supported", ("Notifications d'exposition Bluetooth", CatRadio, "b") },
            { "user_hfp_client_setting", ("Bluetooth : mode main libre", CatRadio, "b") },
            { "nfc_payment_default_component", ("Appli de paiement sans contact", CatRadio, "comp") },
            { "nfc_payment_foreground", ("Paiement : priorité à l'appli ouverte", CatRadio, "b") },
            { "nearby_sharing_component", ("Partage à proximité", CatRadio, "comp") },
            { "cell_on", ("Réseau mobile activé", CatRadio, "b") },
            { "mobile_data_always_on", ("Données mobiles toujours actives", CatRadio, "b") },
            { "mobile_signal_detector", ("Détection du signal mobile", CatRadio, "b") },
            { "preferred_network_mode", ("Type de réseau mobile préféré (code par carte SIM)", CatRadio, "list") },
            { "airplane_mode_on", ("Mode avion", CatRadio, "b") },
            { "airplane_mode_radios", ("Radios coupées par le mode avion", CatRadio, "list") },
            { "airplane_mode_toggleable_radios", ("Radios réactivables en mode avion", CatRadio, "list") },
            { "satellite_mode_enabled", ("Mode satellite", CatRadio, "b") },
            { "satellite_mode_radios", ("Radios coupées par le mode satellite", CatRadio, "list") },
            { "call_auto_retry", ("Rappel automatique d'un appel échoué", CatRadio, "b") },
            { "default_restrict_background_data", ("Économiseur de données", CatRadio, "b") },
            { "subscription_mode", ("Mode d'abonnement (CDMA)", CatRadio, "n") },
            { "usb_mass_storage_enabled", ("Stockage de masse USB", CatRadio, "b") },
            { "mount_ums_autostart", ("Stockage USB automatique au branchement", CatRadio, "b") },
            { "mount_ums_notify_enabled", ("Notification du stockage USB", CatRadio, "b") },
            { "mount_ums_prompt", ("Demander avant le stockage USB", CatRadio, "b") },
            { "mount_play_not_snd", ("Son à la connexion du stockage USB", CatRadio, "b") },

            // Localisation
            { "mock_location", ("Positions fictives autorisées", CatLocation, "b") },
            { "allowed_geolocation_origins", ("Sites autorisés à connaître la position", CatLocation, "list") },
            { "location_background_throttle_interval_ms", ("Fréquence de localisation en arrière-plan", CatLocation, "ms") },
            { "location_ignore_settings_package_whitelist", ("Applis localisées même localisation coupée", CatLocation, "list") },
            { "gnss_satellite_blocklist", ("Satellites GPS ignorés", CatLocation, "list") },
            { "obtain_paired_device_location", ("Position par un appareil associé", CatLocation, "b") },

            // Applications et sécurité
            { "default_install_location", ("Emplacement d'installation des applis", CatApps, "n") },
            { "set_install_location", ("Choix de l'emplacement d'installation", CatApps, "b") },
            { "unknown_sources_default_reversed", ("Sources inconnues (comportement par défaut)", CatApps, "b") },
            { "verifier_timeout", ("Délai de vérification d'une appli", CatApps, "ms") },
            { "upload_apk_enable", ("Envoi des applis inconnues à Google pour analyse", CatApps, "b") },
            { "app_standby_enabled", ("Mise en veille des applis inutilisées", CatApps, "b") },
            { "wait_for_debugger", ("Attendre le débogueur au lancement (développeur)", CatApps, "b") },
            { "sysui_demo_allowed", ("Mode démo de la barre d'état (développeur)", CatApps, "b") },
            { "sysui_tuner_demo_on", ("Mode démo activé (développeur)", CatApps, "b") },
            { "enable_ephemeral_feature", ("Applis instantanées", CatApps, "b") },
            { "backup_transport", ("Service de sauvegarde", CatApps, "comp") },
            { "zram_enabled", ("Compression de la mémoire (zRAM)", CatApps, "b") },
            { "apm_enhancement_enabled", ("Gestion avancée de l'énergie", CatApps, "b") },
            { "fusion_apply_global_proxy", ("Proxy global du Wi-Fi (Zebra)", CatApps, "b") },
            { "restart_nap_after_start", ("Relancer la veille après démarrage", CatApps, "b") },

            // Services Google
            { "show_qr_code_scanner_setting", ("Lecteur de QR code des réglages rapides", CatGoogle, "comp") },
            { "has_pay_tokens", ("Cartes de paiement enregistrées", CatGoogle, "b") },
            { "phone_play_store_availability", ("Play Store disponible", CatGoogle, "b") },
            { "gms_checkin_timeout_min", ("Délai d'enregistrement auprès de Google", CatGoogle, "min") },
            { "obtain_mute_when_off_body", ("Couper le son quand la montre est retirée", CatGoogle, "b") },
            { "current_watchface_decomposable", ("Cadran de montre (Wear OS)", CatInternal, "b") },
            { "content_capture_service_explicitly_enabled", ("Analyse du contenu à l'écran par Google", CatGoogle, "t") },

            // Matériel du terminal
            { "keylight_auto_brightness", ("Rétroéclairage du clavier automatique", CatHardware, "b") },
            { "keylight_brightness", ("Luminosité du rétroéclairage du clavier", CatHardware, "n") },
            { "touchkeylight_off_timeout", ("Extinction du rétroéclairage des touches", CatHardware, "ms") },
            { "nled_brightness_value", ("Luminosité du voyant de notification", CatHardware, "n") },
            { "vendor.zebra.connection.initiated", ("Connexion Zebra initialisée", CatInternal, "b") },
            { "interval_time", ("Intervalle de tâche périodique du fabricant", CatHardware, "s") },
        };

        // Clés internes : comptes rendus, compteurs, journaux, réglages de Google ou d'Android sans effet visible.
        private static readonly Regex Internal = new Regex(
            @"(^|_)(set|logging|stats|watchlist|anomaly|binder|sqlite|wal|psd|provider_api|checkin|capabilities|cpupower|migration|completed|updated|viewed|used|initiated|suggestion)(_|$)|" +
            @"^(activity_starts_logging_enabled|netstats_enabled|settings_use_.*|system_capabilities|sys_uidcpupower|hybrid_sysui_.*|backup_agent_timeout_parameters|" +
            @"max_sound_trigger_.*|sound_trigger_.*|alarm_alert_set|ringtone_set|com\.google\..*|wifi_migration_completed|auto_time_zone_explicit|ntp_server_2)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Nom en français, catégorie et type de valeur d'un réglage technique.</summary>
        public static (string Label, string Category, string Type) Describe(string key)
        {
            key = key ?? "";
            if (Known.TryGetValue(key, out var k)) return k;
            string cat = Internal.IsMatch(key) ? CatInternal : GuessCategory(key);
            return (Humanize(key), cat, GuessType(key));
        }

        public static bool IsInternal(string key) => Describe(key).Category == CatInternal;

        private static string GuessCategory(string key)
        {
            string k = key.ToLowerInvariant();
            if (Regex.IsMatch(k, "volume|sound|tone|ringer|ringtone|alarm|vibrat|audio|haptic")) return CatSound;
            if (k.StartsWith("lock") || k.Contains("unlock") || k.Contains("facelock")) return CatLock;
            if (Regex.IsMatch(k, "notification|zen_|heads_up|badg")) return CatNotif;
            if (Regex.IsMatch(k, "input_method|keyboard|spell|voice|autofill|ime")) return CatInput;
            if (k.StartsWith("accessibility")) return CatAccess;
            if (Regex.IsMatch(k, "gesture|button|flashlight")) return CatGestures;
            if (k.StartsWith("wifi") || k.Contains("_wifi")) return CatWifi;
            if (Regex.IsMatch(k, "bluetooth|nfc|mobile|cell|airplane|roaming|network|data_|sim|usb")) return CatRadio;
            if (Regex.IsMatch(k, "location|gps|gnss|geo")) return CatLocation;
            if (Regex.IsMatch(k, "display|screen|brightness|density|refresh|rotation|font|ambient|screensaver|dream")) return CatDisplay;
            if (Regex.IsMatch(k, "google|gms|play_|wear|clockwork")) return CatGoogle;
            if (Regex.IsMatch(k, "zebra|rfid|keylight|nled|scanner|docking")) return CatHardware;
            if (Regex.IsMatch(k, "install|verif|package|app_|backup|debug|demo|developer")) return CatApps;
            return CatOther;
        }

        private static string GuessType(string key)
        {
            string k = key.ToLowerInvariant();
            if (Regex.IsMatch(k, "(_enabled|_on|_enable|^show_|^enable_)$|^(show|enable)_")) return "b";
            if (Regex.IsMatch(k, "(_ms|_millis|_timeout)$")) return "ms";
            if (k.EndsWith("_min")) return "min";
            if (Regex.IsMatch(k, "(_component|_service)$")) return "comp";
            return "";
        }

        // Mots courants des clés Android → français, pour nommer un réglage inconnu.
        private static readonly Dictionary<string, string> Words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "enabled", "activé" }, { "enable", "activer" }, { "disabled", "désactivé" }, { "on", "activé" }, { "show", "afficher" }, { "hide", "masquer" },
            { "screen", "écran" }, { "display", "affichage" }, { "brightness", "luminosité" }, { "timeout", "délai" }, { "volume", "volume" },
            { "sound", "son" }, { "sounds", "sons" }, { "tone", "tonalité" }, { "notification", "notification" }, { "notifications", "notifications" },
            { "lock", "verrouillage" }, { "lockscreen", "écran de verrouillage" }, { "wifi", "Wi-Fi" }, { "bluetooth", "Bluetooth" }, { "mobile", "mobile" },
            { "data", "données" }, { "network", "réseau" }, { "location", "localisation" }, { "auto", "automatique" }, { "default", "par défaut" },
            { "mode", "mode" }, { "power", "alimentation" }, { "battery", "batterie" }, { "charging", "charge" }, { "keyboard", "clavier" },
            { "input", "saisie" }, { "method", "méthode" }, { "user", "utilisateur" }, { "time", "heure" }, { "zone", "zone" }, { "gesture", "geste" },
            { "button", "bouton" }, { "music", "média" }, { "speaker", "haut-parleur" }, { "headset", "casque" }, { "ring", "sonnerie" }, { "alarm", "alarme" },
            { "vibrate", "vibrer" }, { "vibration", "vibration" }, { "dock", "station d'accueil" }, { "install", "installation" }, { "app", "appli" },
            { "apps", "applis" }, { "package", "paquet" }, { "service", "service" }, { "level", "niveau" }, { "scale", "échelle" }, { "rate", "fréquence" },
            { "min", "min." }, { "max", "max." }, { "always", "toujours" }, { "allow", "autoriser" }, { "allowed", "autorisé" }, { "when", "quand" },
            { "while", "pendant" }, { "plugged", "branché" }, { "sleep", "veille" }, { "wake", "réveil" }, { "accessibility", "accessibilité" },
        };

        /// <summary>« volume_music_speaker » → « Volume média haut-parleur » (approximation pour une clé inconnue).</summary>
        public static string Humanize(string key)
        {
            var parts = Regex.Split(key ?? "", @"[_.\-]+").Where(p => p.Length > 0).Select(p => Words.TryGetValue(p, out var w) ? w : p.ToLowerInvariant()).ToList();
            if (parts.Count == 0) return key ?? "";
            string s = string.Join(" ", parts);
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        /// <summary>Valeur lisible : activé/désactivé, durée, nom de son, appli, liste…</summary>
        public static string DisplayValue(string key, string value)
        {
            value = value ?? "";
            string type = Describe(key).Type;
            if (value.Length == 0) return "(vide)";
            switch (type)
            {
                case "b":
                    return value == "1" ? "activé" : value == "0" ? "désactivé" : value;
                case "ms":
                    return long.TryParse(value, out var ms) ? (ms < 0 ? "jamais / désactivé" : Duration(ms / 1000.0)) : value;
                case "s":
                    return long.TryParse(value, out var s) ? Duration(s) : value;
                case "min":
                    return long.TryParse(value, out var mn) ? (mn < 0 ? "jamais / désactivé" : mn == 0 ? "0" : Duration(mn * 60.0)) : value;
                case "snd":
                    var t = Regex.Match(value, @"[?&]title=([^&]+)");
                    return t.Success ? "« " + Uri.UnescapeDataString(t.Groups[1].Value.Replace("+", " ")) + " »" : value;
                case "comp":
                    string known = MasterProfile.KnownAppName(value);
                    if (known.Length > 0) return known;
                    string pkg = value.Split('/')[0];
                    if (pkg.StartsWith("com.google.")) return "Google (" + pkg.Substring(11) + ")";
                    return pkg;
                case "list":
                    var items = value.Split(new[] { ',', ':', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    return items.Length > 3 ? string.Join(", ", items.Take(3)) + $"… ({items.Length})" : string.Join(", ", items);
                default:
                    return value.Length > 60 ? value.Substring(0, 60) + "…" : value;
            }
        }

        private static string Duration(double seconds)
        {
            if (seconds >= 2147483) return "jamais";
            if (seconds < 1) return (seconds * 1000).ToString("0", CultureInfo.InvariantCulture) + " ms";
            if (seconds < 60) return seconds.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR")) + " s";
            if (seconds < 3600) return (seconds / 60).ToString("0.#", CultureInfo.GetCultureInfo("fr-FR")) + " min";
            return (seconds / 3600).ToString("0.#", CultureInfo.GetCultureInfo("fr-FR")) + " h";
        }
    }
}
