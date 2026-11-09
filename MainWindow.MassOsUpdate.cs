using System;
using System.Collections.Generic;
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
    /// Mise à jour OS (Outils avancés ▸ Mise à jour OS), pour un ou plusieurs terminaux de toutes marques : le même
    /// paquet .zip installé sur tous les terminaux cochés, en parallèle, chacun avec sa progression. Deux méthodes :
    /// - par câble (« adb sideload ») : la plus courante (Zebra, Honeywell, Datalogic, Newland, Urovo…) ;
    /// - copie dans la mémoire du terminal puis redémarrage en Recovery, où le technicien choisit « Apply update
    ///   from SD card / internal storage » : pour les modèles qui refusent le sideload.
    /// Pour chaque terminal : vérifications (modèle visé par le paquet, batterie), redémarrage direct en mode
    /// sideload (« reboot sideload-auto-reboot »), ou attente que le technicien choisisse « Apply update from
    /// ADB » quand le modèle ne le permet pas, envoi, puis retour du terminal et contrôle de la nouvelle version.
    /// Un envoi commencé n'est jamais interrompu (risque de terminal inutilisable) ; seules les attentes s'arrêtent.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class MassOsRow
        {
            public string Serial = "";
            public string State = "";
            public string Model = "";
            public string Device = "";
            public string Fingerprint = "";
            public string DisplayId = "";
            public string DisplayIdAfter = "";
            public int Battery = -1;
            public bool Selectable;
            public string Outcome = "";   // ok, warn, bad ou vide (pas lancé)
            public string Message = "";
            public CheckBox Check;
            public TextBlock TxtVersion;
            public TextBlock TxtBattery;
            public TextBlock TxtStatus;
            public ProgressBar Bar;
        }

        private const int MassOsMinBattery = 30;
        private readonly List<MassOsRow> _massOsRows = new List<MassOsRow>();
        private OsUpdatePackage _massOsPackage;
        private CancellationTokenSource _massOsWaitCts;
        private bool _massOsBusy;
        private bool _massOsHooked;

        // ------------------------------------------------------------------ Ouverture / fermeture

        private const string MassOsIntroSideload = "Tous les terminaux cochés reçoivent le même paquet, en même temps. ADB Tools redémarre chacun en mode « mise à jour par ADB » ; si un modèle ne le permet pas, choisissez « Apply update from ADB » dans son menu Recovery et ADB Tools continue tout seul. Un terminal pour lequel le paquet n'est pas prévu est écarté. Ne débranchez aucun terminal pendant l'envoi.";
        private const string MassOsIntroCopy = "Le paquet est copié dans la mémoire de chaque terminal coché, puis le terminal redémarre en Recovery : choisissez « Apply update from SD card » (ou « internal storage ») puis le fichier « update_… ». ADB Tools attend son retour pour vérifier la version, puis efface la copie. Pour les modèles qui refusent le sideload.";
        private const string OsHelpSideload = "Le paquet passe par le câble et le terminal l'installe depuis son mode Recovery. Zebra, Honeywell, Datalogic, Newland, Urovo et la plupart des terminaux Android.";
        private const string OsHelpCopy = "Le paquet est copié sur le terminal, qui redémarre en Recovery : il reste à choisir « Apply update from SD card / internal storage » sur son écran. Pour les modèles qui refusent le sideload.";

        private bool MassOsCopyMode => RbMassOsCopy.IsChecked == true;

        // Choix de la méthode sur la carte de la page Outils avancés.
        private void OsMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (TxtOsMethodHelp == null) return;
            TxtOsMethodHelp.Text = Loc.T(RbOsCopy.IsChecked == true ? OsHelpCopy : OsHelpSideload);
        }

        // Choix de la méthode dans la fenêtre : textes et terminaux éligibles mis à jour.
        private void MassOsMethod_Checked(object sender, RoutedEventArgs e)
        {
            if (TxtMassOsIntro == null) return;
            TxtMassOsIntro.Text = Loc.T(MassOsCopyMode ? MassOsIntroCopy : MassOsIntroSideload);
            RefreshMassOsFit();
        }

        private async void BtnMassOsOpen_Click(object sender, RoutedEventArgs e)
        {
            if (!_massOsBusy)
            {
                if (RbOsCopy.IsChecked == true) RbMassOsCopy.IsChecked = true; else RbMassOsSideload.IsChecked = true;
                TxtMassOsIntro.Text = Loc.T(MassOsCopyMode ? MassOsIntroCopy : MassOsIntroSideload);
            }
            if (!_massOsHooked)
            {
                _massOsHooked = true;
                ChkMassOsBattery.Checked += (s, a) => RefreshMassOsFit();
                ChkMassOsBattery.Unchecked += (s, a) => RefreshMassOsFit();
            }
            MassOsOverlay.Visibility = Visibility.Visible;
            // Pendant une mise à jour, la fenêtre se rouvre telle quelle (les lignes suivent les envois en cours).
            if (!_massOsBusy) await LoadMassOsDevicesAsync();
        }

        private void BtnMassOsClose_Click(object sender, RoutedEventArgs e)
        {
            MassOsOverlay.Visibility = Visibility.Collapsed;
            if (_massOsBusy)
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 La mise à jour OS continue : rouvrez « Mettre à jour l'OS… » pour suivre les terminaux.", DateTime.Now));
        }

        private async void BtnMassOsRefresh_Click(object sender, RoutedEventArgs e) => await LoadMassOsDevicesAsync();

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

        // ------------------------------------------------------------------ Paquet

        private void BtnMassOsPick_Click(object sender, RoutedEventArgs e)
        {
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
                TxtMassOsPackage.Text = System.IO.Path.GetFileName(dlg.FileName) + " — " + _massOsPackage.Describe();
            }
            catch (Exception ex)
            {
                _massOsPackage = null;
                TxtMassOsFile.Text = "";
                TxtMassOsPackage.Text = Loc.F("Ce fichier n'est pas une archive .zip lisible : {0}", ex.Message);
            }
            RefreshMassOsFit();
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
                // Lecture des infos de chaque terminal en parallèle : avec 10 terminaux, 10 fois plus vite.
                var rows = devices.Select(d => new MassOsRow { Serial = d.Serial, State = d.State }).ToList();
                await Task.WhenAll(rows.Where(r => r.State == "device").Select(ReadMassOsInfoAsync));
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
                var s = await _adbManager.RunShellSectionsAsync(row.Serial, TimeSpan.FromSeconds(15),
                    "getprop ro.product.model", "getprop ro.product.device", "getprop ro.build.fingerprint", "getprop ro.build.display.id", "dumpsys battery");
                row.Model = s[0]; row.Device = s[1]; row.Fingerprint = s[2]; row.DisplayId = s[3];
                row.Battery = OsUpdatePackage.ParseBatteryLevel(s[4]);
            }
            catch { /* terminal qui ne répond pas : il reste listé, sans détails */ }
        }

        private void BuildMassOsRow(MassOsRow row)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            foreach (var w in new[] { new GridLength(30), new GridLength(200), new GridLength(220), new GridLength(70), new GridLength(1, GridUnitType.Star) })
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

        /// <summary>Recalcule, pour chaque terminal, s'il peut recevoir le paquet (sans toucher aux lignes déjà lancées).</summary>
        private void RefreshMassOsFit()
        {
            if (_massOsBusy) return;
            foreach (var r in _massOsRows)
            {
                if (r.Outcome.Length > 0) { r.Outcome = ""; r.Bar.Visibility = Visibility.Collapsed; }
                string msg; string brush = "Muted"; bool selectable = true; bool checkByDefault = true;
                switch (r.State)
                {
                    case "device":
                        msg = Loc.T("Prêt");
                        if (ChkMassOsBattery.IsChecked == true && r.Battery >= 0 && r.Battery < MassOsMinBattery)
                        { msg = Loc.F("Batterie trop faible ({0} %) : à charger avant la mise à jour", r.Battery); brush = "WarnFg"; selectable = false; }
                        else if (_massOsPackage != null)
                        {
                            var (fit, why) = _massOsPackage.Check(r.Device, r.Fingerprint);
                            if (fit == OsUpdatePackage.Fit.Blocked) { msg = why; brush = "BadFg"; selectable = false; }
                            else if (fit == OsUpdatePackage.Fit.AlreadyThere) { msg = why; checkByDefault = false; }
                        }
                        break;
                    case "sideload":
                    case "recovery":
                        if (MassOsCopyMode) { msg = Loc.T("La copie demande un terminal démarré normalement (pas en Recovery)"); brush = "WarnFg"; selectable = false; }
                        else if (r.State == "sideload") msg = Loc.T("Déjà en mode mise à jour par ADB");
                        else { msg = Loc.T("En Recovery : choisissez « Apply update from ADB » sur le terminal"); brush = "WarnFg"; }
                        break;
                    case "unauthorized":
                        msg = Loc.T("Non autorisé : acceptez le débogage USB sur l'écran du terminal, puis « Actualiser »"); brush = "WarnFg"; selectable = false; break;
                    default:
                        msg = Loc.F("Indisponible ({0})", r.State); brush = "WarnFg"; selectable = false; break;
                }
                r.Selectable = selectable;
                r.Check.IsEnabled = selectable;
                r.Check.IsChecked = selectable && checkByDefault;
                SetMassOsStatus(r, msg, brush);
            }
            UpdateMassOsRunButton();
            if (!_massOsBusy && _massOsRows.Count > 0)
                TxtMassOsSummary.Text = Loc.F("{0} terminal(aux) branché(s), {1} pouvant être mis à jour.", _massOsRows.Count, _massOsRows.Count(r => r.Selectable));
        }

        private void UpdateMassOsRunButton()
        {
            int n = _massOsRows.Count(r => r.Selectable && r.Check.IsChecked == true);
            BtnMassOsRun.IsEnabled = !_massOsBusy && _massOsPackage != null && n > 0;
            BtnMassOsRun.Content = _massOsPackage == null ? Loc.T("Choisissez d'abord le paquet")
                : n > 0 ? Loc.F("Mettre à jour {0} terminal(aux)", n) : Loc.T("Mettre à jour");
        }

        private void SetMassOsStatus(MassOsRow row, string text, string brushKey)
        {
            row.TxtStatus.Text = text;
            row.TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        }

        // ------------------------------------------------------------------ Lancement

        private async void BtnMassOsRun_Click(object sender, RoutedEventArgs e)
        {
            var rows = _massOsRows.Where(r => r.Selectable && r.Check.IsChecked == true).ToList();
            if (rows.Count == 0 || _massOsPackage == null) return;
            string file = _massOsPackage.FilePath;
            if (!System.IO.File.Exists(file)) { TxtMassOsPackage.Text = Loc.T("Le paquet n'est plus à cet emplacement : choisissez-le à nouveau."); return; }

            bool ok = await ShowCustomDialogAsync(Loc.T("⚠️ Mise à jour OS"),
                Loc.F("{0} terminal(aux) vont recevoir {1}.\n\nPendant la mise à jour, ne débranchez aucun terminal et ne fermez pas ADB Tools. Les terminaux redémarrent plusieurs fois ; l'installation peut durer de 5 à 20 minutes.\n\nLancer la mise à jour ?",
                      rows.Count, System.IO.Path.GetFileName(file)),
                Loc.T("Lancer la mise à jour"), Loc.T("Annuler"), true);
            if (!ok) return;

            bool verify = ChkMassOsVerify.IsChecked == true;
            bool copy = MassOsCopyMode;
            _massOsBusy = true;
            _massOsWaitCts = new CancellationTokenSource();
            SetMassOsLocked(true);
            foreach (var r in _massOsRows.Where(x => !rows.Contains(x))) r.Check.IsEnabled = false;
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Mise à jour OS : {1} terminal(aux), paquet {2} ({3}).", DateTime.Now, rows.Count, System.IO.Path.GetFileName(file),
                copy ? Loc.T("copie dans la mémoire du terminal") : Loc.T("par câble")));
            AppendConsole(Loc.T("\n⚠️ NE DÉBRANCHEZ AUCUN TERMINAL PENDANT LA MISE À JOUR."));
            UpdateMassOsProgressSummary(rows);
            try
            {
                var token = _massOsWaitCts.Token;
                await Task.WhenAll(rows.Select(r => copy ? RunMassOsCopyAsync(r, file, verify, token, rows) : RunMassOsDeviceAsync(r, file, verify, token, rows)));
            }
            finally
            {
                _massOsBusy = false;
                _massOsWaitCts.Dispose();
                _massOsWaitCts = null;
                SetMassOsLocked(false);
                foreach (var r in _massOsRows) r.Check.IsEnabled = r.Selectable && r.Outcome.Length == 0;
                foreach (var r in rows) r.Check.IsChecked = false;
                UpdateMassOsRunButton();
            }

            int good = rows.Count(r => r.Outcome == "ok"), warn = rows.Count(r => r.Outcome == "warn"), bad = rows.Count(r => r.Outcome == "bad");
            TxtMassOsSummary.Text = Loc.F("Terminé : {0} réussi(s), {1} à vérifier, {2} en échec.", good, warn, bad);
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Mise à jour OS terminée : {1} réussi(s), {2} à vérifier, {3} en échec.", DateTime.Now, good, warn, bad));
            if (MassOsOverlay.Visibility != Visibility.Visible)
                await ShowCustomDialogAsync(Loc.T("📦 Mise à jour OS terminée"), TxtMassOsSummary.Text, "OK", null, false);
        }

        private void SetMassOsLocked(bool busy)
        {
            BtnMassOsPick.IsEnabled = !busy;
            BtnMassOsRefresh.IsEnabled = !busy;
            BtnMassOsAll.IsEnabled = !busy;
            ChkMassOsBattery.IsEnabled = !busy;
            ChkMassOsVerify.IsEnabled = !busy;
            RbMassOsSideload.IsEnabled = !busy;
            RbMassOsCopy.IsEnabled = !busy;
            BtnMassOsRun.IsEnabled = !busy;
            BtnMassOsStop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnMassOsStop.IsEnabled = busy;
        }

        private void UpdateMassOsProgressSummary(List<MassOsRow> rows)
        {
            if (!_massOsBusy) return;
            int done = rows.Count(r => r.Outcome.Length > 0);
            TxtMassOsSummary.Text = Loc.F("En cours : {0} / {1} terminé(s). Ne débranchez aucun terminal.", done, rows.Count);
        }

        private async Task RunMassOsDeviceAsync(MassOsRow row, string file, bool verify, CancellationToken waitToken, List<MassOsRow> all)
        {
            _massOsRunning++;
            row.Check.IsEnabled = false;
            row.Bar.Visibility = Visibility.Visible;
            row.Bar.IsIndeterminate = true;
            try
            {
                // 1. Passage en mode « mise à jour par ADB ».
                if (row.State == "device")
                {
                    SetMassOsStatus(row, Loc.T("Redémarrage en mode mise à jour…"), "Ink");
                    // sideload-auto-reboot : le terminal redémarre seul une fois le paquet installé.
                    await _adbManager.ExecuteAsync($"-s {row.Serial} reboot {(verify ? "sideload-auto-reboot" : "sideload")}", TimeSpan.FromSeconds(20));
                }
                else if (row.State == "recovery")
                {
                    SetMassOsStatus(row, Loc.T("Sur le terminal : choisissez « Apply update from ADB »"), "WarnFg");
                }

                bool ready = row.State == "sideload" || await WaitForMassOsStateAsync(row, "sideload", TimeSpan.FromMinutes(15), waitToken, TimeSpan.FromSeconds(75),
                    () => SetMassOsStatus(row, Loc.T("Pas de passage automatique : sur le terminal, ouvrez le menu Recovery et choisissez « Apply update from ADB »"), "WarnFg"));
                if (!ready)
                {
                    FinishMassOs(row, "bad", waitToken.IsCancellationRequested ? Loc.T("Attente arrêtée : le terminal n'a pas reçu le paquet")
                                                                                : Loc.T("Le terminal n'est pas passé en mode mise à jour en 15 minutes"));
                    return;
                }

                // 2. Envoi : jamais interrompu.
                SetMassOsStatus(row, Loc.T("Envoi du paquet… ne pas débrancher"), "Ink");
                row.Bar.IsIndeterminate = false;
                row.Bar.Value = 0;
                var transfer = new AdbTransferProcess(OsUpdatePackage.SideloadProgress);
                var (exitOk, output) = await transfer.RunAsync(_adbExePath, $"-s {row.Serial} sideload {AdbManager.QuoteLocalArg(file)}",
                    pct => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        row.Bar.Value = pct;
                        SetMassOsStatus(row, Loc.F("Envoi du paquet… {0} % — ne pas débrancher", pct), "Ink");
                    })), CancellationToken.None);
                // Certains recovery ferment la connexion en fin de transfert (code de sortie non nul) : « Total xfer » confirme la réussite.
                bool sent = exitOk || (output ?? "").IndexOf("Total xfer", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!sent)
                {
                    FinishMassOs(row, "bad", Loc.F("Paquet refusé ou envoi interrompu : {0}", LastLines(output, 2)));
                    return;
                }
                row.Bar.Value = 100;

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
                string before = row.Fingerprint;
                await Task.Delay(TimeSpan.FromSeconds(5)); // laisse Android finir de démarrer avant de lire ses propriétés
                await ReadMassOsInfoAsync(row);
                row.DisplayIdAfter = row.DisplayId;
                row.TxtVersion.Text = row.DisplayId.Length > 0 ? row.DisplayId : OsUpdatePackage.ShortBuild(row.Fingerprint);
                row.TxtVersion.ToolTip = row.Fingerprint;
                if (before.Length > 0 && row.Fingerprint.Length > 0 && row.Fingerprint == before)
                    FinishMassOs(row, "warn", Loc.T("Redémarré, mais la version n'a pas changé : le terminal a peut-être refusé le paquet (voir son écran Recovery)"));
                else
                    FinishMassOs(row, "ok", Loc.F("À jour : {0}", row.TxtVersion.Text));
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

        /// <summary>
        /// Méthode « copie dans la mémoire du terminal » : envoi du paquet sur le stockage interne, contrôle de sa
        /// taille, redémarrage en Recovery ; le technicien lance l'installation sur l'écran du terminal. Au retour,
        /// contrôle de la version et effacement de la copie.
        /// </summary>
        private async Task RunMassOsCopyAsync(MassOsRow row, string file, bool verify, CancellationToken waitToken, List<MassOsRow> all)
        {
            _massOsRunning++;
            row.Check.IsEnabled = false;
            row.Bar.Visibility = Visibility.Visible;
            row.Bar.IsIndeterminate = false;
            row.Bar.Value = 0;
            // Nom sans espace ni accent : facile à retrouver dans la liste du Recovery, sans souci de guillemets.
            string remote = "/sdcard/" + MassOsRemoteName(file);
            try
            {
                SetMassOsStatus(row, Loc.T("Copie du paquet sur le terminal… ne pas débrancher"), "Ink");
                var transfer = new AdbTransferProcess();
                var (pushed, output) = await transfer.RunAsync(_adbExePath, $"-s {row.Serial} push {AdbManager.QuoteLocalArg(file)} {remote}",
                    pct => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        row.Bar.Value = pct;
                        SetMassOsStatus(row, Loc.F("Copie du paquet… {0} % — ne pas débrancher", pct), "Ink");
                    })), CancellationToken.None);
                if (!pushed)
                {
                    FinishMassOs(row, "bad", Loc.F("Copie impossible : {0}", LastLines(output, 2)));
                    return;
                }
                // Taille relue sur le terminal : une copie tronquée ne doit jamais être proposée à l'installation.
                var size = await _adbManager.ExecuteAsync($"-s {row.Serial} shell stat -c %s {remote}", TimeSpan.FromSeconds(15));
                long expected = new System.IO.FileInfo(file).Length;
                if (!long.TryParse((size.StandardOutput ?? "").Trim(), out long got) || got != expected)
                {
                    await _adbManager.ExecuteAsync($"-s {row.Serial} shell rm -f {remote}", TimeSpan.FromSeconds(15));
                    FinishMassOs(row, "bad", Loc.T("Copie incomplète (taille différente sur le terminal) : copie effacée, recommencez"));
                    return;
                }
                row.Bar.Value = 100;

                await _adbManager.ExecuteAsync($"-s {row.Serial} reboot recovery", TimeSpan.FromSeconds(20));
                string name = System.IO.Path.GetFileName(remote);
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
                string before = row.Fingerprint;
                await Task.Delay(TimeSpan.FromSeconds(5));
                await ReadMassOsInfoAsync(row);
                try { await _adbManager.ExecuteAsync($"-s {row.Serial} shell rm -f {remote}", TimeSpan.FromSeconds(15)); } catch { }
                row.TxtVersion.Text = row.DisplayId.Length > 0 ? row.DisplayId : OsUpdatePackage.ShortBuild(row.Fingerprint);
                row.TxtVersion.ToolTip = row.Fingerprint;
                if (before.Length > 0 && row.Fingerprint.Length > 0 && row.Fingerprint == before)
                    FinishMassOs(row, "warn", Loc.T("Redémarré, mais la version n'a pas changé : l'installation n'a pas été lancée ou a été refusée"));
                else
                    FinishMassOs(row, "ok", Loc.F("À jour : {0}", row.TxtVersion.Text));
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

        /// <summary>« Mise à jour TC58 14.zip » → « update_Mise_a_jour_TC58_14.zip » (lettres, chiffres, . _ - seulement).</summary>
        private static string MassOsRemoteName(string file)
        {
            string n = System.IO.Path.GetFileName(file).Normalize(NormalizationForm.FormD);
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
            row.Bar.IsIndeterminate = false;
            if (outcome != "ok") row.Bar.Visibility = Visibility.Collapsed;
            string icon = outcome == "ok" ? "✅" : outcome == "warn" ? "⚠️" : "❌";
            SetMassOsStatus(row, icon + " " + message, outcome == "ok" ? "OkFg" : outcome == "warn" ? "WarnFg" : "BadFg");
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] {icon} {row.Serial}{(row.Model.Length > 0 ? " (" + row.Model + ")" : "")} : {message}");
        }

        /// <summary>
        /// Attend que le terminal apparaisse dans l'état voulu (« sideload », « device »…). <paramref name="hintAfter"/> :
        /// délai au-delà duquel <paramref name="onSlow"/> est appelé une fois (ex. consigne pour un passage manuel).
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
                    var res = await _adbManager.ExecuteAsync("devices", TimeSpan.FromSeconds(10));
                    if (res.IsSuccess && ParseAdbDevices(res.StandardOutput).Any(d => d.Serial == row.Serial && d.State == state)) return true;
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

        private void BtnMassOsCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_massOsRows.Count == 0) return;
            var sb = new StringBuilder();
            sb.AppendLine(Loc.F("Mise à jour OS — {0:dd/MM/yyyy HH:mm}", DateTime.Now));
            if (_massOsPackage != null) sb.AppendLine(Loc.F("Paquet : {0}", System.IO.Path.GetFileName(_massOsPackage.FilePath)));
            sb.AppendLine(string.Join("\t", Loc.T("N° de série"), Loc.T("Modèle"), Loc.T("Version"), Loc.T("Résultat")));
            foreach (var r in _massOsRows)
                sb.AppendLine(string.Join("\t", r.Serial, r.Model, r.TxtVersion.Text, r.TxtStatus.Text));
            try
            {
                Clipboard.SetText(sb.ToString());
                TxtMassOsSummary.Text = Loc.T("Bilan copié : collez-le dans Excel ou dans un ticket.");
            }
            catch { /* presse-papiers occupé par une autre application */ }
        }
    }
}
