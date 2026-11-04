using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using ADBToolsNew.Core;

namespace ADBToolsNew
{
    /// <summary>
    /// Mises à jour d'ADB Tools lui-même (Paramètres ▸ Mises à jour) : recherche au démarrage, bouton discret dans la
    /// barre du haut quand une nouvelle version est disponible, téléchargement vérifié puis remplacement automatique.
    /// </summary>
    public partial class MainWindow
    {
        private AppRelease _latestRelease;
        private bool _updateBusy;

        /// <summary>Au démarrage : recherche une nouvelle version, sans rien bloquer ni afficher d'erreur.</summary>
        private async Task CheckAppUpdateAtStartupAsync()
        {
            if (!AppUpdater.AutoCheckEnabled) return;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3)); // laisse le démarrage se terminer
                await CheckAppUpdateAsync(false);
            }
            catch
            {
                // Pas de connexion, GitHub indisponible... : la vérification est un bonus, jamais une erreur.
            }
        }

        /// <summary>Interroge GitHub et met à jour l'affichage (page Paramètres + bouton de la barre du haut).</summary>
        private async Task CheckAppUpdateAsync(bool userAsked)
        {
            if (_updateBusy) return;
            _updateBusy = true;
            if (userAsked) SetUpdateStatus("Recherche d'une nouvelle version…", null);
            try
            {
                _latestRelease = await AppUpdater.GetLatestAsync();
                UpdateAppUpdateUi();
                if (AppUpdater.IsUpdate(_latestRelease))
                {
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🔔 Nouvelle version d'ADB Tools disponible : {_latestRelease.VersionText} (Paramètres ▸ Mises à jour).");
                }
            }
            catch (Exception ex)
            {
                if (userAsked) SetUpdateStatus("Impossible de joindre GitHub pour le moment (" + ex.Message + "). Vérifiez la connexion Internet et réessayez.", "BadFg");
                else throw;
            }
            finally
            {
                _updateBusy = false;
                if (BtnUpdCheck != null) BtnUpdCheck.IsEnabled = true;
            }
        }

        /// <summary>Remplit la page Paramètres ▸ Mises à jour et affiche/masque le bouton de la barre du haut.</summary>
        private void UpdateAppUpdateUi()
        {
            if (TxtUpdCurrent == null) return;
            TxtUpdCurrent.Text = $"Version installée : {App.VersionText}";
            if (ChkUpdAuto != null) ChkUpdAuto.IsChecked = AppUpdater.AutoCheckEnabled;

            bool available = AppUpdater.IsUpdate(_latestRelease);
            BtnUpdateAvailable.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            if (available) TxtUpdateAvailable.Text = $"Mise à jour {_latestRelease.VersionText}";

            BtnUpdNotes.Visibility = _latestRelease != null && !string.IsNullOrWhiteSpace(_latestRelease.Notes) ? Visibility.Visible : Visibility.Collapsed;
            BtnUpdNotes.Content = available ? $"Nouveautés de la {_latestRelease.VersionText}" : "Nouveautés de cette version";

            if (_latestRelease == null)
            {
                BtnUpdInstall.Visibility = Visibility.Collapsed;
                SetUpdateStatus("Aucune vérification pour le moment. Cliquez sur « Rechercher » pour vérifier.", null);
                return;
            }

            if (!available)
            {
                BtnUpdInstall.Visibility = Visibility.Collapsed;
                SetUpdateStatus($"ADB Tools est à jour (dernière version publiée : {_latestRelease.VersionText}).", "OkFg");
                return;
            }

            string date = _latestRelease.PublishedAt.HasValue ? $", publiée le {_latestRelease.PublishedAt.Value.ToLocalTime():dd/MM/yyyy}" : "";
            if (AppUpdater.CanInstall(_latestRelease))
            {
                BtnUpdInstall.Content = $"Installer la version {_latestRelease.VersionText}";
                BtnUpdInstall.Visibility = Visibility.Visible;
                double mo = _latestRelease.ZipSize / 1024.0 / 1024.0;
                SetUpdateStatus($"Nouvelle version disponible : {_latestRelease.VersionText}{date} ({mo:0.0} Mo). " +
                                "L'installation remplace uniquement les fichiers du programme : vos modèles, outils, captures et réglages sont conservés.", "Ink");
            }
            else
            {
                BtnUpdInstall.Visibility = Visibility.Collapsed;
                SetUpdateStatus($"Nouvelle version disponible : {_latestRelease.VersionText}{date}. Elle s'installe à la main depuis la page des versions.", "Ink");
            }
        }

        private void SetUpdateStatus(string text, string brushKey)
        {
            if (TxtUpdStatus == null) return;
            TxtUpdStatus.Text = text;
            TxtUpdStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey ?? "Muted");
        }

        // ------------------------------------------------------------------ Boutons

        private void BtnUpdateAvailable_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings("Updates");
        }

        private async void BtnUpdCheck_Click(object sender, RoutedEventArgs e)
        {
            BtnUpdCheck.IsEnabled = false;
            await CheckAppUpdateAsync(true);
        }

        private void ChkUpdAuto_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkUpdAuto == null || !IsLoaded) return; // ignoré pendant le chargement de la fenêtre
            AppUpdater.AutoCheckEnabled = ChkUpdAuto.IsChecked == true;
        }

        private void BtnUpdNotes_Click(object sender, RoutedEventArgs e)
        {
            if (_latestRelease == null) return;
            // Notes au format Markdown (GitHub) : on retire la mise en forme que la fenêtre de document n'interprète pas.
            string notes = Regex.Replace(_latestRelease.Notes ?? "", @"\*\*|`", "");
            notes = Regex.Replace(notes, @"\[([^\]]+)\]\([^)]+\)", "$1");
            DocumentWindow.ShowText(this, "Quoi de neuf", $"ADB Tools Pro {_latestRelease.VersionText}", notes);
        }

        private void BtnUpdPage_Click(object sender, RoutedEventArgs e)
        {
            string url = _latestRelease?.PageUrl ?? AppUpdater.ReleasesPage;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private async void BtnUpdInstall_Click(object sender, RoutedEventArgs e)
        {
            var release = _latestRelease;
            if (!AppUpdater.IsUpdate(release) || !AppUpdater.CanInstall(release) || _updateBusy) return;

            if (_isSideloading)
            {
                await ShowCustomDialogAsync("⏳ Opération en cours", "Une mise à jour du terminal (Sideload) est en cours. Attendez qu'elle soit terminée avant de mettre à jour ADB Tools.", "OK", null, false);
                return;
            }
            if (!AppUpdater.AppFolderIsWritable())
            {
                await ShowCustomDialogAsync("🔒 Dossier protégé",
                    "ADB Tools ne peut pas modifier son propre dossier (par exemple dans « Program Files »).\n\n" +
                    "Téléchargez la nouvelle version depuis la page des versions et décompressez-la à la place de l'ancienne, ou déplacez ADB Tools dans un dossier personnel (Documents, Bureau...).",
                    "OK", null, false);
                return;
            }

            bool go = await ShowCustomDialogAsync($"⬆️ Installer la version {release.VersionText} ?",
                "ADB Tools va télécharger la nouvelle version, vérifier son intégrité, puis redémarrer pour l'installer.\n\n" +
                "Vos modèles de rapport, outils tiers, captures et réglages sont conservés.",
                "Installer et redémarrer", "Annuler", false);
            if (!go) return;

            _updateBusy = true;
            BtnUpdInstall.IsEnabled = false;
            BtnUpdCheck.IsEnabled = false;
            UpdProgress.Value = 0;
            UpdProgress.Visibility = Visibility.Visible;
            try
            {
                SetUpdateStatus($"Téléchargement de la version {release.VersionText}…", null);
                var progress = new Progress<double>(p =>
                {
                    UpdProgress.Value = p;
                    SetUpdateStatus($"Téléchargement de la version {release.VersionText}… {p:0} %", null);
                });
                string folder = await AppUpdater.DownloadAsync(release, progress);

                SetUpdateStatus("Installation : ADB Tools va redémarrer…", "Ink");
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ⬆️ Installation de la version {release.VersionText} : redémarrage d'ADB Tools.");
                AppUpdater.StartInstallAndExit(folder);
                await Task.Delay(300);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                UpdProgress.Visibility = Visibility.Collapsed;
                SetUpdateStatus("La mise à jour n'a pas pu être installée : " + ex.Message, "BadFg");
                BtnUpdInstall.IsEnabled = true;
                BtnUpdCheck.IsEnabled = true;
                _updateBusy = false;
            }
        }
    }
}
