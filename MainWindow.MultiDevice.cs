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
        // ==========================================
        // === MOTEUR GÉNÉRIQUE MULTI-APPAREILS (sélecteur + confirmation + exécution) ===
        // Socle volontairement restreint à 3 usages connus (commande ADB, mirroring, installation APK) :
        // Device Info, Package Manager (hors install) et Explorer restent strictement mono-appareil.
        // ==========================================

        // Action à exécuter pour CHAQUE appareil coché, fournie par le point d'entrée qui ouvre le
        // sélecteur (commande ADB, lancement scrcpy, installation APK...). Retourne (succès, message
        // à afficher en Console).
        private Func<AndroidDevice, Task<(bool Success, string Message)>> _multiDeviceAction;

        // Texte affiché dans la confirmation, avant la liste des numéros de série ciblés.
        private string _multiDeviceConfirmBody;

        // Style du bouton de confirmation : rouge (Danger) uniquement pour la commande ADB libre, qui
        // n'a aucun niveau de dangerosité connu à l'avance — mirroring et installation ne sont pas
        // destructeurs, donc confirmation en bleu (Primary).
        private bool _multiDeviceConfirmIsDanger = true;

        // Callback optionnel invoqué une fois la boucle terminée (ex: rafraîchir le tableau de paquets),
        // avec la liste des appareils effectivement traités. Remis à null après chaque exécution pour ne
        // pas fuiter vers le prochain usage du sélecteur.
        private Action<List<AndroidDevice>> _multiDeviceOnComplete;

        /// <summary>
        /// Ouvre le sélecteur d'appareils. <paramref name="fieldLabel"/> à null masque entièrement la ligne
        /// de saisie (cas du mirroring, qui n'a besoin que de la liste d'appareils).
        /// </summary>
        private async Task OpenMultiDeviceDialogAsync(string title, string fieldLabel, string fieldText, bool fieldReadOnly)
        {
            TxtMultiDeviceTitle.Text = title;

            if (fieldLabel == null)
            {
                MultiDeviceFieldPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                MultiDeviceFieldPanel.Visibility = Visibility.Visible;
                TxtMultiDeviceFieldLabel.Text = fieldLabel;
                TxtMultiDeviceCommand.Text = fieldText;
                TxtMultiDeviceCommand.IsReadOnly = fieldReadOnly;
                TxtMultiDeviceCommand.Opacity = fieldReadOnly ? 0.75 : 1.0;
            }

            BtnMultiDeviceRun.IsEnabled = false;
            BtnMultiDeviceRun.Content = "Exécuter";
            MultiDeviceOverlay.Visibility = Visibility.Visible;

            await PopulateMultiDeviceCheckListAsync();
        }

        /// <summary>Relit la liste des appareils actuellement connectés (même logique que ParseAdbDevices,
        /// déjà utilisée par le watcher) et construit une case à cocher par appareil, non liée par
        /// data-binding — cohérent avec le reste de l'app, qui n'utilise pas de pattern MVVM.</summary>
        private async Task PopulateMultiDeviceCheckListAsync()
        {
            MultiDeviceCheckList.Children.Clear();

            var result = await _adbManager.ExecuteAsync("devices");
            var devices = result.IsSuccess ? ParseAdbDevices(result.StandardOutput) : new List<AndroidDevice>();

            if (devices.Count == 0)
            {
                MultiDeviceCheckList.Children.Add(new TextBlock
                {
                    Text = "Aucun appareil détecté.",
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
                    FontStyle = FontStyles.Italic
                });
                return;
            }

            foreach (var dev in devices)
            {
                bool selectable = dev.State == "device";
                var chk = new CheckBox
                {
                    Content = selectable ? dev.Serial : $"{dev.Serial}  [⚠️ {dev.State.ToUpper()}]",
                    IsEnabled = selectable,
                    Tag = dev,
                    Style = (Style)FindResource("ModernCheckBoxStyle"),
                    Margin = new Thickness(0, 6, 0, 6)
                };
                chk.Checked += MultiDeviceCheckBox_Changed;
                chk.Unchecked += MultiDeviceCheckBox_Changed;
                MultiDeviceCheckList.Children.Add(chk);
            }
        }

        private void MultiDeviceCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            int count = GetCheckedMultiDevices().Count;
            BtnMultiDeviceRun.IsEnabled = count > 0;
            BtnMultiDeviceRun.Content = count > 0 ? $"Exécuter sur {count} appareil(s)" : "Exécuter";
        }

        private List<AndroidDevice> GetCheckedMultiDevices() =>
            MultiDeviceCheckList.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag as AndroidDevice)
                .Where(d => d != null)
                .ToList();

        private void BtnMultiDeviceSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var chk in MultiDeviceCheckList.Children.OfType<CheckBox>().Where(c => c.IsEnabled))
                chk.IsChecked = true;
        }

        private void BtnMultiDeviceCancel_Click(object sender, RoutedEventArgs e)
        {
            MultiDeviceOverlay.Visibility = Visibility.Collapsed;
            // Annulé : le callback de fin préparé pour cet usage ne doit pas se déclencher lors d'un usage ultérieur.
            _multiDeviceOnComplete = null;
        }

        private async void BtnMultiDeviceRun_Click(object sender, RoutedEventArgs e)
        {
            var targets = GetCheckedMultiDevices();
            if (targets.Count == 0 || _multiDeviceAction == null) return;

            // Le sélecteur doit être masqué AVANT d'ouvrir la confirmation : les deux overlays partagent le
            // même Panel.ZIndex, donc laisser le sélecteur visible l'affichait par-dessus la confirmation
            // (déclaré après lui dans le XAML) au lieu de dessous.
            MultiDeviceOverlay.Visibility = Visibility.Collapsed;

            string serialsList = string.Join("\n", targets.Select(d => d.Serial));
            bool confirm = await ShowCustomDialogAsync(
                "Exécuter sur plusieurs appareils",
                $"{_multiDeviceConfirmBody}\n\nAppareils ciblés ({targets.Count}) :\n{serialsList}",
                "Confirmer",
                "Annuler",
                _multiDeviceConfirmIsDanger
            );
            if (!confirm) return;

            BtnMultiDeviceRun.IsEnabled = false;
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🖥️ Lancement sur {targets.Count} terminal(aux)...");

            foreach (var dev in targets)
            {
                try
                {
                    var (success, message) = await _multiDeviceAction(dev);
                    AppendConsole(success
                        ? $"\n[{DateTime.Now:HH:mm:ss}]   ✔️ {dev.Serial} : {message}"
                        : $"\n[{DateTime.Now:HH:mm:ss}]   ❌ {dev.Serial} : {message}");
                }
                catch (Exception ex)
                {
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}]   ❌ {dev.Serial} : {ex.Message}");
                }
            }

            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🖥️ Traitement multi-appareils terminé.");
            BtnMultiDeviceRun.IsEnabled = true;

            _multiDeviceOnComplete?.Invoke(targets);
            _multiDeviceOnComplete = null;
        }

        // --- POINT D'ENTRÉE 1 : COMMANDE ADB (Bibliothèque ADB, ligne pré-remplie ou champ libre) ---

        private async void BtnExecuteLibraryCmdMulti_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var commandObj = button?.DataContext as AdbCommandItem;
            if (commandObj == null) return;

            await OpenAdbCommandMultiDeviceAsync(commandObj.Command, commandReadOnly: true);
        }

        private async void BtnMultiDeviceFreeCommand_Click(object sender, RoutedEventArgs e)
        {
            await OpenAdbCommandMultiDeviceAsync("", commandReadOnly: false);
        }

        private async Task OpenAdbCommandMultiDeviceAsync(string command, bool commandReadOnly)
        {
            _multiDeviceAction = async (dev) =>
            {
                // La commande est relue au moment de l'exécution (pas capturée à l'ouverture) : dans le cas
                // du champ libre, le technicien la tape après ouverture du sélecteur.
                string cmd = TxtMultiDeviceCommand.Text.Trim();
                // Tolère "adb ..." tapé par habitude : "-s <serial>" est déjà ajouté devant.
                if (cmd.StartsWith("adb ", StringComparison.OrdinalIgnoreCase)) cmd = cmd.Substring(4).TrimStart();
                var result = await _adbManager.ExecuteAsync($"-s {dev.Serial} {cmd}");
                string outText = result.StandardOutput?.Trim() ?? "";
                string errText = result.StandardError?.Trim() ?? "";

                // Le code de sortie fait foi (stderr peut contenir de simples informations).
                if (!result.IsSuccess)
                    return (false, !string.IsNullOrEmpty(errText) ? errText : (!string.IsNullOrEmpty(outText) ? outText : $"échec (code {result.ExitCode})."));
                if (!string.IsNullOrEmpty(outText)) return (true, outText);
                if (!string.IsNullOrEmpty(errText)) return (true, errText);
                return (true, "commande envoyée avec succès.");
            };
            _multiDeviceOnComplete = null; // Pas de reste d'un usage précédent du sélecteur (ex. installation annulée)
            _multiDeviceConfirmBody = commandReadOnly
                ? $"La commande suivante va s'exécuter :\n\nadb {command}"
                : "La commande saisie ci-dessous va s'exécuter (vérifiez-la avant de confirmer).";
            _multiDeviceConfirmIsDanger = true; // Commande non classifiée : on suppose le pire par défaut

            await OpenMultiDeviceDialogAsync("🖥️ Exécuter sur plusieurs appareils", "Commande", command, commandReadOnly);
        }
    }
}
