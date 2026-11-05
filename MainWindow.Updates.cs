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

        private UpdateCheck _lastUpdateCheck;

        /// <summary>Interroge le dossier de l'équipe et GitHub, puis met à jour l'affichage (Paramètres + barre du haut).</summary>
        private async Task CheckAppUpdateAsync(bool userAsked)
        {
            if (_updateBusy) return;
            _updateBusy = true;
            if (userAsked) SetUpdateStatus(Loc.T("Recherche d'une nouvelle version…"), null);
            try
            {
                _lastUpdateCheck = await AppUpdater.CheckAsync();
                _latestRelease = _lastUpdateCheck.Latest;
                UpdateAppUpdateUi();
                if (AppUpdater.IsUpdate(_latestRelease))
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔔 Nouvelle version d'ADB Tools disponible : {1} ({2}) — Paramètres ▸ Mises à jour.", DateTime.Now, _latestRelease.VersionText, _latestRelease.SourceLabel));
                }
            }
            catch (Exception ex)
            {
                if (userAsked) SetUpdateStatus(Loc.T("La recherche a échoué (") + ex.Message + Loc.T("). Réessayez plus tard."), "BadFg");
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
            TxtUpdCurrent.Text = Loc.F("Version installée : {0}", App.VersionText);
            if (ChkUpdAuto != null) ChkUpdAuto.IsChecked = AppUpdater.AutoCheckEnabled;
            string share = AppUpdater.ShareFolder;
            TxtUpdShare.Text = string.IsNullOrWhiteSpace(share) ? Loc.T("Aucun (GitHub seulement, si le dépôt est public)") : share;
            BtnUpdShareClear.Visibility = string.IsNullOrWhiteSpace(share) ? Visibility.Collapsed : Visibility.Visible;

            bool available = AppUpdater.IsUpdate(_latestRelease);
            BtnUpdateAvailable.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            if (available) TxtUpdateAvailable.Text = Loc.F("Mise à jour {0}", _latestRelease.VersionText);

            BtnUpdNotes.Visibility = _latestRelease != null && !string.IsNullOrWhiteSpace(_latestRelease.Notes) ? Visibility.Visible : Visibility.Collapsed;
            BtnUpdNotes.Content = available ? Loc.F("Nouveautés de la {0}", _latestRelease.VersionText) : Loc.T("Nouveautés de cette version");

            // Détail des sources qui n'ont pas répondu (dépôt privé, partage introuvable...), en petit sous l'état.
            var problems = new System.Collections.Generic.List<string>();
            if (_lastUpdateCheck?.ShareProblem != null) problems.Add(Loc.F("Dossier de l'équipe : {0}.", _lastUpdateCheck.ShareProblem));
            if (_lastUpdateCheck?.GitHubProblem != null) problems.Add(Loc.F("GitHub : {0}.", _lastUpdateCheck.GitHubProblem));
            TxtUpdSources.Text = string.Join("\n", problems);
            TxtUpdSources.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_lastUpdateCheck == null)
            {
                BtnUpdInstall.Visibility = Visibility.Collapsed;
                SetUpdateStatus(Loc.T("Aucune vérification pour le moment. Cliquez sur « Rechercher maintenant »."), null);
                return;
            }

            if (_latestRelease == null)
            {
                BtnUpdInstall.Visibility = Visibility.Collapsed;
                SetUpdateStatus(string.IsNullOrWhiteSpace(share)
                    ? Loc.T("Aucune version trouvée. Si le dépôt GitHub est privé, choisissez le dossier de diffusion de l'équipe ci-dessous.")
                    : Loc.T("Aucune version trouvée pour le moment."), "WarnFg");
                return;
            }

            if (!available)
            {
                BtnUpdInstall.Visibility = Visibility.Collapsed;
                SetUpdateStatus(Loc.F("ADB Tools {0} est à jour (dernière version disponible : {1}, {2}).", App.VersionText, _latestRelease.VersionText, _latestRelease.SourceLabel), "OkFg");
                return;
            }

            string date = _latestRelease.PublishedAt.HasValue ? $", publiée le {_latestRelease.PublishedAt.Value.ToLocalTime():dd/MM/yyyy}" : "";
            if (AppUpdater.CanInstall(_latestRelease))
            {
                BtnUpdInstall.Content = Loc.F("Installer la version {0}", _latestRelease.VersionText);
                BtnUpdInstall.Visibility = Visibility.Visible;
                double mo = _latestRelease.ZipSize / 1024.0 / 1024.0;
                SetUpdateStatus(Loc.F("Nouvelle version disponible : {0}{1} ({2:0.0} Mo, {3}). ", _latestRelease.VersionText, date, mo, _latestRelease.SourceLabel) +
                                Loc.T("L'installation remplace uniquement les fichiers du programme : vos modèles, outils, captures et réglages sont conservés."), "Ink");
            }
            else
            {
                BtnUpdInstall.Visibility = Visibility.Collapsed;
                SetUpdateStatus(Loc.F("Nouvelle version disponible : {0}{1}. Elle s'installe à la main depuis la page des versions.", _latestRelease.VersionText, date), "Ink");
            }
        }

        private void SetUpdateStatus(string text, string brushKey)
        {
            if (TxtUpdStatus == null) return;
            TxtUpdStatus.Text = text;
            TxtUpdStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey ?? "Muted");
        }

        // ------------------------------------------------------------------ Boutons

        /// <summary>Pastille « Mise à jour X » de la barre de titre : propose directement l'installation (comme Scanalyse).</summary>
        private void BtnUpdateAvailable_Click(object sender, RoutedEventArgs e)
        {
            if (AppUpdater.IsUpdate(_latestRelease) && AppUpdater.CanInstall(_latestRelease))
                BtnUpdInstall_Click(sender, e);
            else
                OpenSettings("Updates");
        }

        // ------------------------------------------------------------------ Fenêtre d'installation

        private TaskCompletionSource<bool> _updateDialogTcs;

        /// <summary>« Installer ADB Tools X ? » avec les nouveautés de la version ; vrai si l'utilisateur confirme.</summary>
        private Task<bool> ShowUpdateDialogAsync(AppRelease release)
        {
            TxtUpdateDialogTitle.Text = Loc.F("Installer ADB Tools {0} ?", release.VersionText);
            string notes = AppUpdater.FormatReleaseNotes(release.Notes);
            TxtUpdateDialogNotes.Text = notes;
            TxtUpdateDialogNotes.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDialog.Visibility = Visibility.Visible;
            _updateDialogTcs?.TrySetResult(false);
            _updateDialogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            BtnUpdateDialogInstall.Focus();
            return _updateDialogTcs.Task;
        }

        private void BtnUpdateDialogInstall_Click(object sender, RoutedEventArgs e)
        {
            UpdateDialog.Visibility = Visibility.Collapsed;
            _updateDialogTcs?.TrySetResult(true);
        }

        private void BtnUpdateDialogCancel_Click(object sender, RoutedEventArgs e)
        {
            UpdateDialog.Visibility = Visibility.Collapsed;
            _updateDialogTcs?.TrySetResult(false);
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

        private async void BtnUpdShareChoose_Click(object sender, RoutedEventArgs e)
        {
            string folder = PickFolder("Dossier de diffusion de l'équipe (où l'on dépose ADBToolsPro-X.Y.Z.zip)", AppUpdater.ShareFolder);
            if (folder == null) return;
            AppUpdater.ShareFolder = folder;
            UpdateAppUpdateUi();
            BtnUpdCheck.IsEnabled = false;
            await CheckAppUpdateAsync(true);
        }

        private async void BtnUpdShareClear_Click(object sender, RoutedEventArgs e)
        {
            AppUpdater.ShareFolder = "";
            UpdateAppUpdateUi();
            BtnUpdCheck.IsEnabled = false;
            await CheckAppUpdateAsync(true);
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
            string url = _latestRelease != null && !_latestRelease.FromShare ? _latestRelease.PageUrl : AppUpdater.ReleasesPage;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private async void BtnUpdInstall_Click(object sender, RoutedEventArgs e)
        {
            var release = _latestRelease;
            if (!AppUpdater.IsUpdate(release) || !AppUpdater.CanInstall(release) || _updateBusy) return;

            if (_isSideloading)
            {
                await ShowCustomDialogAsync(Loc.T("⏳ Opération en cours"), Loc.T("Une mise à jour du terminal (Sideload) est en cours. Attendez qu'elle soit terminée avant de mettre à jour ADB Tools."), "OK", null, false);
                return;
            }
            if (!AppUpdater.AppFolderIsWritable())
            {
                await ShowCustomDialogAsync(Loc.T("🔒 Dossier protégé"),
                    Loc.T("ADB Tools ne peut pas modifier son propre dossier (par exemple dans « Program Files »).\n\n") +
                    Loc.T("Téléchargez la nouvelle version depuis la page des versions et décompressez-la à la place de l'ancienne, ou déplacez ADB Tools dans un dossier personnel (Documents, Bureau...)."),
                    "OK", null, false);
                return;
            }

            if (!await ShowUpdateDialogAsync(release)) return;
            OpenSettings("Updates"); // la progression du téléchargement s'affiche dans Paramètres ▸ Mises à jour

            _updateBusy = true;
            BtnUpdInstall.IsEnabled = false;
            BtnUpdCheck.IsEnabled = false;
            UpdProgress.Value = 0;
            UpdProgress.Visibility = Visibility.Visible;
            try
            {
                SetUpdateStatus(Loc.F("Téléchargement de la version {0}…", release.VersionText), null);
                var progress = new Progress<double>(p =>
                {
                    UpdProgress.Value = p;
                    SetUpdateStatus(Loc.F("Téléchargement de la version {0}… {1:0} %", release.VersionText, p), null);
                });
                string folder = await AppUpdater.DownloadAsync(release, progress);

                SetUpdateStatus(Loc.T("Installation : ADB Tools va redémarrer…"), "Ink");
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⬆️ Installation de la version {1} : redémarrage d'ADB Tools.", DateTime.Now, release.VersionText));
                AppUpdater.StartInstallAndExit(folder);
                await Task.Delay(300);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                UpdProgress.Visibility = Visibility.Collapsed;
                SetUpdateStatus(Loc.T("La mise à jour n'a pas pu être installée : ") + ex.Message, "BadFg");
                BtnUpdInstall.IsEnabled = true;
                BtnUpdCheck.IsEnabled = true;
                _updateBusy = false;
            }
        }
    }
}
