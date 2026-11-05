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
        // MODULE PACKAGE MANAGER & SNIFFER
        // ==========================================
        private readonly PackageManagerService _packageManagerService;
        private readonly ForegroundAppService _foregroundAppService;

        private async Task LoadDevicePackagesAsync()
        {
            var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;
            if (selectedDevice == null || selectedDevice.State != "device")
            {
                _allPackages.Clear();
                if (PackagesDataGrid != null) PackagesDataGrid.ItemsSource = null;
                return;
            }

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Récupération de la liste complète des packages de {1}...", DateTime.Now, selectedDevice.Serial));

            try
            {
                _allPackages = await _packageManagerService.ListPackagesAsync(selectedDevice.Serial);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    ApplyPackageFilters(true); // True = silence pour le premier chargement
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[ERREUR PACKAGES] {0}", ex.Message)); });
            }
        }

        private void ApplyPackageFilters(bool silent = false)
        {
            var criteria = BuildPackageFilterCriteria(out string currentFilterName);
            var finalList = PackageFilter.Apply(_allPackages, criteria);

            if (PackagesDataGrid != null)
            {
                PackagesDataGrid.ItemsSource = finalList;
            }

            // Affiche dans la console si l'action est manuelle
            if (!silent && ConsoleOutput != null)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔍 Filtre appliqué : {1} -> {2} package(s) trouvé(s).", DateTime.Now, currentFilterName, finalList.Count));
                if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();
            }
        }

        // Capture l'état des contrôles UNE fois par appel (voir Core/PackageManagerService.cs pour la
        // logique de filtrage elle-même, pure et testable indépendamment de la UI).
        private PackageFilterCriteria BuildPackageFilterCriteria(out string filterName)
        {
            string query = TxtPackageSearch?.Text?.Trim() ?? "";
            string type = null;
            bool onlyDisabled = false;
            filterName = "Tous";

            if (RbFilterUser != null && RbFilterUser.IsChecked == true)
            {
                type = "Utilisateur";
                filterName = "Utilisateur";
            }
            else if (RbFilterSystem != null && RbFilterSystem.IsChecked == true)
            {
                type = "Système";
                filterName = "Système";
            }
            else if (RbFilterDisabled != null && RbFilterDisabled.IsChecked == true)
            {
                onlyDisabled = true;
                filterName = "Désactivés";
            }

            if (!string.IsNullOrEmpty(query))
            {
                filterName += $" (Recherche: '{query}')";
            }

            return new PackageFilterCriteria { TypeFilter = type, OnlyDisabled = onlyDisabled, SearchText = query };
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyPackageFilters(false);
        // Recherche appliquée 250 ms après la dernière frappe, sans ligne de console à chaque caractère
        // (auparavant chaque lettre ajoutait "Filtre appliqué..." dans la console).
        private DispatcherTimer _packageSearchDebounce;

        private void TxtPackageSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_packageSearchDebounce == null)
            {
                _packageSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _packageSearchDebounce.Tick += (s, ev) => { _packageSearchDebounce.Stop(); ApplyPackageFilters(true); };
            }
            _packageSearchDebounce.Stop();
            _packageSearchDebounce.Start();
        }
        private void BtnRefreshPackages_Click(object sender, RoutedEventArgs e) => _ = LoadDevicePackagesAsync();

        private AndroidPackageItem GetSelectedPackage() => PackagesDataGrid?.SelectedItem as AndroidPackageItem;

        private List<AndroidPackageItem> GetSelectedPackages() =>
            PackagesDataGrid?.SelectedItems?.Cast<AndroidPackageItem>().ToList() ?? new List<AndroidPackageItem>();

        private async void PackagesDataGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selected = GetSelectedPackages();

            // Le libellé reflète le statut du paquet unique sélectionné ; générique si sélection multiple
            // (l'action, elle, bascule quand même chaque paquet selon son propre statut, voir plus bas).
            if (BtnToggleEnabled != null)
            {
                BtnToggleEnabled.Content = selected.Count == 1
                    ? (selected[0].Status == "Désactivé" ? Loc.T("🔌 Activer") : Loc.T("🔌 Désactiver"))
                    : Loc.T("🔌 Activer/Désactiver");
            }

            await UpdatePackageDetailsPanelAsync(selected);
        }

        /// <summary>Détails à la demande pour UN paquet sélectionné (un appel dumpsys par paquet serait trop lent pour toute la liste).</summary>
        private async Task UpdatePackageDetailsPanelAsync(List<AndroidPackageItem> selected)
        {
            if (TxtPkgDetails == null) return;

            if (selected.Count != 1)
            {
                TxtPkgDetails.Text = Loc.T("Sélectionnez un seul paquet pour voir ses détails.");
                return;
            }

            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                TxtPkgDetails.Text = Loc.T("Aucun terminal connecté.");
                return;
            }

            var pkg = selected[0];
            TxtPkgDetails.Text = Loc.T("Chargement...");
            var details = await _packageManagerService.GetPackageDetailsAsync(dev.Serial, pkg.PackageName);

            // Le paquet sélectionné a pu changer pendant l'attente de la réponse ADB : on ignore le résultat
            // s'il ne correspond plus à la sélection courante.
            if (GetSelectedPackages().Count != 1 || GetSelectedPackages()[0].PackageName != pkg.PackageName) return;

            TxtPkgDetails.Text = details == null
                ? Loc.T("Détails indisponibles.")
                : Loc.F("Version : {0} (code {1})\nSDK cible : {2}\nInstallé via : {3}", details.VersionName ?? Loc.T("Inconnue"), details.VersionCode ?? "?", details.TargetSdk ?? Loc.T("Inconnu"), details.InstallerPackage ?? Loc.T("Inconnu"));
        }

        private async void PkgAction_Launch_Click(object sender, RoutedEventArgs e)
        {
            var pkg = GetSelectedPackage();
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (pkg == null || dev == null || dev.State != "device") return;

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🚀 Lancement de {1}...", DateTime.Now, pkg.PackageName));
            try
            {
                ReportAdbActionResult("Lancement", pkg.PackageName, await _packageManagerService.LaunchAsync(dev.Serial, pkg.PackageName));
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur au lancement : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void PkgAction_Stop_Click(object sender, RoutedEventArgs e)
        {
            var packages = GetSelectedPackages();
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (packages.Count == 0 || dev == null || dev.State != "device") return;

            try
            {
                foreach (var pkg in packages)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🛑 Arrêt forcé de {1}...", DateTime.Now, pkg.PackageName));
                    ReportAdbActionResult("Arrêt forcé", pkg.PackageName, await _packageManagerService.ForceStopAsync(dev.Serial, pkg.PackageName));
                }
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur à l'arrêt forcé : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void PkgAction_Clear_Click(object sender, RoutedEventArgs e)
        {
            var packages = GetSelectedPackages();
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (packages.Count == 0 || dev == null || dev.State != "device") return;

            bool isConfirmed = await ShowCustomDialogAsync(
                Loc.T("Vider Cache / Données"),
                packages.Count == 1
                    ? Loc.F("Voulez-vous vraiment effacer toutes les données de l'application suivante ?\n\n{0}", packages[0].PackageName)
                    : Loc.F("Voulez-vous vraiment effacer toutes les données de ces {0} applications ?\n\n{1}", packages.Count, string.Join("\n", packages.Select(p => p.PackageName))),
                Loc.T("Vider"),
                Loc.T("Annuler"),
                true
            );
            if (!isConfirmed) return;

            try
            {
                foreach (var pkg in packages)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🧹 Vidage du cache et des données de {1}...", DateTime.Now, pkg.PackageName));
                    ReportAdbActionResult("Vidage des données", pkg.PackageName, await _packageManagerService.ClearDataAsync(dev.Serial, pkg.PackageName));
                }
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur au vidage : {1}", DateTime.Now, ex.Message));
            }
        }

        /// <summary>Bascule chaque paquet sélectionné selon SON PROPRE statut (utile même si la sélection mélange actifs et désactivés).</summary>
        private async void PkgAction_ToggleEnabled_Click(object sender, RoutedEventArgs e)
        {
            var packages = GetSelectedPackages();
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (packages.Count == 0 || dev == null || dev.State != "device") return;

            try
            {
                foreach (var pkg in packages)
                {
                    bool isDisabled = pkg.Status == "Désactivé";
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🔌 {(isDisabled ? "Activation" : "Désactivation")} de {pkg.PackageName}...");
                    if (isDisabled) ReportAdbActionResult("Activation", pkg.PackageName, await _packageManagerService.EnableAsync(dev.Serial, pkg.PackageName));
                    else ReportAdbActionResult("Désactivation", pkg.PackageName, await _packageManagerService.DisableAsync(dev.Serial, pkg.PackageName));
                }

                _ = LoadDevicePackagesAsync();
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur activer/désactiver : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void PkgAction_Uninstall_Click(object sender, RoutedEventArgs e)
        {
            var packages = GetSelectedPackages();
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (packages.Count == 0 || dev == null || dev.State != "device") return;

            bool isConfirmed = await ShowCustomDialogAsync(
                Loc.T("Désinstallation"),
                packages.Count == 1
                    ? Loc.F("Voulez-vous vraiment désinstaller l'application suivante du terminal ?\n\n{0}", packages[0].PackageName)
                    : Loc.F("Voulez-vous vraiment désinstaller ces {0} applications du terminal ?\n\n{1}", packages.Count, string.Join("\n", packages.Select(p => p.PackageName))),
                Loc.T("Désinstaller"),
                Loc.T("Annuler"),
                true // true = bouton rouge
            );

            if (isConfirmed)
            {
                try
                {
                    foreach (var pkg in packages)
                    {
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🗑️ Désinstallation de {1}...", DateTime.Now, pkg.PackageName));
                        ReportAdbActionResult("Désinstallation", pkg.PackageName, await _packageManagerService.UninstallAsync(dev.Serial, pkg.PackageName));
                    }
                    _ = LoadDevicePackagesAsync();
                }
                catch (Exception ex)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur à la désinstallation : {1}", DateTime.Now, ex.Message));
                }
            }
        }

        private async void PkgAction_Pull_Click(object sender, RoutedEventArgs e)
        {
            var pkg = GetSelectedPackage();
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (pkg == null || dev == null || dev.State != "device") return;

            try
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📥 Récupération du chemin APK pour {1}...", DateTime.Now, pkg.PackageName));
                List<string> remotePaths = await _packageManagerService.GetApkPathsAsync(dev.Serial, pkg.PackageName);

                if (remotePaths.Count == 0)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Chemin APK introuvable pour {1}.", DateTime.Now, pkg.PackageName));
                    return;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = $"{pkg.PackageName}.apk",
                    DefaultExt = ".apk",
                    Filter = "APK files (*.apk)|*.apk"
                };
                if (dlg.ShowDialog() != true) return;

                // 1. APK principal (base.apk, toujours en tête de liste) vers le fichier choisi.
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📥 Téléchargement de l'APK vers {1}...", DateTime.Now, dlg.FileName));
                var baseRes = await _packageManagerService.PullApkAsync(dev.Serial, remotePaths[0], dlg.FileName);
                if (!baseRes.IsSuccess || !System.IO.File.Exists(dlg.FileName))
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Échec de l'extraction : {1}", DateTime.Now, (baseRes.StandardError ?? baseRes.StandardOutput)?.Trim()));
                    return;
                }

                if (remotePaths.Count == 1)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ APK extrait avec succès sur le PC !", DateTime.Now));
                    return;
                }

                // 2. App "split APK" : base.apk seul ne s'installe pas. Les parties complémentaires sont
                // récupérées dans un dossier voisin, pour une réinstallation via "adb install-multiple".
                string splitFolder = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(dlg.FileName),
                    System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) + "_splits");
                System.IO.Directory.CreateDirectory(splitFolder);

                int failed = 0;
                foreach (string splitPath in remotePaths.Skip(1))
                {
                    string localSplit = System.IO.Path.Combine(splitFolder, System.IO.Path.GetFileName(splitPath));
                    var splitRes = await _packageManagerService.PullApkAsync(dev.Serial, splitPath, localSplit);
                    if (!splitRes.IsSuccess) failed++;
                }

                AppendConsole(failed == 0
                    ? Loc.F("\n[{0:HH:mm:ss}] ✅ APK extrait (app en {1} parties) : base dans {2}, {3} partie(s) complémentaire(s) dans {4}. Réinstallation : adb install-multiple avec tous ces fichiers.", DateTime.Now, remotePaths.Count, dlg.FileName, remotePaths.Count - 1, splitFolder)
                    : Loc.F("\n[{0:HH:mm:ss}] ⚠️ APK principal extrait, mais {1} partie(s) complémentaire(s) sur {2} n'ont pas pu être récupérées ({3}).", DateTime.Now, failed, remotePaths.Count - 1, splitFolder));
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur extraction APK : {1}", DateTime.Now, ex.Message));
            }
        }

        /// <summary>
        /// Signale en console l'échec d'une action adb (paquet ou fichier). Auparavant ces résultats étaient
        /// ignorés : "pm clear", "uninstall", "rm"... pouvaient échouer sans que le technicien le sache.
        /// Rien n'est affiché en cas de succès (la ligne "en cours" suffit).
        /// </summary>
        private void ReportAdbActionResult(string action, string target, AdbCommandResult res)
        {
            string output = (res.StandardOutput ?? "").Trim();
            string error = (res.StandardError ?? "").Trim();

            bool failed = !res.IsSuccess
                || output.StartsWith("Failure", StringComparison.OrdinalIgnoreCase)
                || output.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                || output.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("monkey aborted", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("Read-only file system", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!failed) return;

            string detail = !string.IsNullOrEmpty(error) ? error : (!string.IsNullOrEmpty(output) ? output : $"code {res.ExitCode}");
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ {1} impossible ({2}) : {3}", DateTime.Now, action, target, detail));
        }

        private void DropZone_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Handled = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
        }

        private async void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                var dev = DeviceComboBox.SelectedItem as AndroidDevice;
                if (dev == null || dev.State != "device") return;

                var installSets = BuildApkInstallSets(files);
                if (installSets.Count == 0)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Aucun fichier .apk trouvé dans ce qui a été déposé.", DateTime.Now));
                    return;
                }

                foreach (var set in installSets)
                {
                    string label = set.Count > 1
                        ? $"{System.IO.Path.GetFileName(set[0])} + {set.Count - 1} partie(s) complémentaire(s)"
                        : System.IO.Path.GetFileName(set[0]);

                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Installation de {1} en cours (patientez)...", DateTime.Now, label));
                    if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll1) scroll1.ScrollToEnd();

                    try
                    {
                        // Flags : -r (remplacer), -d (rétrogradation autorisée), -t (paquets de test autorisés).
                        // Plusieurs fichiers → "install-multiple" (app en plusieurs parties).
                        var res = await _packageManagerService.InstallApkSetAsync(dev.Serial, set);
                        var (ok, message) = InterpretInstallResult(res);
                        AppendConsole(ok
                            ? Loc.F("\n[{0:HH:mm:ss}] ✅ Succès : application installée.", DateTime.Now)
                            : Loc.F("\n[{0:HH:mm:ss}] ❌ Échec de l'installation : {1}", DateTime.Now, message));

                        if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll2) scroll2.ScrollToEnd();

                        // 🔄 Attendu (pas de fire-and-forget) : plusieurs installations successives ne doivent pas
                        // lancer des rafraîchissements qui se chevauchent sur _allPackages.
                        await LoadDevicePackagesAsync();
                    }
                    catch (Exception ex)
                    {
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur d'installation ({1}) : {2}", DateTime.Now, label, ex.Message));
                    }
                }
            }
        }

        /// <summary>
        /// Regroupe les éléments déposés en lots d'installation :
        /// - dossier → un lot avec tous ses .apk ;
        /// - X.apk avec un dossier voisin X_splits → un lot (extraction faite par l'app) ;
        /// - plusieurs .apk d'un même dossier dont au moins un "split_*.apk" → un seul lot (app en plusieurs parties) ;
        /// - sinon un lot par .apk.
        /// Un fichier déjà inclus dans un lot n'est pas réinstallé une seconde fois.
        /// </summary>
        private static List<List<string>> BuildApkInstallSets(IEnumerable<string> droppedPaths)
        {
            var sets = new List<List<string>>();
            var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = (droppedPaths ?? Enumerable.Empty<string>()).ToList();

            // Fichiers .apk "en vrac" appartenant à une app en plusieurs parties, regroupés par dossier.
            var looseSplitGroups = paths
                .Where(p => System.IO.File.Exists(p) && p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => System.IO.Path.GetDirectoryName(p), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1 && g.Any(p => System.IO.Path.GetFileName(p).StartsWith("split_", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var group in looseSplitGroups)
            {
                var set = group
                    .OrderBy(p => System.IO.Path.GetFileName(p).StartsWith("split_", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var p in set) alreadyIncluded.Add(p);
                sets.Add(set);
            }

            // Les fichiers avant les dossiers : si l'utilisateur dépose X.apk ET X_splits, X.apk embarque déjà
            // les parties de X_splits, et le dossier, traité ensuite, ne contient plus rien de nouveau.
            foreach (string path in paths.OrderBy(p => System.IO.Directory.Exists(p) ? 1 : 0))
            {
                if (alreadyIncluded.Contains(path)) continue;

                var set = PackageManagerService.ResolveApkSet(path).Where(p => !alreadyIncluded.Contains(p)).ToList();
                if (set.Count == 0) continue;

                foreach (var p in set) alreadyIncluded.Add(p);
                sets.Add(set);
            }

            return sets;
        }

        /// <summary>Analyse le retour d'"adb install(-multiple)" : succès, ou message d'échec avec explication si le code est connu.</summary>
        private static (bool Success, string Message) InterpretInstallResult(AdbCommandResult res)
        {
            string outText = res.StandardOutput?.Trim() ?? "";
            string errText = res.StandardError?.Trim() ?? "";

            if (res.IsSuccess && outText.Contains("Success")) return (true, "application installée avec succès.");

            string raw = !string.IsNullOrEmpty(errText) ? errText : outText;
            string hint = PackageManagerService.DescribeInstallFailure(outText + "\n" + errText);
            return (false, hint != null ? $"{raw}\n   👉 {hint}" : raw);
        }

        // --- INSTALLATION APK SUR PLUSIEURS APPAREILS (réutilise le sélecteur multi-appareils générique) ---
        private async void BtnInstallApkMulti_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Fichiers APK (*.apk)|*.apk",
                Title = Loc.T("Choisir l'APK à installer sur plusieurs appareils")
            };
            if (dlg.ShowDialog() != true) return;

            string apkPath = dlg.FileName;
            // Une app extraite en plusieurs parties (dossier "<nom>_splits" à côté) est installée en entier.
            List<string> apkSet = PackageManagerService.ResolveApkSet(apkPath);
            string apkFileName = apkSet.Count > 1
                ? $"{System.IO.Path.GetFileName(apkPath)} + {apkSet.Count - 1} partie(s) complémentaire(s)"
                : System.IO.Path.GetFileName(apkPath);
            string selectedSerialAtOpen = (DeviceComboBox.SelectedItem as AndroidDevice)?.Serial;

            _multiDeviceAction = async (dev) =>
            {
                var res = await _packageManagerService.InstallApkSetAsync(dev.Serial, apkSet);
                return InterpretInstallResult(res);
            };
            _multiDeviceConfirmBody = $"Le fichier suivant va être installé (remplace une version déjà présente le cas échéant) :\n\n{apkFileName}";
            _multiDeviceConfirmIsDanger = false;

            // Rafraîchit le tableau une fois l'installation terminée, uniquement si le terminal actuellement
            // affiché faisait partie du lot — pas de rafraîchissement incohérent pour les autres terminaux.
            _multiDeviceOnComplete = (targets) =>
            {
                if (selectedSerialAtOpen != null && targets.Any(d => d.Serial == selectedSerialAtOpen))
                    _ = LoadDevicePackagesAsync();
            };

            await OpenMultiDeviceDialogAsync("📦 Installer un APK sur plusieurs appareils", "Fichier APK", apkFileName, fieldReadOnly: true);
        }

        private void BtnToggleSniffer_Click(object sender, RoutedEventArgs e)
        {
            _isSnifferActive = BtnToggleSniffer.IsChecked == true;
            if (_isSnifferActive)
            {
                BtnToggleSniffer.Content = Loc.T("Désactiver Sniffer");
                StartPackageSniffer();

                // 🚀 LANCE LA PULSATION SUR L'ONGLET "PACKAGE MANAGER"
                AnimateNavButton(BtnNavPackages, true);
            }
            else
            {
                BtnToggleSniffer.Content = Loc.T("Activer le Sniffer");
                _snifferTimer?.Stop();
                TxtForegroundPackage.Text = Loc.T("Sniffer désactivé.");

                // 🛑 ARRÊTE LA PULSATION
                AnimateNavButton(BtnNavPackages, false);
            }
        }

        private bool _isSnifferPolling;

        private void StartPackageSniffer()
        {
            _snifferTimer?.Stop(); // Jamais deux timers de sniffer en parallèle
            _snifferTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _snifferTimer.Tick += async (s, args) =>
            {
                // Une détection peut enchaîner jusqu'à 3 "dumpsys" (plusieurs secondes sur un terminal lent) :
                // sans ce verrou, les ticks de 2 s s'empilaient et multipliaient les appels adb.
                if (_isSnifferPolling) return;

                var dev = DeviceComboBox.SelectedItem as AndroidDevice;
                if (dev != null && dev.State == "device")
                {
                    _isSnifferPolling = true;
                    try
                    {
                        // 🚀 On utilise notre nouvelle méthode qui ramène le Package + l'Activité !
                        string foregroundComponent = await _foregroundAppService.GetForegroundComponentAsync(dev.Serial);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (!string.IsNullOrEmpty(foregroundComponent))
                            {
                                TxtForegroundPackage.Text = foregroundComponent;
                            }
                            else
                            {
                                TxtForegroundPackage.Text = Loc.T("Écran Système / Verrouillé");
                            }
                        });
                    }
                    catch
                    {
                        // Silencieux si le câble est débranché pendant le tick
                    }
                    finally
                    {
                        _isSnifferPolling = false;
                    }
                }
            };
            _snifferTimer.Start();
        }

        private void BtnClearConsole_Click(object sender, RoutedEventArgs e) { ClearConsole();
        }

        private void BtnCopyConsole_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(ConsoleOutput.Text);
        }
    }
}
