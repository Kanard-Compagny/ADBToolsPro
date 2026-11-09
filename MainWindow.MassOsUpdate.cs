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
    /// Mise à jour OS en masse (Outils avancés ▸ Mise à jour OS) : le même paquet .zip envoyé par « adb sideload »
    /// à tous les terminaux cochés, en parallèle, chacun avec sa progression.
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

        private async void BtnMassOsOpen_Click(object sender, RoutedEventArgs e)
        {
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
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 La mise à jour en masse continue : rouvrez « Mise à jour en masse » pour suivre les terminaux.", DateTime.Now));
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

            row.Check = new CheckBox { Style = (Style)FindResource("ModernCheckBoxStyle"), VerticalAlignment = VerticalAlignment.Center };
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
                        msg = Loc.T("Déjà en mode mise à jour par ADB"); break;
                    case "recovery":
                        msg = Loc.T("En Recovery : choisissez « Apply update from ADB » sur le terminal"); brush = "WarnFg"; break;
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

            bool ok = await ShowCustomDialogAsync(Loc.T("⚠️ Mise à jour OS en masse"),
                Loc.F("{0} terminal(aux) vont recevoir {1}.\n\nPendant la mise à jour, ne débranchez aucun terminal et ne fermez pas ADB Tools. Les terminaux redémarrent plusieurs fois ; l'installation peut durer de 5 à 20 minutes.\n\nLancer la mise à jour ?",
                      rows.Count, System.IO.Path.GetFileName(file)),
                Loc.T("Lancer la mise à jour"), Loc.T("Annuler"), true);
            if (!ok) return;

            bool verify = ChkMassOsVerify.IsChecked == true;
            _massOsBusy = true;
            _massOsWaitCts = new CancellationTokenSource();
            SetMassOsLocked(true);
            foreach (var r in _massOsRows.Where(x => !rows.Contains(x))) r.Check.IsEnabled = false;
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Mise à jour OS en masse : {1} terminal(aux), paquet {2}.", DateTime.Now, rows.Count, System.IO.Path.GetFileName(file)));
            AppendConsole(Loc.T("\n⚠️ NE DÉBRANCHEZ AUCUN TERMINAL PENDANT LA MISE À JOUR."));
            UpdateMassOsProgressSummary(rows);
            try
            {
                await Task.WhenAll(rows.Select(r => RunMassOsDeviceAsync(r, file, verify, _massOsWaitCts.Token, rows)));
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
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Mise à jour en masse terminée : {1} réussi(s), {2} à vérifier, {3} en échec.", DateTime.Now, good, warn, bad));
            if (MassOsOverlay.Visibility != Visibility.Visible)
                await ShowCustomDialogAsync(Loc.T("📦 Mise à jour en masse terminée"), TxtMassOsSummary.Text, "OK", null, false);
        }

        private void SetMassOsLocked(bool busy)
        {
            BtnMassOsPick.IsEnabled = !busy;
            BtnMassOsRefresh.IsEnabled = !busy;
            BtnMassOsAll.IsEnabled = !busy;
            ChkMassOsBattery.IsEnabled = !busy;
            ChkMassOsVerify.IsEnabled = !busy;
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
            sb.AppendLine(Loc.F("Mise à jour OS en masse — {0:dd/MM/yyyy HH:mm}", DateTime.Now));
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
