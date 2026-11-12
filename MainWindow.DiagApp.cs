﻿using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace ADBToolsNew
{
    /// <summary>
    /// Appli ADB Tools Diag sur le terminal (DiagApp\ADBToolsDiag.apk) : tests du terminal, test du lecteur, mesure
    /// Wi-Fi, suivi terrain, pilotés par le PC ou utilisés seuls sur le terminal. Elle reste installée jusqu'à ce que
    /// l'utilisateur la retire (bouton « Appli Diag » de la barre du haut). Installation : autorisations accordées
    /// d'office (adb install -g), plus les droits réservés au débogage (journal Android, état du système).
    /// </summary>
    public partial class MainWindow
    {
        private string _diagAppSerial;
        private readonly HashSet<string> _diagAppUsers = new HashSet<string>();
        private DateTime _diagAppPopupLastClosed;
        private int _diagAppInstalledVersion = -1;

        // Écoute de l'appli sur le terminal sélectionné : « Envoyer au PC » ouvre la fenêtre de récupération.
        private readonly AdbStreamProcess _diagWatch = new AdbStreamProcess();
        private string _diagWatchSerial;

        private void UpdateDiagWatch(string serial, bool installed)
        {
            if (serial == null || !installed)
            {
                try { _diagWatch.Stop(); } catch { }
                _diagWatchSerial = null;
                return;
            }
            if (_diagWatchSerial == serial && _diagWatch.IsRunning) return;
            _diagWatchSerial = serial;
            try
            {
                _diagWatch.Start(_adbExePath, DiagApp.LogcatArgs(serial), line =>
                {
                    var m = DiagMessage.Parse(line);
                    if (m != null && m.Type == "EXPORT") Dispatcher.InvokeAsync(() => OnDiagAppExport(serial, m.Test));
                });
            }
            catch { _diagWatchSerial = null; }
        }

        /// <summary>Version installée sur le terminal (0 : absente, -1 : illisible).</summary>
        private async Task<int> GetDiagAppVersionAsync(string serial)
        {
            try
            {
                var res = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys package {DiagApp.Package}", TimeSpan.FromSeconds(15));
                return DiagApp.ParseInstalledVersion(res.StandardOutput);
            }
            catch { return -1; }
        }

        /// <summary>Installe ou met à jour l'appli si besoin. Faux (avec message) si l'installation est refusée.</summary>
        private async Task<bool> EnsureDiagAppAsync(string serial, string user)
        {
            if (!File.Exists(DiagApp.ApkPath))
            {
                await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                    Loc.F("L'appli de diagnostic est introuvable :\n{0}\n\nElle est fournie avec les versions publiées d'ADB Tools (dossier DiagApp).", DiagApp.ApkPath), Loc.T("OK"), "", false);
                return false;
            }
            if (_diagAppSerial == serial && _diagAppUsers.Count > 0) { _diagAppUsers.Add(user); return true; }

            // Déjà à jour : rien à installer (les résultats gardés sur le terminal sont conservés).
            if (await GetDiagAppVersionAsync(serial) < DiagApp.VersionCode && !await InstallDiagAppAsync(serial))
                return false;
            _diagAppSerial = serial;
            _diagAppUsers.Add(user);
            return true;
        }

        /// <summary>Installation ou mise à jour, puis droits de débogage. Faux (avec message) en cas d'échec.</summary>
        private async Task<bool> InstallDiagAppAsync(string serial)
        {
            if (!File.Exists(DiagApp.ApkPath))
            {
                await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                    Loc.F("L'appli de diagnostic est introuvable :\n{0}\n\nElle est fournie avec les versions publiées d'ADB Tools (dossier DiagApp).", DiagApp.ApkPath), Loc.T("OK"), "", false);
                return false;
            }
            string apk = AdbManager.QuoteLocalArg(DiagApp.ApkPath);
            var res = await _adbManager.ExecuteAsync($"-s {serial} install -r -t -g {apk}", TimeSpan.FromSeconds(90));
            if (!(res.StandardOutput ?? "").Contains("Success"))
            {
                // Ancienne version signée autrement (chaque compilation a sa clé) : on la retire d'abord —
                // sauf si elle garde une mesure Wi-Fi ou un suivi terrain pas encore récupéré.
                if (await HasPendingSurveyAsync(serial))
                {
                    await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                        Loc.T("Une mesure Wi-Fi sans câble est encore sur le terminal : récupérez-la d'abord (Analyse Wi-Fi ▸ Mesure sans câble ▸ Récupérer les mesures)."),
                        Loc.T("OK"), "", false);
                    return false;
                }
                if (await HasPendingRecordingAsync(serial))
                {
                    await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                        Loc.T("Un enregistrement terrain est encore sur le terminal : récupérez-le d'abord (Analyse de fichiers ▸ Suivi terrain ▸ Récupérer l'enregistrement)."),
                        Loc.T("OK"), "", false);
                    return false;
                }
                await _adbManager.ExecuteAsync($"-s {serial} uninstall {DiagApp.Package}");
                res = await _adbManager.ExecuteAsync($"-s {serial} install -t -g {apk}", TimeSpan.FromSeconds(90));
            }
            if (!(res.StandardOutput ?? "").Contains("Success"))
            {
                string detail = ((res.StandardError ?? "") + " " + (res.StandardOutput ?? "")).Trim();
                await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                    Loc.F("L'appli de diagnostic n'a pas pu être installée sur le terminal :\n{0}\n\nCertains terminaux gérés (MDM) interdisent l'installation d'applications par ADB.", PackageManagerService.DescribeInstallFailure(detail)),
                    Loc.T("OK"), "", false);
                return false;
            }
            string pkg = DiagApp.Package;
            // Journal complet et état du système (suivi terrain) : droits réservés au débogage, accordés ici par USB.
            await _adbManager.ExecuteAsync($"-s {serial} shell pm grant {pkg} android.permission.READ_LOGS");
            await _adbManager.ExecuteAsync($"-s {serial} shell pm grant {pkg} android.permission.DUMP");
            // Mesure Wi-Fi et suivi terrain écran éteint : pas de mise en veille profonde ni de restriction d'arrière-plan.
            await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys deviceidle whitelist +{pkg}");
            await _adbManager.ExecuteAsync($"-s {serial} shell cmd appops set {pkg} RUN_ANY_IN_BACKGROUND allow");
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📱 ADB Tools Diag installée sur {1}.", DateTime.Now, serial));
            if (CurrentSerial() == serial) _ = RefreshDiagAppButtonAsync();
            return true;
        }

        private Task SendDiagCommandAsync(string serial, string cmd) =>
            _adbManager.ExecuteAsync(DiagApp.CommandArgs(serial, cmd));

        /// <summary>Plus aucune fenêtre ne s'en sert. L'appli reste sur le terminal (utilisable seule).</summary>
        private Task ReleaseDiagAppAsync(string user)
        {
            _diagAppUsers.Remove(user);
            if (_diagAppUsers.Count == 0) _diagAppSerial = null;
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------ Bouton « Appli Diag » (barre du haut)

        private string CurrentSerial()
        {
            var d = DeviceComboBox.SelectedItem as AndroidDevice;
            return d != null && d.State == "device" ? d.Serial : null;
        }

        /// <summary>État du bouton selon le terminal sélectionné : à installer, à mettre à jour, à jour.</summary>
        private async Task RefreshDiagAppButtonAsync()
        {
            string serial = CurrentSerial();
            BtnDiagApp.IsEnabled = serial != null;
            BtnDiagApp.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            if (serial == null) { TxtDiagAppButton.Text = Loc.T("Appli Diag"); _diagAppInstalledVersion = -1; UpdateDiagWatch(null, false); return; }
            int v = await GetDiagAppVersionAsync(serial);
            if (CurrentSerial() != serial) return; // terminal changé entre-temps
            _diagAppInstalledVersion = v;
            UpdateDiagWatch(serial, v > 0);
            if (v == 0) TxtDiagAppButton.Text = Loc.T("Installer l'appli Diag");
            else if (v > 0 && v < DiagApp.VersionCode)
            {
                TxtDiagAppButton.Text = Loc.T("Mettre à jour l'appli Diag");
                BtnDiagApp.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "Action");
            }
            else TxtDiagAppButton.Text = Loc.T("Appli Diag ✓");
            if (v > 0)
            {
                // Mesure Wi-Fi ou suivi terrain fini sur le terminal, pas encore récupéré : on le signale (orange).
                int pending = (await HasPendingSurveyAsync(serial) ? 1 : 0) + (await HasPendingRecordingAsync(serial) ? 1 : 0);
                if (CurrentSerial() != serial) return;
                if (pending > 0)
                {
                    TxtDiagAppButton.Text = Loc.F("Appli Diag · {0} à récupérer", pending);
                    BtnDiagApp.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "Action");
                }
            }
        }

        private async void BtnDiagApp_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.Now - _diagAppPopupLastClosed).TotalMilliseconds < 200) return;
            string serial = CurrentSerial();
            if (serial == null) return;
            TxtDiagAppState.Text = Loc.T("Lecture de l'appli sur le terminal…");
            BtnDiagAppInstall.Visibility = Visibility.Collapsed;
            BtnDiagAppOpen.Visibility = Visibility.Collapsed;
            BtnDiagAppRemove.Visibility = Visibility.Collapsed;
            BtnDiagAppFetchSurvey.Visibility = Visibility.Collapsed;
            BtnDiagAppFetchRecord.Visibility = Visibility.Collapsed;
            BtnDiagAppFetchTests.Visibility = Visibility.Collapsed;
            DiagAppPopup.IsOpen = true;
            await RefreshDiagAppButtonAsync();
            int v = _diagAppInstalledVersion;
            bool installed = v > 0, outdated = v > 0 && v < DiagApp.VersionCode;
            TxtDiagAppState.Text = v < 0 ? Loc.T("Impossible de lire l'état de l'appli sur ce terminal.")
                : !installed ? Loc.T("L'appli n'est pas installée sur ce terminal.")
                : outdated ? Loc.T("Une version plus ancienne est installée : mettez-la à jour.")
                : Loc.T("L'appli est installée et à jour.");
            BtnDiagAppInstall.Content = installed ? Loc.T("Mettre à jour (réinstaller)") : Loc.T("Installer sur le terminal");
            BtnDiagAppInstall.Visibility = !installed || outdated ? Visibility.Visible : Visibility.Collapsed;
            BtnDiagAppOpen.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            BtnDiagAppRemove.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            if (installed)
            {
                // Tout ce que l'appli a produit se récupère ici, au même endroit.
                BtnDiagAppFetchSurvey.Visibility = await HasPendingSurveyAsync(serial) ? Visibility.Visible : Visibility.Collapsed;
                BtnDiagAppFetchRecord.Visibility = await HasPendingRecordingAsync(serial) ? Visibility.Visible : Visibility.Collapsed;
                BtnDiagAppFetchTests.Visibility = Visibility.Visible;
            }
        }

        private void BtnDiagAppFetchSurvey_Click(object sender, RoutedEventArgs e)
        {
            DiagAppPopup.IsOpen = false;
            FetchSurveyFromApp();
        }

        private void BtnDiagAppFetchRecord_Click(object sender, RoutedEventArgs e)
        {
            DiagAppPopup.IsOpen = false;
            FetchRecordingFromApp();
        }

        private async void BtnDiagAppFetchTests_Click(object sender, RoutedEventArgs e)
        {
            DiagAppPopup.IsOpen = false;
            string serial = CurrentSerial();
            if (serial == null) return;
            NavDiag_Click(null, null);
            await ShowDiagImportAsync(serial);
        }

        /// <summary>Mesure Wi-Fi sans câble : ouverte dans l'Analyse Wi-Fi (fenêtre de suivi de la copie).</summary>
        private void FetchSurveyFromApp()
        {
            NavRadar_Click(null, null);
            WifiSurveyOverlay.Visibility = Visibility.Visible;
            BtnSurveyFetch_Click(null, null);
        }

        /// <summary>Suivi terrain : copié puis ouvert dans l'Analyse de fichiers.</summary>
        private void FetchRecordingFromApp()
        {
            RecorderOverlay.Visibility = Visibility.Visible;
            BtnRecorderFetch_Click(null, null);
        }

        private void DiagAppPopup_Closed(object sender, EventArgs e) => _diagAppPopupLastClosed = DateTime.Now;

        private async void BtnDiagAppInstall_Click(object sender, RoutedEventArgs e)
        {
            string serial = CurrentSerial();
            if (serial == null) return;
            DiagAppPopup.IsOpen = false;
            BtnDiagApp.IsEnabled = false;
            TxtDiagAppButton.Text = Loc.T("Installation…");
            try
            {
                if (await InstallDiagAppAsync(serial))
                    await SendDiagCommandAsync(serial, "idle");   // l'appli s'ouvre sur le terminal
            }
            finally { await RefreshDiagAppButtonAsync(); }
        }

        private async void BtnDiagAppOpen_Click(object sender, RoutedEventArgs e)
        {
            string serial = CurrentSerial();
            DiagAppPopup.IsOpen = false;
            if (serial != null) await SendDiagCommandAsync(serial, "idle");
        }

        private async void BtnDiagAppRemove_Click(object sender, RoutedEventArgs e)
        {
            string serial = CurrentSerial();
            DiagAppPopup.IsOpen = false;
            if (serial == null) return;
            if (_diagAppUsers.Count > 0 && _diagAppSerial == serial)
            {
                await ShowCustomDialogAsync(Loc.T("📱 ADB Tools Diag"), Loc.T("L'appli est en cours d'utilisation par une fenêtre du programme (tests, lecteur, mesure Wi-Fi ou suivi terrain) : terminez-la d'abord."), Loc.T("OK"), "", false);
                return;
            }
            if (await HasPendingSurveyAsync(serial))
            {
                await ShowCustomDialogAsync(Loc.T("📱 ADB Tools Diag"), Loc.T("Une mesure Wi-Fi sans câble est encore sur le terminal : récupérez-la d'abord (Analyse Wi-Fi ▸ Mesure sans câble ▸ Récupérer les mesures)."), Loc.T("OK"), "", false);
                return;
            }
            if (await HasPendingRecordingAsync(serial))
            {
                await ShowCustomDialogAsync(Loc.T("📱 ADB Tools Diag"), Loc.T("Un enregistrement terrain est encore sur le terminal : récupérez-le d'abord (Analyse de fichiers ▸ Suivi terrain ▸ Récupérer l'enregistrement)."), Loc.T("OK"), "", false);
                return;
            }
            if (!await ShowCustomDialogAsync(Loc.T("📱 ADB Tools Diag"), Loc.T("Retirer l'appli de ce terminal ? Les résultats gardés dans l'appli seront effacés."), Loc.T("Retirer"), Loc.T("Annuler"), true))
                return;
            try
            {
                // Profil DataWedge retiré avant l'appli (sinon DataWedge garde un profil actif sans appli), puis
                // réglages d'énergie remis par défaut.
                await _adbManager.ExecuteAsync($"-s {serial} shell am broadcast -a com.symbol.datawedge.api.ACTION --es com.symbol.datawedge.api.DELETE_PROFILE {DiagApp.DataWedgeProfile}");
                await _adbManager.ExecuteAsync($"-s {serial} shell am force-stop {DiagApp.Package}");
                await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys deviceidle whitelist -{DiagApp.Package}");
                await _adbManager.ExecuteAsync($"-s {serial} uninstall {DiagApp.Package}");
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📱 ADB Tools Diag retirée de {1}.", DateTime.Now, serial));
            }
            catch { /* terminal débranché entre-temps */ }
            await RefreshDiagAppButtonAsync();
        }
    }
}
