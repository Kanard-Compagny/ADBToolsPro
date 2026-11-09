﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ADBToolsNew.Core;

namespace ADBToolsNew
{
    /// <summary>
    /// Page « Mise à jour OS » : un ou plusieurs terminaux de toutes marques mis à jour en même temps, chacun avec sa
    /// progression. Trois méthodes :
    /// - par câble (« adb sideload ») : la plus courante (Zebra, Honeywell, Datalogic, Newland, Urovo…). Passage
    ///   direct en mode sideload (« reboot sideload-auto-reboot ») ; pour un retour arrière, le technicien choisit
    ///   « Downgrade from ADB » dans le Recovery (le mode automatique refuse une version plus ancienne) ;
    /// - copie dans la mémoire du terminal puis Recovery (« Apply update from SD card / internal storage ») : pour
    ///   les modèles qui refusent le sideload ;
    /// - fastboot (« fastboot update » d'une image d'usine) : terminaux au bootloader déverrouillé seulement.
    /// Paquet unique, ou bibliothèque (dossier) où ADB Tools choisit le paquet le plus récent prévu pour chaque modèle.
    /// Vérifications avant l'envoi (modèle, version, batterie, bootloader), envois simultanés limitables, contrôle de la
    /// version au retour, puis actions facultatives (APK, commandes ADB) et bilan Excel.
    /// Un envoi commencé n'est jamais interrompu (risque de terminal inutilisable) ; seules les attentes s'arrêtent.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class MassOsRow
        {
            public string Serial = "";
            public string State = "";
            public string Model = "";
            public List<string> DeviceIds = new List<string>();
            public string Fingerprint = "";
            public string DisplayId = "";
            public int Battery = -1;
            public long BuildUtc;
            public bool? FlashLocked;       // bootloader verrouillé (true), déverrouillé (false), inconnu (null)
            public string Bootloader = "";  // version du bootloader
            public OsUpdatePackage Package; // paquet retenu pour ce terminal
            public bool Wipe;
            public bool Downgrade;          // paquet plus ancien que le terminal (paquet de « downgrade » du fabricant)
            public bool Selectable;
            public string Outcome = "";     // ok, warn, bad ou vide (pas lancé)
            public string Message = "";
            public string VersionBefore = "";
            public string Method = "";
            public DateTime? Started, Ended;
            public CheckBox Check;
            public TextBlock TxtVersion;
            public TextBlock TxtBattery;
            public TextBlock TxtStatus;
            public ProgressBar Bar;
        }

        private const int MassOsMinBattery = 30;
        private const int OsUpdateTabIndex = 12;
        private readonly List<MassOsRow> _massOsRows = new List<MassOsRow>();
        private OsUpdatePackage _massOsPackage;
        private List<OsUpdatePackage> _massOsLibrary = new List<OsUpdatePackage>();
        private readonly List<string> _massOsApks = new List<string>();
        private CancellationTokenSource _massOsWaitCts;
        private SemaphoreSlim _massOsSlots;
        private bool _massOsBusy;
        private bool _massOsInit;

        private string FastbootExePath => Path.Combine(Path.GetDirectoryName(_adbExePath) ?? "", "fastboot.exe");

        // ------------------------------------------------------------------ Méthode et source du paquet

        private const string MassOsIntroSideload = "Tous les terminaux cochés reçoivent le même paquet, en même temps. ADB Tools redémarre chacun en mode « mise à jour par ADB » ; si un modèle ne le permet pas, choisissez « Apply update from ADB » dans son menu Recovery et ADB Tools continue tout seul. Un terminal pour lequel le paquet n'est pas prévu est écarté. Ne débranchez aucun terminal pendant l'envoi.";
        private const string MassOsIntroCopy = "Le paquet est copié dans la mémoire de chaque terminal coché, puis le terminal redémarre en Recovery : choisissez « Apply update from SD card » (ou « internal storage ») puis le fichier « update_… ». ADB Tools attend son retour pour vérifier la version, puis efface la copie. Pour les modèles qui refusent le sideload.";
        private const string MassOsIntroFastboot = "Chaque terminal redémarre en mode bootloader et reçoit l'image d'usine par « fastboot update », qui vérifie elle-même le modèle et le bootloader. ADB Tools contrôle avant tout que le bootloader est déverrouillé : sinon rien n'est flashé.";

        private string MassOsMethod => RbMassOsFastboot.IsChecked == true ? "fastboot" : RbMassOsCopy.IsChecked == true ? "copy" : "sideload";
        private bool MassOsCopyMode => MassOsMethod == "copy";
        private bool MassOsLibraryMode => RbMassOsLibrary.IsChecked == true;

        private void MassOsMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (TxtMassOsIntro == null || BdMassOsFastbootWarn == null || ChkMassOsWipe == null) return;
            string m = MassOsMethod;
            TxtMassOsIntro.Text = Loc.T(m == "fastboot" ? MassOsIntroFastboot : m == "copy" ? MassOsIntroCopy : MassOsIntroSideload);
            BdMassOsFastbootWarn.Visibility = m == "fastboot" ? Visibility.Visible : Visibility.Collapsed;
            ChkMassOsWipe.Visibility = m == "fastboot" ? Visibility.Visible : Visibility.Collapsed;
            if (m != "fastboot") ChkMassOsWipe.IsChecked = false;
            if (_massOsInit && !_massOsBusy) _ = LoadMassOsDevicesAsync(); // les terminaux en mode fastboot n'apparaissent qu'avec cette méthode
        }

        private void MassOsSource_Checked(object sender, RoutedEventArgs e)
        {
            if (TxtMassOsFile == null || BtnMassOsPick == null) return;
            if (MassOsLibraryMode)
            {
                BtnMassOsPick.Content = Loc.T("📂 Choisir le dossier…");
                TxtMassOsFile.Tag = Loc.T("Dossier de paquets (.zip), sous-dossiers compris");
                TxtMassOsFile.Text = AppSettings.Current.OsPackageLibraryFolder ?? "";
                if (TxtMassOsFile.Text.Length > 0) _ = ScanMassOsLibraryAsync(TxtMassOsFile.Text);
                else TxtMassOsPackage.Text = Loc.T("Choisissez le dossier où sont rangés les paquets (le vôtre ou le dossier partagé de l'équipe).");
            }
            else
            {
                BtnMassOsPick.Content = Loc.T("📂 Choisir le paquet…");
                TxtMassOsFile.Tag = Loc.T("Paquet de mise à jour (.zip)");
                TxtMassOsFile.Text = _massOsPackage?.FilePath ?? "";
                TxtMassOsPackage.Text = _massOsPackage != null ? Path.GetFileName(_massOsPackage.FilePath) + " — " + _massOsPackage.Describe() : Loc.T("Aucun paquet choisi.");
            }
            RefreshMassOsFit();
        }

        private void CmbMassOsParallel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_massOsInit) return;
            AppSettings.Current.OsMaxParallel = MassOsParallel();
            AppSettings.Save();
        }

        private int MassOsParallel() =>
            (CmbMassOsParallel?.SelectedItem as ComboBoxItem)?.Tag is string t && int.TryParse(t, out int n) ? n : 0;

        // ------------------------------------------------------------------ Page

        private async void NavOsUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl == null) return;
            MainTabControl.SelectedIndex = OsUpdateTabIndex;
            UpdateNavButtonsUI(BtnNavOsUpdate);
            if (!_massOsInit)
            {
                _massOsInit = true;
                ChkMassOsBattery.Checked += (s, a) => RefreshMassOsFit();
                ChkMassOsBattery.Unchecked += (s, a) => RefreshMassOsFit();
                ChkMassOsWipe.Checked += (s, a) => RefreshMassOsFit();
                ChkMassOsWipe.Unchecked += (s, a) => RefreshMassOsFit();
                int par = AppSettings.Current.OsMaxParallel;
                foreach (ComboBoxItem it in CmbMassOsParallel.Items)
                    if ((it.Tag as string) == par.ToString()) { CmbMassOsParallel.SelectedItem = it; break; }
                if (MassOsLibraryMode) MassOsSource_Checked(null, null);
            }
            // Pendant une mise à jour, la page reste telle quelle (les lignes suivent les envois en cours) ;
            // après une mise à jour, le bilan reste affiché jusqu'à « Actualiser ».
            if (!_massOsBusy && !_massOsRows.Any(r => r.Outcome.Length > 0)) await LoadMassOsDevicesAsync();
        }

        private async void BtnMassOsRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (MassOsLibraryMode && TxtMassOsFile.Text.Length > 0) await ScanMassOsLibraryAsync(TxtMassOsFile.Text);
            await LoadMassOsDevicesAsync();
        }

        private void BtnMassOsAll_Click(object sender, RoutedEventArgs e)
        {
            bool allChecked = _massOsRows.Where(r => r.Selectable).All(r => r.Check.IsChecked == true);
            foreach (var r in _massOsRows.Where(r => r.Selectable)) r.Check.IsChecked = !allChecked;
        }

        private void BtnMassOsStop_Click(object sender, RoutedEventArgs e)
        {
            _massOsWaitCts?.Cancel();
            BtnMassOsStop.IsEnabled = false;
        }

        // ------------------------------------------------------------------ Paquet ou bibliothèque

        private async void BtnMassOsPick_Click(object sender, RoutedEventArgs e)
        {
            if (MassOsLibraryMode)
            {
                string folder = FolderPicker.Pick(this, Loc.T("Dossier des paquets de mise à jour OS"), TxtMassOsFile.Text.Length > 0 ? TxtMassOsFile.Text : null);
                if (string.IsNullOrEmpty(folder)) return;
                TxtMassOsFile.Text = folder;
                AppSettings.Current.OsPackageLibraryFolder = folder;
                AppSettings.Save();
                await ScanMassOsLibraryAsync(folder);
                RefreshMassOsFit();
                return;
            }
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Loc.T("Paquets de mise à jour OS (*.zip)|*.zip"),
                Title = Loc.T("Choisissez le paquet de mise à jour OS")
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _massOsPackage = OsUpdatePackage.Read(dlg.FileName);
                TxtMassOsFile.Text = dlg.FileName;
                TxtMassOsPackage.Text = Path.GetFileName(dlg.FileName) + " — " + _massOsPackage.Describe();
                // Image fastboot choisie avec une autre méthode (ou l'inverse) : on bascule sur la bonne méthode.
                if (_massOsPackage.IsFastbootImage && MassOsMethod != "fastboot") RbMassOsFastboot.IsChecked = true;
                else if (_massOsPackage.IsOtaPackage && MassOsMethod == "fastboot") RbMassOsSideload.IsChecked = true;
            }
            catch (Exception ex)
            {
                _massOsPackage = null;
                TxtMassOsFile.Text = "";
                TxtMassOsPackage.Text = Loc.F("Ce fichier n'est pas une archive .zip lisible : {0}", ex.Message);
            }
            RefreshMassOsFit();
        }

        /// <summary>Lit tous les paquets .zip du dossier (sous-dossiers compris), sans bloquer l'interface.</summary>
        private async Task ScanMassOsLibraryAsync(string folder)
        {
            TxtMassOsPackage.Text = Loc.T("Lecture des paquets du dossier…");
            int unreadable = 0;
            var found = await Task.Run(() =>
            {
                var list = new List<OsUpdatePackage>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(folder, "*.zip", SearchOption.AllDirectories).Take(1000))
                    {
                        try { list.Add(OsUpdatePackage.Read(f)); }
                        catch { Interlocked.Increment(ref unreadable); }
                    }
                }
                catch { /* dossier inaccessible : liste vide */ }
                return list;
            });
            _massOsLibrary = found;
            int ota = found.Count(p => p.IsOtaPackage), fb = found.Count(p => p.IsFastbootImage);
            int other = found.Count - ota - fb + unreadable;
            TxtMassOsPackage.Text = found.Count == 0
                ? Loc.T("Aucun paquet .zip reconnu dans ce dossier.")
                : Loc.F("{0} paquet(s) de mise à jour et {1} image(s) fastboot trouvés", ota, fb)
                  + (other > 0 ? Loc.F(" ; {0} fichier(s) .zip sans description ignorés (choisissez-les avec « Un paquet pour tous »)", other) : ".");
            RefreshMassOsFit();
        }

        private void BtnMassOsApks_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Loc.T("Applications Android (*.apk)|*.apk"),
                Title = Loc.T("Applications à installer après la mise à jour"),
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var f in dlg.FileNames)
                if (!_massOsApks.Contains(f, StringComparer.OrdinalIgnoreCase)) _massOsApks.Add(f);
            RenderMassOsApks();
            ChkMassOsAfterApk.IsChecked = _massOsApks.Count > 0;
        }

        /// <summary>Une étiquette par application choisie, avec ✕ pour la retirer : lisible même avec beaucoup d'APK.</summary>
        private void RenderMassOsApks()
        {
            PnlMassOsApks.Children.Clear();
            TxtMassOsApks.Visibility = _massOsApks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var apk in _massOsApks.ToList())
            {
                var chip = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(10, 3, 4, 3), Margin = new Thickness(0, 0, 6, 6), ToolTip = apk };
                chip.SetResourceReference(Border.BackgroundProperty, "Surface2");
                chip.SetResourceReference(Border.BorderBrushProperty, "Line");
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var name = new TextBlock { Text = "📦 " + Path.GetFileName(apk), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                var remove = new Button { Content = "✕", Width = 20, Height = 20, FontSize = 10, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0), ToolTip = Loc.T("Retirer"), Style = (Style)FindResource("SidebarToggleStyle") };
                string path = apk;
                remove.Click += (s, a) => { _massOsApks.Remove(path); RenderMassOsApks(); if (_massOsApks.Count == 0) ChkMassOsAfterApk.IsChecked = false; };
                sp.Children.Add(name); sp.Children.Add(remove);
                chip.Child = sp;
                PnlMassOsApks.Children.Add(chip);
            }
        }

        // ------------------------------------------------------------------ Liste des terminaux

        private async Task LoadMassOsDevicesAsync()
        {
            BtnMassOsRefresh.IsEnabled = false;
            MassOsList.Children.Clear();
            _massOsRows.Clear();
            TxtMassOsSummary.Text = Loc.T("Recherche des terminaux…");
            try
            {
                var res = await _adbManager.ExecuteAsync("devices");
                var devices = res.IsSuccess ? ParseAdbDevices(res.StandardOutput) : new List<AndroidDevice>();
                var rows = devices.Select(d => new MassOsRow { Serial = d.Serial, State = d.State }).ToList();
                // Terminaux déjà en mode bootloader : visibles seulement par fastboot.
                if (File.Exists(FastbootExePath))
                {
                    string fbOut = await RunFastbootAsync("devices", 10);
                    foreach (var serial in OsUpdatePackage.ParseFastbootDevices(fbOut).Where(s => rows.All(r => r.Serial != s)))
                        rows.Add(new MassOsRow { Serial = serial, State = "fastboot" });
                }
                // Lecture des infos de chaque terminal en parallèle : avec 10 terminaux, 10 fois plus vite.
                await Task.WhenAll(rows.Select(r => r.State == "device" ? ReadMassOsInfoAsync(r) : r.State == "fastboot" ? ReadMassOsFastbootInfoAsync(r) : Task.CompletedTask));
                foreach (var r in rows) { BuildMassOsRow(r); _massOsRows.Add(r); }
                if (rows.Count == 0)
                    MassOsList.Children.Add(new TextBlock { Text = Loc.T("Aucun terminal détecté. Branchez les terminaux en USB, débogage USB autorisé, puis « Actualiser »."), FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10), Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
            }
            catch (Exception ex)
            {
                TxtMassOsSummary.Text = Loc.F("Impossible de lister les terminaux : {0}", ex.Message);
            }
            finally { BtnMassOsRefresh.IsEnabled = !_massOsBusy; }
            RefreshMassOsFit();
        }

        private async Task ReadMassOsInfoAsync(MassOsRow row)
        {
            try
            {
                // Toutes les propriétés en un appel : le nom visé par le paquet varie selon le constructeur.
                var s = await _adbManager.RunShellSectionsAsync(row.Serial, TimeSpan.FromSeconds(15), "getprop", "dumpsys battery");
                var props = AdbManager.ParseGetprop(s[0]);
                string P(string k) => props.TryGetValue(k, out var v) ? v : "";
                row.Model = P("ro.product.model"); row.Fingerprint = P("ro.build.fingerprint"); row.DisplayId = P("ro.build.display.id");
                row.DeviceIds = OsUpdatePackage.DeviceIdsFrom(props);
                long.TryParse(P("ro.build.date.utc"), out row.BuildUtc);
                row.Bootloader = P("ro.bootloader");
                string locked = P("ro.boot.flash.locked"), vb = P("ro.boot.verifiedbootstate");
                row.FlashLocked = locked == "1" ? true : locked == "0" ? false : vb == "orange" ? false : vb == "green" ? true : (bool?)null;
                row.Battery = OsUpdatePackage.ParseBatteryLevel(s[1]);
            }
            catch { /* terminal qui ne répond pas : il reste listé, sans détails */ }
        }

        private async Task ReadMassOsFastbootInfoAsync(MassOsRow row)
        {
            var vars = OsUpdatePackage.ParseFastbootVars(await RunFastbootAsync($"-s {row.Serial} getvar all", 20));
            string V(string k) => vars.TryGetValue(k, out var v) ? v : "";
            row.Model = V("product");
            row.DeviceIds = new[] { V("product"), V("variant") }.Where(x => x.Length > 0).Distinct().ToList();
            row.Bootloader = V("version-bootloader");
            row.FlashLocked = V("unlocked") == "yes" ? false : V("unlocked") == "no" ? true : (bool?)null;
            row.DisplayId = Loc.T("(mode bootloader)");
        }

        private void BuildMassOsRow(MassOsRow row)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            foreach (var w in new[] { new GridLength(30), new GridLength(220), new GridLength(260), new GridLength(80), new GridLength(1, GridUnitType.Star) })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

            row.Check = new CheckBox { Style = (Style)FindResource("ModernCheckBoxStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
            row.Check.Checked += (s, a) => UpdateMassOsRunButton();
            row.Check.Unchecked += (s, a) => UpdateMassOsRunButton();
            g.Children.Add(row.Check);

            var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var name = new TextBlock { Text = row.Model.Length > 0 ? row.Model : Loc.T("Terminal"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            var serial = new TextBlock { Text = row.Serial, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            serial.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            who.Children.Add(name); who.Children.Add(serial);
            Grid.SetColumn(who, 1); g.Children.Add(who);

            row.TxtVersion = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = row.Fingerprint.Length > 0 ? row.Fingerprint : null };
            row.TxtVersion.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            row.TxtVersion.Text = row.DisplayId.Length > 0 ? row.DisplayId : OsUpdatePackage.ShortBuild(row.Fingerprint);
            Grid.SetColumn(row.TxtVersion, 2); g.Children.Add(row.TxtVersion);

            row.TxtBattery = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Text = row.Battery >= 0 ? row.Battery + " %" : "—" };
            row.TxtBattery.SetResourceReference(TextBlock.ForegroundProperty, row.Battery >= 0 && row.Battery < MassOsMinBattery ? "WarnFg" : "Ink");
            Grid.SetColumn(row.TxtBattery, 3); g.Children.Add(row.TxtBattery);

            var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            row.TxtStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            row.Bar = new ProgressBar { Style = (Style)FindResource("ModernProgressBarStyle"), Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
            st.Children.Add(row.TxtStatus); st.Children.Add(row.Bar);
            Grid.SetColumn(st, 4); g.Children.Add(st);

            MassOsList.Children.Add(g);
        }

        /// <summary>Le paquet convient-il à ce terminal, pour la méthode choisie ?</summary>
        private (OsUpdatePackage.Fit Fit, string Why) MassOsFit(OsUpdatePackage pkg, MassOsRow r, string method)
        {
            if (method == "fastboot")
                return pkg.CheckFastboot(r.DeviceIds, r.FlashLocked, r.Bootloader);
            if (pkg.IsFastbootImage)
                return (OsUpdatePackage.Fit.Blocked, Loc.T("Image fastboot : choisissez la méthode « Fastboot »"));
            if (pkg.IsFactoryArchive)
                return (OsUpdatePackage.Fit.Blocked, Loc.T("Archive d'usine pour fastboot : elle ne s'installe pas par le Recovery"));
            return pkg.Check(r.DeviceIds, r.Fingerprint, r.BuildUtc);
        }

        /// <summary>Bibliothèque : paquet le plus récent prévu pour ce terminal (jamais un retour arrière).</summary>
        private (OsUpdatePackage Package, OsUpdatePackage.Fit Fit, string Why) PickMassOsLibraryPackage(MassOsRow r, string method)
        {
            var candidates = _massOsLibrary.Where(p => method == "fastboot" ? p.IsFastbootImage : p.IsOtaPackage)
                                           .Select(p => new { P = p, F = MassOsFit(p, r, method) }).ToList();
            var ok = candidates.Where(c => c.F.Fit == OsUpdatePackage.Fit.Ok)
                               .OrderByDescending(c => c.P.PostTimestamp).ThenByDescending(c => c.P.FileDate).FirstOrDefault();
            if (ok != null) return (ok.P, OsUpdatePackage.Fit.Ok, "");
            var same = candidates.FirstOrDefault(c => c.F.Fit == OsUpdatePackage.Fit.AlreadyThere);
            if (same != null) return (same.P, OsUpdatePackage.Fit.AlreadyThere, Loc.T("À jour : la bibliothèque n'a pas de version plus récente pour ce modèle"));
            var locked = candidates.FirstOrDefault(c => c.F.Fit == OsUpdatePackage.Fit.Blocked && r.FlashLocked == true && method == "fastboot");
            if (locked != null) return (null, OsUpdatePackage.Fit.Blocked, locked.F.Why);
            return (null, OsUpdatePackage.Fit.Blocked, Loc.T("Aucun paquet de la bibliothèque n'est prévu pour ce modèle"));
        }

        /// <summary>Recalcule, pour chaque terminal, s'il peut être mis à jour et avec quel paquet.</summary>
        private void RefreshMassOsFit()
        {
            if (_massOsBusy || ChkMassOsBattery == null) return;
            string method = MassOsMethod;
            bool library = MassOsLibraryMode;
            foreach (var r in _massOsRows)
            {
                if (r.Outcome.Length > 0) { r.Outcome = ""; r.Bar.Visibility = Visibility.Collapsed; }
                string msg; string brush = "Muted"; bool selectable = true; bool checkByDefault = true;
                r.Wipe = false; r.Downgrade = false; r.Package = null;
                switch (r.State)
                {
                    case "device":
                    case "fastboot":
                        if (r.State == "fastboot" && method != "fastboot")
                        { msg = Loc.T("En mode bootloader : choisissez la méthode « Fastboot », ou redémarrez-le normalement"); brush = "WarnFg"; selectable = false; break; }
                        msg = Loc.T("Prêt");
                        if (ChkMassOsBattery.IsChecked == true && r.Battery >= 0 && r.Battery < MassOsMinBattery)
                        { msg = Loc.F("Batterie trop faible ({0} %) : à charger avant la mise à jour", r.Battery); brush = "WarnFg"; selectable = false; break; }
                        if (method == "fastboot" && !File.Exists(FastbootExePath))
                        { msg = Loc.T("fastboot.exe absent du dossier Tools : réinstallez ADB / Fastboot (Paramètres ▸ Outils tiers)"); brush = "BadFg"; selectable = false; break; }
                        OsUpdatePackage pkg; OsUpdatePackage.Fit fit; string why;
                        if (library)
                        {
                            if (_massOsLibrary.Count == 0) { msg = Loc.T("Choisissez le dossier des paquets"); break; }
                            (pkg, fit, why) = PickMassOsLibraryPackage(r, method);
                        }
                        else
                        {
                            pkg = _massOsPackage;
                            if (pkg == null) break;
                            (fit, why) = MassOsFit(pkg, r, method);
                        }
                        if (fit == OsUpdatePackage.Fit.Blocked) { msg = why; brush = "BadFg"; selectable = false; break; }
                        r.Package = pkg;
                        if (fit == OsUpdatePackage.Fit.AlreadyThere) { msg = why.Length > 0 ? why : Loc.T("Déjà à cette version"); checkByDefault = false; if (library) selectable = false; }
                        // Retour arrière : jamais coché d'office, le technicien le choisit en connaissance de cause.
                        else if (fit == OsUpdatePackage.Fit.Downgrade) { msg = "⚠️ " + why; brush = "WarnFg"; checkByDefault = false; }
                        else if (library) msg = Loc.F("Prêt — paquet : {0}", Path.GetFileName(pkg.FilePath));
                        r.Downgrade = fit == OsUpdatePackage.Fit.Downgrade;
                        r.Wipe = r.Downgrade || (method != "fastboot" && pkg.Wipes) || (method == "fastboot" && ChkMassOsWipe.IsChecked == true);
                        break;
                    case "sideload":
                    case "recovery":
                        if (method != "sideload") { msg = Loc.T("En Recovery : redémarrez-le normalement pour cette méthode"); brush = "WarnFg"; selectable = false; }
                        else if (library) { msg = Loc.T("En Recovery : ADB Tools ne peut pas savoir quel paquet lui convient. Redémarrez-le normalement, ou choisissez « Un paquet pour tous »."); brush = "WarnFg"; selectable = false; }
                        else
                        {
                            r.Package = _massOsPackage;
                            if (r.State == "sideload") msg = Loc.T("Déjà en mode mise à jour par ADB");
                            else { msg = Loc.T("En Recovery : choisissez « Apply update from ADB » sur le terminal"); brush = "WarnFg"; }
                        }
                        break;
                    case "unauthorized":
                        msg = Loc.T("Non autorisé : acceptez le débogage USB sur l'écran du terminal, puis « Actualiser »"); brush = "WarnFg"; selectable = false; break;
                    default:
                        msg = Loc.F("Indisponible ({0})", r.State); brush = "WarnFg"; selectable = false; break;
                }
                r.Selectable = selectable;
                r.Check.IsEnabled = selectable;
                r.Check.IsChecked = selectable && checkByDefault && r.Package != null;
                SetMassOsStatus(r, msg, brush);
            }
            UpdateMassOsRunButton();
            if (!_massOsBusy && _massOsRows.Count > 0)
                TxtMassOsSummary.Text = Loc.F("{0} terminal(aux) branché(s), {1} pouvant être mis à jour.", _massOsRows.Count, _massOsRows.Count(r => r.Selectable && r.Package != null));
        }

        private void UpdateMassOsRunButton()
        {
            int n = _massOsRows.Count(r => r.Selectable && r.Package != null && r.Check.IsChecked == true);
            bool havePackage = MassOsLibraryMode ? _massOsLibrary.Count > 0 : _massOsPackage != null;
            BtnMassOsRun.IsEnabled = !_massOsBusy && havePackage && n > 0;
            BtnMassOsRun.Content = !havePackage ? (MassOsLibraryMode ? Loc.T("Choisissez d'abord le dossier") : Loc.T("Choisissez d'abord le paquet"))
                : n > 0 ? Loc.F("Mettre à jour {0} terminal(aux)", n) : Loc.T("Mettre à jour");
        }

        private void SetMassOsStatus(MassOsRow row, string text, string brushKey)
        {
            row.TxtStatus.Text = text;
            row.TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        }

        // ------------------------------------------------------------------ Lancement

        private sealed class MassOsAfter
        {
            public List<string> Apks = new List<string>();
            public List<string> Commands = new List<string>();
            public bool Any => Apks.Count > 0 || Commands.Count > 0;
        }

        private async void BtnMassOsRun_Click(object sender, RoutedEventArgs e)
        {
            var rows = _massOsRows.Where(r => r.Selectable && r.Package != null && r.Check.IsChecked == true).ToList();
            if (rows.Count == 0) return;
            var missing = rows.Select(r => r.Package.FilePath).Distinct().Where(f => !File.Exists(f)).ToList();
            if (missing.Count > 0) { TxtMassOsPackage.Text = Loc.F("Paquet introuvable : {0}. Choisissez-le à nouveau.", Path.GetFileName(missing[0])); return; }

            string method = MassOsMethod;
            int wiped = rows.Count(r => r.Wipe);
            var packages = rows.Select(r => r.Package.FilePath).Distinct().ToList();
            string what = packages.Count == 1 ? Path.GetFileName(packages[0]) : Loc.F("{0} paquets différents (un par modèle)", packages.Count);
            string wipeWarning = wiped > 0
                ? Loc.F("\n\n⚠️ {0} terminal(aux) seront EFFACÉS par ce paquet (retour à une version antérieure ou effacement demandé par le fabricant) : applications, réglages et données perdus.", wiped)
                : "";
            if (method == "fastboot")
                wipeWarning += Loc.T("\n\nFastboot : ne débranchez surtout pas un terminal pendant le flash, il pourrait ne plus démarrer.");
            bool ok = await ShowCustomDialogAsync(Loc.T("⚠️ Mise à jour OS"),
                Loc.F("{0} terminal(aux) vont recevoir {1}.{2}\n\nPendant la mise à jour, ne débranchez aucun terminal et ne fermez pas ADB Tools. Les terminaux redémarrent plusieurs fois ; l'installation peut durer de 5 à 20 minutes.\n\nLancer la mise à jour ?",
                      rows.Count, what, wipeWarning),
                Loc.T("Lancer la mise à jour"), Loc.T("Annuler"), true);
            if (!ok) return;

            bool verify = ChkMassOsVerify.IsChecked == true;
            var after = new MassOsAfter();
            if (ChkMassOsAfterApk.IsChecked == true) after.Apks.AddRange(_massOsApks.Where(File.Exists));
            if (ChkMassOsAfterCmd.IsChecked == true)
                after.Commands.AddRange((TxtMassOsCmds.Text ?? "").Replace("\r", "").Split('\n').Select(NormalizeMassOsCommand).Where(c => c.Length > 0));
            int parallel = MassOsParallel();

            _massOsBusy = true;
            _massOsWaitCts = new CancellationTokenSource();
            _massOsSlots = new SemaphoreSlim(parallel > 0 ? parallel : rows.Count);
            SetMassOsLocked(true);
            AnimateNavButton(BtnNavOsUpdate, true); // le bouton du menu pulse tant que la mise à jour tourne
            string methodName = method == "fastboot" ? Loc.T("Fastboot") : method == "copy" ? Loc.T("Copie dans la mémoire") : Loc.T("Par câble");
            foreach (var r in rows) { r.Method = methodName; r.VersionBefore = r.TxtVersion.Text; r.Started = DateTime.Now; r.Ended = null; }
            foreach (var r in _massOsRows.Where(x => !rows.Contains(x))) r.Check.IsEnabled = false;
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Mise à jour OS : {1} terminal(aux), paquet {2} ({3}).", DateTime.Now, rows.Count, what, methodName));
            AppendConsole(Loc.T("\n⚠️ NE DÉBRANCHEZ AUCUN TERMINAL PENDANT LA MISE À JOUR."));
            UpdateMassOsProgressSummary(rows);
            try
            {
                var token = _massOsWaitCts.Token;
                await Task.WhenAll(rows.Select(r =>
                    method == "fastboot" ? RunMassOsFastbootAsync(r, verify, after, token, rows)
                    : method == "copy" ? RunMassOsCopyAsync(r, verify, after, token, rows)
                    : RunMassOsDeviceAsync(r, verify, after, token, rows)));
            }
            finally
            {
                _massOsBusy = false;
                _massOsWaitCts.Dispose();
                _massOsWaitCts = null;
                _massOsSlots.Dispose();
                _massOsSlots = null;
                SetMassOsLocked(false);
                AnimateNavButton(BtnNavOsUpdate, false);
                SetTaskbarProgress(null);
                FlashTaskbarIfInBackground();
                if (MainTabControl.SelectedIndex == OsUpdateTabIndex) UpdateNavButtonsUI(BtnNavOsUpdate);
                foreach (var r in _massOsRows) r.Check.IsEnabled = r.Selectable && r.Outcome.Length == 0;
                foreach (var r in rows) r.Check.IsChecked = false;
                UpdateMassOsRunButton();
            }

            int good = rows.Count(r => r.Outcome == "ok"), warn = rows.Count(r => r.Outcome == "warn"), bad = rows.Count(r => r.Outcome == "bad");
            TxtMassOsSummary.Text = Loc.F("Terminé : {0} réussi(s), {1} à vérifier, {2} en échec.", good, warn, bad);
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Mise à jour OS terminée : {1} réussi(s), {2} à vérifier, {3} en échec.", DateTime.Now, good, warn, bad));
            if (MainTabControl.SelectedIndex != OsUpdateTabIndex)
                await ShowCustomDialogAsync(Loc.T("📦 Mise à jour OS terminée"), TxtMassOsSummary.Text, "OK", null, false);
        }

        /// <summary>« adb -s X shell ls » ou « adb shell ls » → « shell ls » : ADB Tools vise lui-même chaque terminal.</summary>
        internal static string NormalizeMassOsCommand(string line)
        {
            string c = (line ?? "").Trim();
            if (c.Length == 0 || c.StartsWith("#")) return "";
            c = System.Text.RegularExpressions.Regex.Replace(c, @"^(adb(\.exe)?\s+)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            c = System.Text.RegularExpressions.Regex.Replace(c, @"^-s\s+\S+\s+", "");
            return c.Trim();
        }

        private void SetMassOsLocked(bool busy)
        {
            foreach (var c in new Control[] { BtnMassOsPick, BtnMassOsRefresh, BtnMassOsAll, ChkMassOsBattery, ChkMassOsVerify, ChkMassOsWipe,
                                              RbMassOsSideload, RbMassOsCopy, RbMassOsFastboot, RbMassOsSingle, RbMassOsLibrary, CmbMassOsParallel,
                                              ChkMassOsAfterApk, ChkMassOsAfterCmd, BtnMassOsApks, TxtMassOsCmds, BtnMassOsRun, BtnMassOsExport })
                c.IsEnabled = !busy;
            BtnMassOsStop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnMassOsStop.IsEnabled = busy;
        }

        private void UpdateMassOsProgressSummary(List<MassOsRow> rows)
        {
            if (!_massOsBusy) return;
            int done = rows.Count(r => r.Outcome.Length > 0);
            SetTaskbarProgress(rows.Count > 0 ? (double)done / rows.Count : 0); // icône de la barre des tâches
            TxtMassOsSummary.Text = Loc.F("En cours : {0} / {1} terminé(s). Ne débranchez aucun terminal.", done, rows.Count);
        }

        /// <summary>Attend une place pour envoyer (option « Envois simultanés ») ; la ligne l'indique si l'attente dure.</summary>
        private async Task WaitMassOsSlotAsync(MassOsRow row)
        {
            if (_massOsSlots.Wait(0)) return;
            SetMassOsStatus(row, Loc.T("En attente de son tour (envois simultanés limités)…"), "Muted");
            await _massOsSlots.WaitAsync();
        }

        // ------------------------------------------------------------------ Par câble (sideload)

        private async Task RunMassOsDeviceAsync(MassOsRow row, bool verify, MassOsAfter after, CancellationToken waitToken, List<MassOsRow> all)
        {
            string file = row.Package.FilePath;
            _massOsRunning++;
            row.Check.IsEnabled = false;
            row.Bar.Visibility = Visibility.Visible;
            row.Bar.IsIndeterminate = true;
            try
            {
                // 1. Passage en mode « mise à jour par ADB ».
                // Retour arrière : le mode sideload automatique est celui de la mise à jour, qui refuse une version plus
                // ancienne. Le recovery propose une entrée distincte (« Downgrade from ADB » chez Zebra) : on redémarre
                // en Recovery et le technicien la choisit ; ADB Tools envoie le paquet dès que le terminal est prêt.
                if (row.State == "device" && row.Downgrade)
                {
                    SetMassOsStatus(row, Loc.T("Redémarrage en Recovery…"), "Ink");
                    await _adbManager.ExecuteAsync($"-s {row.Serial} reboot recovery", TimeSpan.FromSeconds(20));
                    SetMassOsStatus(row, Loc.T("Sur le terminal, menu Recovery : choisissez « Downgrade from ADB » (retour à une version antérieure). L'envoi démarre tout seul."), "WarnFg");
                }
                else if (row.State == "device")
                {
                    SetMassOsStatus(row, Loc.T("Redémarrage en mode mise à jour…"), "Ink");
                    // sideload-auto-reboot : le terminal redémarre seul une fois le paquet installé.
                    await _adbManager.ExecuteAsync($"-s {row.Serial} reboot {(verify ? "sideload-auto-reboot" : "sideload")}", TimeSpan.FromSeconds(20));
                }
                else if (row.State == "recovery")
                {
                    SetMassOsStatus(row, row.Downgrade ? Loc.T("Sur le terminal, menu Recovery : choisissez « Downgrade from ADB » (retour à une version antérieure). L'envoi démarre tout seul.")
                                                       : Loc.T("Sur le terminal : choisissez « Apply update from ADB »"), "WarnFg");
                }

                bool ready = row.State == "sideload" || await WaitForMassOsStateAsync(row, "sideload", TimeSpan.FromMinutes(15), waitToken, row.Downgrade ? TimeSpan.Zero : TimeSpan.FromSeconds(75),
                    () => SetMassOsStatus(row, Loc.T("Pas de passage automatique : sur le terminal, ouvrez le menu Recovery et choisissez « Apply update from ADB »"), "WarnFg"));
                if (!ready)
                {
                    FinishMassOs(row, "bad", waitToken.IsCancellationRequested ? Loc.T("Attente arrêtée : le terminal n'a pas reçu le paquet")
                                                                                : Loc.T("Le terminal n'est pas passé en mode mise à jour en 15 minutes"));
                    return;
                }

                // 2. Envoi : jamais interrompu.
                await WaitMassOsSlotAsync(row);
                bool sent; string output;
                try
                {
                    SetMassOsStatus(row, Loc.T("Envoi du paquet… ne pas débrancher"), "Ink");
                    row.Bar.IsIndeterminate = false;
                    row.Bar.Value = 0;
                    var transfer = new AdbTransferProcess(OsUpdatePackage.SideloadProgress);
                    bool exitOk;
                    (exitOk, output) = await transfer.RunAsync(_adbExePath, $"-s {row.Serial} sideload {AdbManager.QuoteLocalArg(file)}",
                        pct => Dispatcher.BeginInvoke(new Action(() =>
                        {
                            row.Bar.Value = pct;
                            SetMassOsStatus(row, Loc.F("Envoi du paquet… {0} % — ne pas débrancher", pct), "Ink");
                        })), CancellationToken.None);
                    // Certains recovery ferment la connexion en fin de transfert (code de sortie non nul) : « Total xfer » confirme la réussite.
                    sent = exitOk || (output ?? "").IndexOf("Total xfer", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                finally { _massOsSlots.Release(); }
                if (!sent)
                {
                    FinishMassOs(row, "bad", Loc.F("Paquet refusé ou envoi interrompu : {0}", LastLines(output, 2)));
                    return;
                }
                row.Bar.Value = 100;

                // Retour arrière : le terminal est effacé, le débogage USB avec ; il ne reviendra pas tout seul dans ADB Tools.
                if (row.Downgrade)
                {
                    FinishMassOs(row, "ok", Loc.T("Paquet de retour arrière envoyé : le terminal s'efface puis redémarre. Le débogage USB étant désactivé par l'effacement, vérifiez la version sur le terminal."));
                    return;
                }
                if (!verify)
                {
                    FinishMassOs(row, "ok", Loc.T("Paquet envoyé : suivez l'installation sur l'écran du terminal"));
                    return;
                }

                // 3. Installation, redémarrage, contrôle de la version.
                SetMassOsStatus(row, Loc.T("Installation puis redémarrage du terminal…"), "Ink");
                row.Bar.IsIndeterminate = true;
                bool back = await WaitForMassOsStateAsync(row, "device", TimeSpan.FromMinutes(30), waitToken, TimeSpan.Zero, null);
                if (!back)
                {
                    FinishMassOs(row, "warn", Loc.T("Paquet envoyé, mais le terminal n'a pas redémarré normalement : vérifiez son écran"));
                    return;
                }
                await FinishMassOsVerifiedAsync(row, after, Loc.T("Redémarré, mais la version n'a pas changé : le terminal a peut-être refusé le paquet (voir son écran Recovery)"));
            }
            catch (Exception ex)
            {
                FinishMassOs(row, "bad", Loc.F("Erreur : {0}", ex.Message));
            }
            finally
            {
                _massOsRunning--;
                UpdateMassOsProgressSummary(all);
            }
        }

        // ------------------------------------------------------------------ Copie dans la mémoire du terminal

        /// <summary>
        /// Méthode « copie dans la mémoire du terminal » : envoi du paquet sur le stockage interne, contrôle de sa
        /// taille, redémarrage en Recovery ; le technicien lance l'installation sur l'écran du terminal. Au retour,
        /// contrôle de la version et effacement de la copie.
        /// </summary>
        private async Task RunMassOsCopyAsync(MassOsRow row, bool verify, MassOsAfter after, CancellationToken waitToken, List<MassOsRow> all)
        {
            string file = row.Package.FilePath;
            _massOsRunning++;
            row.Check.IsEnabled = false;
            row.Bar.Visibility = Visibility.Visible;
            row.Bar.IsIndeterminate = false;
            row.Bar.Value = 0;
            // Nom sans espace ni accent : facile à retrouver dans la liste du Recovery, sans souci de guillemets.
            string remote = "/sdcard/" + MassOsRemoteName(file);
            try
            {
                await WaitMassOsSlotAsync(row);
                bool pushed; string output;
                try
                {
                    SetMassOsStatus(row, Loc.T("Copie du paquet sur le terminal… ne pas débrancher"), "Ink");
                    var transfer = new AdbTransferProcess();
                    (pushed, output) = await transfer.RunAsync(_adbExePath, $"-s {row.Serial} push {AdbManager.QuoteLocalArg(file)} {remote}",
                        pct => Dispatcher.BeginInvoke(new Action(() =>
                        {
                            row.Bar.Value = pct;
                            SetMassOsStatus(row, Loc.F("Copie du paquet… {0} % — ne pas débrancher", pct), "Ink");
                        })), CancellationToken.None);
                }
                finally { _massOsSlots.Release(); }
                if (!pushed)
                {
                    FinishMassOs(row, "bad", Loc.F("Copie impossible : {0}", LastLines(output, 2)));
                    return;
                }
                // Taille relue sur le terminal : une copie tronquée ne doit jamais être proposée à l'installation.
                var size = await _adbManager.ExecuteAsync($"-s {row.Serial} shell stat -c %s {remote}", TimeSpan.FromSeconds(15));
                long expected = new FileInfo(file).Length;
                if (!long.TryParse((size.StandardOutput ?? "").Trim(), out long got) || got != expected)
                {
                    await _adbManager.ExecuteAsync($"-s {row.Serial} shell rm -f {remote}", TimeSpan.FromSeconds(15));
                    FinishMassOs(row, "bad", Loc.T("Copie incomplète (taille différente sur le terminal) : copie effacée, recommencez"));
                    return;
                }
                row.Bar.Value = 100;

                await _adbManager.ExecuteAsync($"-s {row.Serial} reboot recovery", TimeSpan.FromSeconds(20));
                string name = Path.GetFileName(remote);
                if (row.Downgrade)
                {
                    // L'effacement supprime aussi la copie et désactive le débogage USB : pas de contrôle au retour.
                    FinishMassOs(row, "ok", Loc.F("Copié, redémarré en Recovery : sur le terminal, « Downgrade from SD card / internal storage » ▸ {0}. Le terminal sera effacé ; vérifiez ensuite sa version à l'écran.", name));
                    return;
                }
                if (!verify)
                {
                    FinishMassOs(row, "ok", Loc.F("Copié, redémarré en Recovery : sur le terminal, « Apply update from SD card / internal storage » ▸ {0}", name));
                    return;
                }

                SetMassOsStatus(row, Loc.F("Sur le terminal : « Apply update from SD card / internal storage » ▸ {0}. ADB Tools attend son redémarrage…", name), "WarnFg");
                row.Bar.IsIndeterminate = true;
                // Le terminal quitte d'abord Android : on attend qu'il ait disparu avant de guetter son retour.
                await Task.Delay(TimeSpan.FromSeconds(20));
                bool back = await WaitForMassOsStateAsync(row, "device", TimeSpan.FromMinutes(45), waitToken, TimeSpan.Zero, null);
                if (!back)
                {
                    FinishMassOs(row, "warn", waitToken.IsCancellationRequested
                        ? Loc.F("Attente arrêtée : vérifiez le terminal (la copie {0} reste dans sa mémoire)", name)
                        : Loc.F("Le terminal n'est pas revenu en 45 minutes : vérifiez son écran (la copie {0} reste dans sa mémoire)", name));
                    return;
                }
                try { await _adbManager.ExecuteAsync($"-s {row.Serial} shell rm -f {remote}", TimeSpan.FromSeconds(15)); } catch { }
                await FinishMassOsVerifiedAsync(row, after, Loc.T("Redémarré, mais la version n'a pas changé : l'installation n'a pas été lancée ou a été refusée"));
            }
            catch (Exception ex)
            {
                FinishMassOs(row, "bad", Loc.F("Erreur : {0}", ex.Message));
            }
            finally
            {
                _massOsRunning--;
                UpdateMassOsProgressSummary(all);
            }
        }

        // ------------------------------------------------------------------ Fastboot

        /// <summary>
        /// Méthode fastboot : passage en mode bootloader, contrôles (bootloader déverrouillé, modèle, version du
        /// bootloader) relus en mode bootloader, puis « fastboot update » de l'image d'usine. En cas de refus avant le
        /// flash, le terminal est redémarré normalement : rien n'a été modifié.
        /// </summary>
        private async Task RunMassOsFastbootAsync(MassOsRow row, bool verify, MassOsAfter after, CancellationToken waitToken, List<MassOsRow> all)
        {
            string file = row.Package.FilePath;
            bool wipe = ChkMassOsWipe.IsChecked == true;
            _massOsRunning++;
            row.Check.IsEnabled = false;
            row.Bar.Visibility = Visibility.Visible;
            row.Bar.IsIndeterminate = true;
            try
            {
                // 1. Mode bootloader.
                if (row.State == "device")
                {
                    SetMassOsStatus(row, Loc.T("Redémarrage en mode bootloader (fastboot)…"), "Ink");
                    await _adbManager.ExecuteAsync($"-s {row.Serial} reboot bootloader", TimeSpan.FromSeconds(20));
                    bool inFastboot = await WaitForMassOsStateAsync(row, "fastboot", TimeSpan.FromMinutes(5), waitToken, TimeSpan.FromSeconds(60),
                        () => SetMassOsStatus(row, Loc.T("Le terminal n'apparaît pas en fastboot : s'il affiche l'écran du bootloader, installez le pilote USB fastboot (Google USB Driver ou celui du fabricant)."), "WarnFg"));
                    if (!inFastboot)
                    {
                        FinishMassOs(row, "bad", waitToken.IsCancellationRequested ? Loc.T("Attente arrêtée : rien n'a été flashé")
                                                                                    : Loc.T("Le terminal n'est pas apparu en mode fastboot : rien n'a été flashé (pilote USB fastboot ?)"));
                        return;
                    }
                }

                // 2. Contrôles relus en mode bootloader : c'est là qu'ils font foi.
                SetMassOsStatus(row, Loc.T("Vérification du bootloader…"), "Ink");
                var vars = OsUpdatePackage.ParseFastbootVars(await RunFastbootAsync($"-s {row.Serial} getvar all", 20));
                string V(string k) => vars.TryGetValue(k, out var v) ? v : "";
                var ids = row.DeviceIds.Concat(new[] { V("product"), V("variant") }).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool? locked = V("unlocked") == "yes" ? false : V("unlocked") == "no" ? true : row.FlashLocked;
                string bl = V("version-bootloader").Length > 0 ? V("version-bootloader") : row.Bootloader;
                var (fit, why) = row.Package.CheckFastboot(ids, locked, bl);
                if (fit == OsUpdatePackage.Fit.Blocked)
                {
                    await RunFastbootAsync($"-s {row.Serial} reboot", 20);
                    FinishMassOs(row, "bad", why + " " + Loc.T("Rien n'a été flashé ; le terminal redémarre normalement."));
                    return;
                }

                // 3. Flash : jamais interrompu.
                await WaitMassOsSlotAsync(row);
                bool flashed; string output;
                try
                {
                    SetMassOsStatus(row, Loc.T("Flash en cours… ne pas débrancher"), "Ink");
                    var transfer = new AdbTransferProcess();
                    var lastShown = DateTime.MinValue;
                    transfer.LineReceived = line =>
                    {
                        // « Sending sparse 'system_a' 2/8 (262140 KB) » : l'étape en cours, sans inonder l'interface.
                        if ((DateTime.Now - lastShown).TotalMilliseconds < 400 || !System.Text.RegularExpressions.Regex.IsMatch(line, @"^(Sending|Writing|Erasing|Rebooting|Setting|Checking|extracting|archive)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
                        lastShown = DateTime.Now;
                        string step = line.Trim();
                        if (step.Length > 90) step = step.Substring(0, 90) + "…";
                        Dispatcher.BeginInvoke(new Action(() => SetMassOsStatus(row, Loc.F("Flash : {0} — ne pas débrancher", step), "Ink")));
                    };
                    (flashed, output) = await transfer.RunAsync(FastbootExePath, $"-s {row.Serial} {(wipe ? "-w " : "")}update {AdbManager.QuoteLocalArg(file)}", null, CancellationToken.None);
                }
                finally { _massOsSlots.Release(); }
                if (!flashed)
                {
                    FinishMassOs(row, "bad", Loc.F("Échec du flash : {0}. Le terminal reste en mode bootloader : ne le redémarrez pas avant d'avoir relancé un flash correct.", LastLines(output, 2)));
                    return;
                }
                row.Bar.IsIndeterminate = false;
                row.Bar.Value = 100;

                if (wipe)
                {
                    FinishMassOs(row, "ok", Loc.T("Flashé et effacé : le terminal redémarre. Le débogage USB étant désactivé par l'effacement, vérifiez la version sur le terminal."));
                    return;
                }
                if (!verify)
                {
                    FinishMassOs(row, "ok", Loc.T("Flashé : le terminal redémarre sur sa nouvelle version"));
                    return;
                }
                SetMassOsStatus(row, Loc.T("Redémarrage du terminal…"), "Ink");
                row.Bar.IsIndeterminate = true;
                bool back = await WaitForMassOsStateAsync(row, "device", TimeSpan.FromMinutes(20), waitToken, TimeSpan.Zero, null);
                if (!back)
                {
                    FinishMassOs(row, "warn", Loc.T("Flashé, mais le terminal n'est pas revenu sous Android : vérifiez son écran (le premier démarrage peut être long)"));
                    return;
                }
                await FinishMassOsVerifiedAsync(row, after, Loc.T("Redémarré, mais la version n'a pas changé : vérifiez l'image choisie"));
            }
            catch (Exception ex)
            {
                FinishMassOs(row, "bad", Loc.F("Erreur : {0}", ex.Message));
            }
            finally
            {
                _massOsRunning--;
                UpdateMassOsProgressSummary(all);
            }
        }

        /// <summary>Lance fastboot.exe et rend sa sortie (vide en cas d'erreur ou de délai dépassé).</summary>
        private async Task<string> RunFastbootAsync(string args, int timeoutSeconds)
        {
            if (!File.Exists(FastbootExePath)) return "";
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    var (_, output) = await new AdbTransferProcess().RunAsync(FastbootExePath, args, null, cts.Token);
                    return output ?? "";
                }
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ Retour du terminal

        /// <summary>Terminal revenu sous Android : contrôle de la version, puis actions « Après la mise à jour ».</summary>
        private async Task FinishMassOsVerifiedAsync(MassOsRow row, MassOsAfter after, string unchangedMessage)
        {
            string before = row.Fingerprint;
            await Task.Delay(TimeSpan.FromSeconds(5)); // laisse Android finir de démarrer avant de lire ses propriétés
            await ReadMassOsInfoAsync(row);
            row.TxtVersion.Text = row.DisplayId.Length > 0 ? row.DisplayId : OsUpdatePackage.ShortBuild(row.Fingerprint);
            row.TxtVersion.ToolTip = row.Fingerprint;
            bool unchanged = before.Length > 0 && row.Fingerprint.Length > 0 && row.Fingerprint == before;
            string outcome = unchanged ? "warn" : "ok";
            string message = unchanged ? unchangedMessage : Loc.F("À jour : {0}", row.TxtVersion.Text);
            if (!unchanged && after.Any)
            {
                var (allOk, summary) = await RunMassOsAfterAsync(row, after);
                message += " — " + summary;
                if (!allOk) outcome = "warn";
            }
            FinishMassOs(row, outcome, message);
        }

        /// <summary>Applications et commandes à passer après la mise à jour ; rend le résumé (« 2/2 applis, 1/1 commande »).</summary>
        private async Task<(bool AllOk, string Summary)> RunMassOsAfterAsync(MassOsRow row, MassOsAfter after)
        {
            int apkOk = 0, cmdOk = 0;
            var failed = new List<string>();
            var installed = new List<string>();
            // Android tout juste démarré : le gestionnaire de paquets peut ne pas être prêt aussitôt.
            await WaitForMassOsBootCompletedAsync(row);
            foreach (var apk in after.Apks)
            {
                SetMassOsStatus(row, Loc.F("Installation de {0}…", Path.GetFileName(apk)), "Ink");
                var res = await _adbManager.ExecuteAsync($"-s {row.Serial} install -r {AdbManager.QuoteLocalArg(apk)}", TimeSpan.FromMinutes(5));
                if ((res.StandardOutput ?? "").Contains("Success")) { apkOk++; installed.Add(Path.GetFileNameWithoutExtension(apk)); }
                else failed.Add(Path.GetFileName(apk));
            }
            foreach (var cmd in after.Commands)
            {
                SetMassOsStatus(row, Loc.F("Commande : {0}", cmd), "Ink");
                var res = await _adbManager.ExecuteAsync($"-s {row.Serial} {cmd}", TimeSpan.FromMinutes(2));
                if (res.IsSuccess) cmdOk++;
                else failed.Add(cmd);
            }
            var parts = new List<string>();
            if (after.Apks.Count > 0) parts.Add(Loc.F("{0}/{1} application(s) installée(s)", apkOk, after.Apks.Count) + (installed.Count > 0 ? " (" + string.Join(", ", installed) + ")" : ""));
            if (after.Commands.Count > 0) parts.Add(Loc.F("{0}/{1} commande(s) réussie(s)", cmdOk, after.Commands.Count));
            if (failed.Count > 0) parts.Add(Loc.F("en échec : {0}", string.Join(", ", failed)));
            return (failed.Count == 0, string.Join(", ", parts));
        }

        private async Task WaitForMassOsBootCompletedAsync(MassOsRow row)
        {
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    var r = await _adbManager.ExecuteAsync($"-s {row.Serial} shell getprop sys.boot_completed", TimeSpan.FromSeconds(10));
                    if ((r.StandardOutput ?? "").Trim() == "1") return;
                }
                catch { }
                await Task.Delay(3000);
            }
        }

        // ------------------------------------------------------------------ Outils

        /// <summary>« Mise à jour TC58 14.zip » → « update_Mise_a_jour_TC58_14.zip » (lettres, chiffres, . _ - seulement).</summary>
        private static string MassOsRemoteName(string file)
        {
            string n = Path.GetFileName(file).Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in n)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(c) && c < 128 || c == '.' || c == '-' || c == '_' ? c : '_');
            }
            string clean = sb.ToString();
            return clean.StartsWith("update", StringComparison.OrdinalIgnoreCase) ? clean : "update_" + clean;
        }

        private void FinishMassOs(MassOsRow row, string outcome, string message)
        {
            row.Outcome = outcome;
            row.Message = message;
            row.Ended = DateTime.Now;
            row.Bar.IsIndeterminate = false;
            if (outcome != "ok") row.Bar.Visibility = Visibility.Collapsed;
            string icon = outcome == "ok" ? "✅" : outcome == "warn" ? "⚠️" : "❌";
            SetMassOsStatus(row, icon + " " + message, outcome == "ok" ? "OkFg" : outcome == "warn" ? "WarnFg" : "BadFg");
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] {icon} {row.Serial}{(row.Model.Length > 0 ? " (" + row.Model + ")" : "")} : {message}");
        }

        /// <summary>
        /// Attend que le terminal apparaisse dans l'état voulu (« sideload », « device », « fastboot »…).
        /// <paramref name="hintAfter"/> : délai au-delà duquel <paramref name="onSlow"/> est appelé une fois.
        /// </summary>
        private async Task<bool> WaitForMassOsStateAsync(MassOsRow row, string state, TimeSpan timeout, CancellationToken token, TimeSpan hintAfter, Action onSlow)
        {
            var start = DateTime.UtcNow;
            bool hinted = false;
            while (DateTime.UtcNow - start < timeout)
            {
                if (token.IsCancellationRequested) return false;
                try
                {
                    if (state == "fastboot")
                    {
                        if (OsUpdatePackage.ParseFastbootDevices(await RunFastbootAsync("devices", 10)).Contains(row.Serial)) return true;
                    }
                    else
                    {
                        var res = await _adbManager.ExecuteAsync("devices", TimeSpan.FromSeconds(10));
                        if (res.IsSuccess && ParseAdbDevices(res.StandardOutput).Any(d => d.Serial == row.Serial && d.State == state)) return true;
                    }
                }
                catch { /* adb occupé par les autres envois : on réessaie au tour suivant */ }
                if (!hinted && onSlow != null && hintAfter > TimeSpan.Zero && DateTime.UtcNow - start > hintAfter) { hinted = true; onSlow(); }
                try { await Task.Delay(3000, token); } catch (TaskCanceledException) { return false; }
            }
            return false;
        }

        private static string LastLines(string output, int count) =>
            string.Join(" | ", (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).Reverse().Take(count).Reverse());

        // ------------------------------------------------------------------ Bilan

        private static readonly string[] MassOsReportHeaders =
            { "N° de série", "Modèle", "Version avant", "Version après", "Batterie", "Méthode", "Paquet", "Résultat", "Détail", "Début", "Fin" };

        private string MassOsOutcomeText(MassOsRow r) =>
            r.Outcome == "ok" ? Loc.T("Réussi") : r.Outcome == "warn" ? Loc.T("À vérifier") : r.Outcome == "bad" ? Loc.T("Échec") : Loc.T("Non lancé");

        private List<string[]> MassOsReportRows() => _massOsRows.Select(r => new[]
        {
            r.Serial, r.Model,
            r.Outcome.Length > 0 ? r.VersionBefore : r.TxtVersion.Text,
            r.Outcome.Length > 0 ? r.TxtVersion.Text : "",
            r.Battery >= 0 ? r.Battery + " %" : "",
            r.Method,
            r.Package != null ? Path.GetFileName(r.Package.FilePath) : "",
            MassOsOutcomeText(r),
            r.Outcome.Length > 0 ? r.Message : r.TxtStatus.Text,
            r.Started?.ToString("dd/MM/yyyy HH:mm:ss") ?? "",
            r.Ended?.ToString("dd/MM/yyyy HH:mm:ss") ?? ""
        }).ToList();

        private List<string> MassOsReportTitle()
        {
            var t = new List<string> { Loc.F("Mise à jour OS — {0:dd/MM/yyyy HH:mm}", DateTime.Now) };
            if (MassOsLibraryMode) { if (TxtMassOsFile.Text.Length > 0) t.Add(Loc.F("Bibliothèque : {0}", TxtMassOsFile.Text)); }
            else if (_massOsPackage != null) t.Add(Loc.F("Paquet : {0}", Path.GetFileName(_massOsPackage.FilePath) + " — " + _massOsPackage.Describe()));
            int good = _massOsRows.Count(r => r.Outcome == "ok"), warn = _massOsRows.Count(r => r.Outcome == "warn"), bad = _massOsRows.Count(r => r.Outcome == "bad");
            if (good + warn + bad > 0) t.Add(Loc.F("Bilan : {0} réussi(s), {1} à vérifier, {2} en échec.", good, warn, bad));
            return t;
        }

        private void BtnMassOsExport_Click(object sender, RoutedEventArgs e)
        {
            if (_massOsRows.Count == 0) { TxtMassOsSummary.Text = Loc.T("Aucun terminal dans la liste : rien à exporter."); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = Loc.T("Classeur Excel (*.xlsx)|*.xlsx"),
                FileName = $"MiseAJourOS_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
                Title = Loc.T("Exporter le bilan de la mise à jour OS")
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var rows = MassOsReportRows();
                XlsxExport.Write(dlg.FileName, Loc.T("Mise à jour OS"), MassOsReportTitle(), MassOsReportHeaders.Select(Loc.T).ToList(), rows,
                    i => _massOsRows[i].Outcome == "ok" ? XlsxExport.StyleOk : _massOsRows[i].Outcome == "warn" ? XlsxExport.StyleWarn
                       : _massOsRows[i].Outcome == "bad" ? XlsxExport.StyleBad : XlsxExport.StyleNormal);
                TxtMassOsSummary.Text = Loc.F("Bilan exporté : {0}", Path.GetFileName(dlg.FileName));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📊 Bilan de la mise à jour OS exporté : {1}", DateTime.Now, dlg.FileName));
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { /* pas d'Excel : le fichier reste enregistré */ }
            }
            catch (Exception ex)
            {
                TxtMassOsSummary.Text = Loc.F("Export impossible : {0}", ex.Message);
            }
        }

        private void BtnMassOsCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_massOsRows.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var line in MassOsReportTitle()) sb.AppendLine(line);
            sb.AppendLine(string.Join("\t", MassOsReportHeaders.Select(Loc.T)));
            foreach (var r in MassOsReportRows()) sb.AppendLine(string.Join("\t", r));
            try
            {
                Clipboard.SetText(sb.ToString());
                TxtMassOsSummary.Text = Loc.T("Bilan copié : collez-le dans Excel ou dans un ticket.");
            }
            catch { /* presse-papiers occupé par une autre application */ }
        }
    }
}
