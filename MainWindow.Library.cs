using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using System.Text;
using System.Text.Json;
using LiveCharts;
using LiveCharts.Wpf;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ADBToolsNew
{
    public partial class MainWindow : Window
    {
        public class AdbCommandItem
        {
            public string Category { get; set; }
            public string Title { get; set; }
            public string Command { get; set; }
            public string MinAndroid { get; set; }

            /// <summary>Dernière version d'Android où la commande a un effet (vide = pas de limite connue).
            /// Certaines commandes historiques sont simplement ignorées par les Android récents, sans message
            /// d'erreur : le technicien croit qu'elles ont marché.</summary>
            public string MaxAndroid { get; set; }

            /// <summary>Plage affichée dans la bibliothèque et la confirmation (ex. "8.0+" ou "8.0 – 10").</summary>
            [System.Text.Json.Serialization.JsonIgnore]
            public string AndroidRange => string.IsNullOrWhiteSpace(MaxAndroid) ? $"{MinAndroid}+" : $"{MinAndroid} – {MaxAndroid}";
        }

        /// <summary>
        /// Mise à jour des commandes par défaut devenues inopérantes sur les Android récents. Appliquée aussi aux
        /// bibliothèques déjà enregistrées (AdbLibrary.json), mais uniquement aux commandes restées identiques à
        /// l'ancienne version par défaut : une commande personnalisée ou modifiée par l'utilisateur n'est jamais touchée.
        /// </summary>
        private static readonly Dictionary<string, AdbCommandItem> LibraryCommandUpdates = new Dictionary<string, AdbCommandItem>
        {
            ["shell settings put secure install_non_market_apps 1"] = new AdbCommandItem { Category = "Sécurité", Title = Loc.T("Autoriser l'installation de sources inconnues (Android 4 à 7 ; ensuite : autorisation par application dans les réglages)"), Command = "shell settings put secure install_non_market_apps 1", MinAndroid = "4.0", MaxAndroid = "7.1" },
            ["shell settings put global policy_control immersive.navigation=*"] = new AdbCommandItem { Category = "Affichage", Title = Loc.T("Masquer la barre de navigation (Mode immersif, supprimé par Google à partir d'Android 11)"), Command = "shell settings put global policy_control immersive.navigation=*", MinAndroid = "4.4", MaxAndroid = "10" },
            // Localisation : trois méthodes enchaînées, chacune ignorée sans erreur là où elle n'existe pas
            // ("cmd location" sur les Android récents, "location_providers_allowed" jusqu'à Android 9,
            // "location_mode" sur les versions intermédiaires). Une seule commande couvre ainsi toutes les versions.
            ["shell settings put secure location_mode 3"] = new AdbCommandItem { Category = "GPS", Title = Loc.T("Activer la localisation (Mode Haute Précision)"), Command = "shell cmd location set-location-enabled true 2>/dev/null; settings put secure location_providers_allowed +gps,+network 2>/dev/null; settings put secure location_mode 3", MinAndroid = "4.4" },
            ["shell settings put secure location_mode 0"] = new AdbCommandItem { Category = "GPS", Title = Loc.T("Désactiver totalement la localisation"), Command = "shell cmd location set-location-enabled false 2>/dev/null; settings put secure location_providers_allowed -gps,-network 2>/dev/null; settings put secure location_mode 0", MinAndroid = "4.4" },
        };

        private static AdbCommandItem ApplyLibraryCommandUpdate(AdbCommandItem item)
        {
            if (item?.Command == null || !LibraryCommandUpdates.TryGetValue(item.Command.Trim(), out var updated)) return item;

            item.Title = updated.Title;
            item.Command = updated.Command;
            item.MinAndroid = updated.MinAndroid;
            item.MaxAndroid = updated.MaxAndroid;
            return item;
        }

        // Liste globale qui stocke toutes nos commandes
        private List<AdbCommandItem> _adbLibraryBase = new List<AdbCommandItem>();

        // Chemin du fichier JSON (il sera créé dans le même dossier que l'exécutable)
        private readonly string _adbLibraryFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AdbLibrary.json");

        // 2. INITIALISATION DE LA BIBLIOTHÈQUE (Version JSON Dynamique)
        private void InitializeAdbLibrary()
        {
            try
            {
                // Si le fichier JSON existe déjà, on le lit !
                if (File.Exists(_adbLibraryFilePath) && new FileInfo(_adbLibraryFilePath).Length > 0)
                {
                    try
                    {
                        string json = File.ReadAllText(_adbLibraryFilePath);
                        _adbLibraryBase = NormalizeLibraryItems(JsonSerializer.Deserialize<List<AdbCommandItem>>(json));
                    }
                    catch (Exception exCorrupt)
                    {
                        // Fichier corrompu : on l'isole (pour ne pas replanter à chaque démarrage) et on repart sur la bibliothèque par défaut.
                        try
                        {
                            string backupPath = _adbLibraryFilePath + $".corrompu_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                            File.Move(_adbLibraryFilePath, backupPath);
                            PurgeOldCorruptBackups(_adbLibraryFilePath);
                        }
                        catch { }

                        _adbLibraryBase = GetDefaultAdbLibrary();
                        SaveAdbLibrary();

                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Bibliothèque ADB corrompue ({1}), réinitialisée à la liste par défaut. Ancien fichier conservé en .bak.", DateTime.Now, exCorrupt.GetType().Name));
                    }
                }
                else
                {
                    // S'il n'existe pas, on charge la liste COMPLÈTE par défaut et ON CRÉE le fichier JSON.
                    _adbLibraryBase = GetDefaultAdbLibrary();

                    // On sauvegarde cette liste colossale dans le fichier JSON
                    SaveAdbLibrary();
                }

                AdbLibraryGrid.ItemsSource = null;
                AdbLibraryGrid.ItemsSource = _adbLibraryBase;
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur lors du chargement de la bibliothèque ADB : {1}", DateTime.Now, ex.Message));
                _adbLibraryBase = new List<AdbCommandItem>();
                AdbLibraryGrid.ItemsSource = _adbLibraryBase;
            }
        }

        private List<AdbCommandItem> GetDefaultAdbLibrary()
        {
            return new List<AdbCommandItem>
                    {
                        // ==========================================
                        // 🌐 RÉSEAU & CONNECTIVITÉ
                        // ==========================================
                        new AdbCommandItem { Category = "Réseau", Title = Loc.T("Tester la connexion Internet (Ping Google DNS)"), Command = "shell ping -c 4 8.8.8.8", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Réseau", Title = Loc.T("Désactiver la détection de portail captif"), Command = "shell settings put global captive_portal_mode 0", MinAndroid = "7.0" },
                        new AdbCommandItem { Category = "Réseau", Title = Loc.T("Réactiver la détection de portail captif"), Command = "shell settings put global captive_portal_mode 1", MinAndroid = "7.0" },
                        new AdbCommandItem { Category = "Réseau", Title = Loc.T("Afficher la table de routage IP"), Command = "shell ip route", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Réseau", Title = Loc.T("Réinitialiser les statistiques réseau"), Command = "shell dumpsys netstats --reset", MinAndroid = "6.0" },
                        new AdbCommandItem { Category = "Réseau", Title = Loc.T("Vider le cache DNS du téléphone"), Command = "shell ndc resolver flushdefaultif", MinAndroid = "5.0" },

                        // ==========================================
                        // ⚙️ SYSTÈME & MATÉRIEL
                        // ==========================================
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Obtenir le temps de fonctionnement (Uptime)"), Command = "shell uptime", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Maintenir l'écran allumé pendant la charge"), Command = "shell settings put global stay_on_while_plugged_in 3", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Forcer la synchronisation de l'heure (NTP)"), Command = "shell cmd network_time_update_service force_refresh", MinAndroid = "9.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Afficher l'utilisation détaillée du CPU"), Command = "shell dumpsys cpuinfo", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Afficher l'utilisation détaillée de la RAM"), Command = "shell dumpsys meminfo", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Redémarrer l'interface (SystemUI) - Si gelée"), Command = "shell pkill -f com.android.systemui", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Forcer la sortie du Mode Sécurisé (Safe Mode)"), Command = "shell setprop persist.sys.safemode 0", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Fermer toutes les boîtes de dialogue (Pop-ups bloquantes)"), Command = "shell am broadcast -a android.intent.action.CLOSE_SYSTEM_DIALOGS", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Afficher les 10 processus les plus gourmands (Top)"), Command = "shell top -n 1 -m 10", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Système", Title = Loc.T("Déclencher un scan Media (Actualiser Fichiers)"), Command = "shell am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file:///sdcard/", MinAndroid = "4.0" },

                        // ==========================================
                        // 🛡️ SÉCURITÉ & MDM
                        // ==========================================
                        ApplyLibraryCommandUpdate(new AdbCommandItem { Command = "shell settings put secure install_non_market_apps 1" }),
                        new AdbCommandItem { Category = "Sécurité", Title = Loc.T("Désactiver Google Play Protect (Bloque parfois les apps)"), Command = "shell settings put global package_verifier_enable 0", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Sécurité", Title = Loc.T("Désactiver le verrouillage de l'écran (Swipe/Code)"), Command = "shell locksettings clear --old 1234", MinAndroid = "7.0" },
                        new AdbCommandItem { Category = "Sécurité", Title = Loc.T("Désactiver l'Alerte Volume Élevé (Casques/Bluetooth)"), Command = "shell settings put global audio_safe_volume_state 2", MinAndroid = "7.0" },
                        new AdbCommandItem { Category = "Sécurité", Title = Loc.T("Désactiver la vérification ADB via USB (Bypass)"), Command = "shell settings put global adb_check_allows_all 1", MinAndroid = "9.0" },
                        new AdbCommandItem { Category = "MDM", Title = Loc.T("Vérifier l'agent MDM (Device Owner actif)"), Command = "shell dumpsys device_policy | grep 'Device Owner'", MinAndroid = "7.0" },
                        new AdbCommandItem { Category = "MDM", Title = Loc.T("Lister les profils utilisateurs (Work Profile SOTI/Intune)"), Command = "shell pm list users", MinAndroid = "5.0" },

                        // ==========================================
                        // 📺 AFFICHAGE & ERGONOMIE
                        // ==========================================
                        new AdbCommandItem { Category = "Affichage", Title = Loc.T("Afficher les appuis tactiles (Show touches)"), Command = "shell settings put system show_touches 1", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Affichage", Title = Loc.T("Masquer les appuis tactiles"), Command = "shell settings put system show_touches 0", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Affichage", Title = Loc.T("Afficher les coordonnées du pointeur (Pointer location)"), Command = "shell settings put system pointer_location 1", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Affichage", Title = Loc.T("Masquer les coordonnées du pointeur"), Command = "shell settings put system pointer_location 0", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Affichage", Title = Loc.T("Désactiver l'animation de démarrage (Boot animation)"), Command = "shell setprop debug.sf.nobootanimation 1", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Affichage", Title = Loc.T("Désactiver la mise en veille (Écran toujours allumé)"), Command = "shell settings put system screen_off_timeout 2147483647", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Affichage", Title = Loc.T("Restaurer la mise en veille à 1 minute"), Command = "shell settings put system screen_off_timeout 60000", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Affichage", Title = Loc.T("Accélérer les animations (Sensation de rapidité x0.5)"), Command = "shell settings put global window_animation_scale 0.5", MinAndroid = "4.0" },
                        ApplyLibraryCommandUpdate(new AdbCommandItem { Command = "shell settings put global policy_control immersive.navigation=*" }),

                        // ==========================================
                        // 🎮 INPUT (SIMULATION D'ENTRÉES)
                        // ==========================================
                        new AdbCommandItem { Category = "Input", Title = Loc.T("Simuler la touche 'Retour' (Back)"), Command = "shell input keyevent 4", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Input", Title = Loc.T("Simuler la touche 'Accueil' (Home)"), Command = "shell input keyevent 3", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Input", Title = Loc.T("Balayer l'écran vers le haut (Déverrouiller)"), Command = "shell input swipe 500 1000 500 100", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Input", Title = Loc.T("Mettre le volume média au maximum"), Command = "shell media volume --stream 3 --set 15", MinAndroid = "8.0" },
                        new AdbCommandItem { Category = "Input", Title = Loc.T("Couper le son (Mute)"), Command = "shell media volume --stream 3 --set 0", MinAndroid = "8.0" },

                        // ==========================================
                        // 📦 PACKAGES (GESTION APPS)
                        // ==========================================
                        new AdbCommandItem { Category = "Packages", Title = Loc.T("Lister uniquement les applications tierces (installées)"), Command = "shell pm list packages -3", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Packages", Title = Loc.T("Lister les applications désactivées"), Command = "shell pm list packages -d", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Packages", Title = Loc.T("Vider le cache de TOUTES les applications (Nettoyage)"), Command = "shell pm trim-caches 999G", MinAndroid = "6.0" },
                        new AdbCommandItem { Category = "Packages", Title = Loc.T("Tuer tous les processus en arrière-plan"), Command = "shell am kill-all", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Packages", Title = Loc.T("Forcer l'arrêt du navigateur Chrome"), Command = "shell am force-stop com.android.chrome", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Packages", Title = Loc.T("Vider les données de Chrome (Remise à zéro)"), Command = "shell pm clear com.android.chrome", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Packages", Title = Loc.T("Désactiver le clavier virtuel (Utile si scanneur physique)"), Command = "shell ime disable com.android.inputmethod.latin/.LatinIME", MinAndroid = "7.0" },

                        // ==========================================
                        // 🔋 BATTERIE & ÉNERGIE
                        // ==========================================
                        new AdbCommandItem { Category = "Batterie", Title = Loc.T("Simuler le débranchement (Test de décharge via USB)"), Command = "shell dumpsys battery unplug", MinAndroid = "6.0" },
                        new AdbCommandItem { Category = "Batterie", Title = Loc.T("Rétablir l'état réel de la batterie (Fin du test)"), Command = "shell dumpsys battery reset", MinAndroid = "6.0" },
                        new AdbCommandItem { Category = "Batterie", Title = Loc.T("Afficher l'historique de consommation (Dumpsys)"), Command = "shell dumpsys batterystats", MinAndroid = "5.0" },

                        // ==========================================
                        // ♿ ACCESSIBILITÉ
                        // ==========================================
                        new AdbCommandItem { Category = "Accessibilité", Title = Loc.T("Désactiver TalkBack (Écran figé / Voix agaçante)"), Command = "shell settings put secure enabled_accessibility_services null", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Accessibilité", Title = Loc.T("Inverser les couleurs (Test de la dalle LCD)"), Command = "shell settings put secure accessibility_display_inversion_enabled 1", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Accessibilité", Title = Loc.T("Désactiver l'inversion des couleurs"), Command = "shell settings put secure accessibility_display_inversion_enabled 0", MinAndroid = "5.0" },

                        // ==========================================
                        // 🌍 GPS & LOCALISATION
                        // ==========================================
                        ApplyLibraryCommandUpdate(new AdbCommandItem { Command = "shell settings put secure location_mode 3" }),
                        ApplyLibraryCommandUpdate(new AdbCommandItem { Command = "shell settings put secure location_mode 0" }),

                        // ==========================================
                        // 🔍 INFO & STOCKAGE
                        // ==========================================
                        new AdbCommandItem { Category = "Info", Title = Loc.T("Afficher la version exacte d'Android"), Command = "shell getprop ro.build.version.release", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Info", Title = Loc.T("Afficher le niveau de patch de sécurité"), Command = "shell getprop ro.build.version.security_patch", MinAndroid = "6.0" },
                        new AdbCommandItem { Category = "Info", Title = Loc.T("Afficher l'architecture du processeur (ARM/x86/64)"), Command = "shell getprop ro.product.cpu.abi", MinAndroid = "4.0" },
                        new AdbCommandItem { Category = "Info", Title = Loc.T("Vérifier si le terminal est crypté"), Command = "shell getprop ro.crypto.state", MinAndroid = "5.0" },
                        new AdbCommandItem { Category = "Stockage", Title = Loc.T("Supprimer les miniatures d'images (Gros gain de place)"), Command = "shell rm -rf /sdcard/DCIM/.thumbnails/*", MinAndroid = "4.0" },
                        
                        // ==========================================
                        // 📞 MATÉRIEL SPÉCIFIQUE
                        // ==========================================
                        new AdbCommandItem { Category = "Matériel", Title = Loc.T("Tester le Vibreur (1 seconde)"), Command = "shell cmd vibrator vibrate 1000", MinAndroid = "9.0" },
                        new AdbCommandItem { Category = "Matériel", Title = Loc.T("Allumer la lampe torche (Flashlight Camera)"), Command = "shell cmd statusbar set-camera-flashlight on", MinAndroid = "10.0" }
                    };
        }

        // Méthode utilitaire pour sauvegarder la liste actuelle dans le fichier JSON
        private void SaveAdbLibrary()
        {
            try
            {
                // WriteIndented = true permet d'avoir un fichier JSON bien formaté (lisible par un humain)
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_adbLibraryBase, options);
                File.WriteAllText(_adbLibraryFilePath, json);
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur de sauvegarde JSON : {1}", DateTime.Now, ex.Message));
            }
        }

        // 4. MOTEUR DE RECHERCHE ET FILTRE (Catégorie + Texte)
        private void FilterLibrary_Changed(object sender, RoutedEventArgs e)
        {
            // LE FIX EST ICI : On vérifie que tous les éléments UI existent bien avant de filtrer
            if (_adbLibraryBase == null || CmbCategoryFilter == null || AdbLibraryGrid == null) return;

            string query = TxtSearchCommand?.Text ?? "";
            string category = CmbCategoryFilter.SelectedItem is ComboBoxItem cbi ? (cbi.Tag as string ?? cbi.Content.ToString()) : "Toutes les catégories";

            // Null-safe : un JSON partagé ou édité à la main peut omettre un champ (ex. "Category").
            bool ContainsQuery(string field) => (field ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

            var filteredList = _adbLibraryBase.Where(c =>
                (category == "Toutes les catégories" || c.Category == category) &&
                (ContainsQuery(c.Title) || ContainsQuery(c.Command) || ContainsQuery(c.Category))
            ).ToList();

            AdbLibraryGrid.ItemsSource = filteredList;
        }

        /// <summary>
        /// Nettoie une liste lue depuis un JSON (bibliothèque locale ou import) : retire les entrées vides ou
        /// sans titre/commande, et complète les champs optionnels manquants pour qu'aucun null n'atteigne
        /// l'UI (le filtre plantait sur une commande importée sans "Category").
        /// </summary>
        private static List<AdbCommandItem> NormalizeLibraryItems(IEnumerable<AdbCommandItem> items)
        {
            return (items ?? Enumerable.Empty<AdbCommandItem>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(c.Command))
                .Select(c =>
                {
                    c.Title = c.Title.Trim();
                    c.Command = c.Command.Trim();
                    c.Category = string.IsNullOrWhiteSpace(c.Category) ? "Divers" : c.Category.Trim();
                    c.MinAndroid = string.IsNullOrWhiteSpace(c.MinAndroid) ? "4.0" : c.MinAndroid.Trim();
                    c.MaxAndroid = string.IsNullOrWhiteSpace(c.MaxAndroid) ? null : c.MaxAndroid.Trim();
                    return ApplyLibraryCommandUpdate(c);
                })
                .ToList();
        }

        // --- GESTION DYNAMIQUE DE LA BIBLIOTHÈQUE ---

        // Afficher ou masquer le formulaire d'ajout
        private void BtnShowAddForm_Click(object sender, RoutedEventArgs e)
        {
            if (PanelAddCommand.Visibility == Visibility.Collapsed)
            {
                PanelAddCommand.Visibility = Visibility.Visible;
                BtnShowAddForm.Content = Loc.T("Masquer le formulaire");
            }
            else
            {
                PanelAddCommand.Visibility = Visibility.Collapsed;
                BtnShowAddForm.Content = Loc.T("+ Nouvelle Commande");

                // On vide les cases si on annule
                TxtNewTitle.Text = "";
                TxtNewCommand.Text = "";
                TxtNewCategory.Text = "";
                TxtNewAndroid.Text = "";
            }
        }

        // Sauvegarder la nouvelle commande
        private async void BtnSaveNewCmd_Click(object sender, RoutedEventArgs e)
        {
            // Vérification anti-champs vides
            if (string.IsNullOrWhiteSpace(TxtNewTitle.Text) || string.IsNullOrWhiteSpace(TxtNewCommand.Text))
            {
                await ShowCustomDialogAsync(Loc.T("⚠️ Champs manquants"), Loc.T("Le Titre et la Commande sont obligatoires."), "OK", null, false);
                return;
            }

            // Création de l'objet
            var newCmd = new AdbCommandItem
            {
                Title = TxtNewTitle.Text.Trim(),
                Command = TxtNewCommand.Text.Trim(),
                Category = string.IsNullOrWhiteSpace(TxtNewCategory.Text) ? Loc.T("Divers") : TxtNewCategory.Text.Trim(),
                MinAndroid = string.IsNullOrWhiteSpace(TxtNewAndroid.Text) ? "4.0" : TxtNewAndroid.Text.Trim()
            };

            // Ajout à la liste globale
            _adbLibraryBase.Add(newCmd);

            // Sauvegarde dans le JSON
            SaveAdbLibrary();

            // Rafraîchissement de l'affichage
            FilterLibrary_Changed(null, null);

            // On ferme le formulaire et on notifie
            BtnShowAddForm_Click(null, null);
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💾 Nouvelle commande ajoutée : {1}", DateTime.Now, newCmd.Title));
        }

        // Supprimer une commande via l'icône Corbeille
        private async void BtnDeleteCmd_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var commandObj = button?.DataContext as AdbCommandItem;

            if (commandObj != null)
            {
                // Petite sécurité pour éviter les suppressions accidentelles
                bool confirm = await ShowCustomDialogAsync(Loc.T("🗑️ SUPPRESSION"), Loc.F("Voulez-vous vraiment supprimer la commande :\n'{0}' ?", commandObj.Title), Loc.T("OUI, SUPPRIMER"), Loc.T("Annuler"), true);

                if (confirm)
                {
                    _adbLibraryBase.Remove(commandObj); // Retire de la liste
                    SaveAdbLibrary();                   // Met à jour le JSON
                    FilterLibrary_Changed(null, null);  // Rafraîchit le tableau

                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🗑️ Commande supprimée : {1}", DateTime.Now, commandObj.Title));
                }
            }
        }

        // 6. EXÉCUTION DE LA COMMANDE DEPUIS LE TABLEAU
        private async void BtnExecuteLibraryCmd_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("⚠️ Terminal introuvable"), Loc.T("Veuillez connecter un terminal valide d'abord."), "OK", null, false);
                return;
            }

            // Récupère l'objet associé au bouton cliqué
            var button = sender as Button;
            var commandObj = button?.DataContext as AdbCommandItem;

            if (commandObj != null)
            {
                // La bibliothèque contient des commandes destructrices (vider un cache, réinitialiser un
                // verrouillage...) au même titre que Supprimer/Désinstaller ailleurs dans l'app, qui, eux,
                // confirment toujours avant d'agir — une seule commande n'a pas de classification de
                // dangerosité individuelle, donc on confirme uniformément plutôt que de deviner laquelle est risquée.
                bool confirm = await ShowCustomDialogAsync(Loc.T("Exécuter cette commande ?"), Loc.F("{0}\n\nadb {1}\n\nCompatibilité : Android {2}", commandObj.Title, commandObj.Command, commandObj.AndroidRange) + (string.IsNullOrWhiteSpace(commandObj.MaxAndroid) ? "" : Loc.T(" (sans effet, et sans message d'erreur, sur les versions plus récentes)")) + Loc.F("\n\nCette commande va s'exécuter sur le terminal connecté ({0}).", dev.Serial), Loc.T("Exécuter"), Loc.T("Annuler"), false);
                if (!confirm) return;

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🚀 Exécution : {1} ({2})", DateTime.Now, commandObj.Title, commandObj.Command));

                // On exécute la commande ADB
                var result = await _adbManager.ExecuteAsync($"-s {dev.Serial} {commandObj.Command}");

                string output = result.StandardOutput?.Trim();
                string error = result.StandardError?.Trim();

                // Analyse du résultat : le code de sortie fait foi. Beaucoup de commandes écrivent des
                // informations sur stderr même quand elles réussissent (et inversement certaines échouent
                // avec un code non nul sans rien écrire).
                if (!result.IsSuccess)
                {
                    string detail = !string.IsNullOrEmpty(error) ? error : (!string.IsNullOrEmpty(output) ? output : "aucun détail renvoyé");
                    AppendConsole(Loc.F("\n[Erreur — code {0}]\n{1}\n", result.ExitCode, detail));
                }
                else if (!string.IsNullOrEmpty(output) || !string.IsNullOrEmpty(error))
                {
                    AppendConsole(Loc.F("\n[Résultat]\n{0}\n", string.Join("\n", new[] { output, error }.Where(s => !string.IsNullOrEmpty(s)))));
                }
                else
                {
                    // Le fameux cas silencieux : code 0 + aucun texte = Succès !
                    AppendConsole(Loc.F("\n[Résultat]\n✔️ Commande envoyée avec succès (Aucun retour système).\n"));
                }
            }
        }

        // --- EXPORT / IMPORT (PARTAGE D'ÉQUIPE) ---

        private void BtnExportLibrary_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"BibliothequeADB_{DateTime.Now:yyyyMMdd}.json",
                DefaultExt = ".json",
                Filter = "Bibliothèque ADB (*.json)|*.json"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_adbLibraryBase, options);
                File.WriteAllText(dlg.FileName, json);

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⬆️ Bibliothèque exportée ({1} commandes) : {2}", DateTime.Now, _adbLibraryBase.Count, dlg.FileName));
                ShowInExplorer(dlg.FileName);
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur lors de l'export : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void BtnImportLibrary_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".json",
                Filter = "Bibliothèque ADB (*.json)|*.json"
            };

            if (dlg.ShowDialog() != true) return;

            List<AdbCommandItem> imported;
            try
            {
                string json = File.ReadAllText(dlg.FileName);
                var raw = JsonSerializer.Deserialize<List<AdbCommandItem>>(json) ?? new List<AdbCommandItem>();
                imported = NormalizeLibraryItems(raw);
                int invalid = raw.Count - imported.Count;
                if (invalid > 0)
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ {1} entrée(s) sans titre ou sans commande ignorée(s) dans le fichier importé.", DateTime.Now, invalid));
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Fichier de bibliothèque invalide : {1}", DateTime.Now, ex.Message));
                return;
            }

            var toAdd = NewLibraryItems(imported);

            int skipped = imported.Count - toAdd.Count;

            if (toAdd.Count == 0)
            {
                await ShowCustomDialogAsync(Loc.T("Import terminé"), Loc.F("Aucune nouvelle commande à ajouter : les {0} commande(s) du fichier sont déjà présentes dans ta bibliothèque.", imported.Count), "OK", null, false);
                return;
            }

            bool confirm = await ShowCustomDialogAsync(Loc.T("Importer la bibliothèque"),
                Loc.F("{0} nouvelle(s) commande(s) seront ajoutées à ta bibliothèque", toAdd.Count) + (skipped > 0 ? Loc.F(" ({0} déjà présente(s) et ignorée(s)).", skipped) : ".") + Loc.T("\n\nContinuer ?"),
                Loc.T("Importer"), Loc.T("Annuler"), false);
            if (!confirm) return;

            _adbLibraryBase.AddRange(toAdd);
            SaveAdbLibrary();
            FilterLibrary_Changed(null, null);

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⬇️ Bibliothèque importée : {1} commande(s) ajoutée(s), {2} ignorée(s) (doublons).", DateTime.Now, toAdd.Count, skipped));
        }

        /// <summary>
        /// Commandes à ajouter lors d'une fusion : une commande est un doublon (ignorée) si une commande existante
        /// a le même Titre ET la même Commande — jamais de suppression ni de remplacement de l'existant, pour ne
        /// jamais faire perdre de commandes personnalisées.
        /// </summary>
        private List<AdbCommandItem> NewLibraryItems(IEnumerable<AdbCommandItem> imported)
        {
            var existingKeys = new HashSet<string>(_adbLibraryBase.Select(c => c.Title + "\u0001" + c.Command));
            return imported.Where(c => !string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(c.Command)
                                       && existingKeys.Add(c.Title + "\u0001" + c.Command)).ToList();
        }

        /// <summary>Bibliothèque actuelle au format JSON (export et profil d'équipe).</summary>
        private string LibraryToJson() => JsonSerializer.Serialize(_adbLibraryBase, new JsonSerializerOptions { WriteIndented = true });

        /// <summary>Fusionne une bibliothèque JSON (profil d'équipe) : renvoie le nombre de commandes ajoutées.</summary>
        private int MergeLibraryJson(string json)
        {
            var raw = JsonSerializer.Deserialize<List<AdbCommandItem>>(json) ?? new List<AdbCommandItem>();
            var toAdd = NewLibraryItems(NormalizeLibraryItems(raw));
            if (toAdd.Count == 0) return 0;
            _adbLibraryBase.AddRange(toAdd);
            SaveAdbLibrary();
            FilterLibrary_Changed(null, null);
            return toAdd.Count;
        }

        // 7. COPIER LA COMMANDE DANS LE PRESSE-PAPIERS
        private void BtnCopyLibraryCmd_Click(object sender, RoutedEventArgs e)
        {
            // Récupère l'objet associé au bouton cliqué
            var button = sender as Button;
            var commandObj = button?.DataContext as AdbCommandItem;

            if (commandObj != null && !string.IsNullOrEmpty(commandObj.Command))
            {
                // On ajoute automatiquement "adb " devant la commande pour qu'elle soit 100% prête à l'emploi
                string fullCommand = $"adb {commandObj.Command}";

                // Copie dans le presse-papiers de Windows
                Clipboard.SetText(fullCommand);

                // Petit retour visuel dans la console de l'application
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📋 Commande copiée dans le presse-papiers : {1}\n", DateTime.Now, fullCommand));
                ConsoleOutput.ScrollToEnd();
            }
        }
    }
}
