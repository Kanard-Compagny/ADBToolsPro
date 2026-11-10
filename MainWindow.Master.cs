using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ADBToolsNew.Core;

namespace ADBToolsNew
{
    /// <summary>
    /// Page « Master » : remettre des terminaux en état après un wipe.
    /// 1. Capture d'un terminal de référence (rien n'y est modifié) : applications installées (APK, morceaux compris)
    ///    et leurs autorisations, applis système désactivées, réglages Android (system / secure / global, hors réglages
    ///    propres à chaque terminal), réseaux Wi-Fi (Android 11+, mot de passe à saisir), lanceur par défaut,
    ///    propriétaire du terminal (kiosque), plus ce que le technicien ajoute : APK et fichiers du PC (ex. DataWedge),
    ///    dossiers du terminal, commandes ADB.
    /// 2. Application sur un ou plusieurs terminaux en parallèle, élément par élément, avec pour chacun le détail de ce
    ///    qui a été remis en place, déjà en place, non appliqué (et pourquoi) ; bilan Excel.
    /// Les données internes des applis ne se récupèrent pas sans root : c'est le rôle du MDM (SOTI, etc.), dont l'agent
    /// peut être réinstallé par le Master.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class MasterResult
        {
            public string Category = "";
            public string Element = "";
            public string Status = "";   // ok, same, ko, skip
            public string Detail = "";
        }

        private sealed class MasterTarget
        {
            public string Serial = "";
            public string Model = "";
            public string Android = "";
            public int Sdk;
            public string Outcome = "";
            public string Message = "";
            public readonly List<MasterResult> Results = new List<MasterResult>();
            public CheckBox Check;
            public TextBlock TxtStatus;
            public ProgressBar Bar;
            public TextBox TxtDetail;
            public Button BtnDetail;
        }

        private const int MasterTabIndex = 13;
        private MasterProfile _master;
        private string _masterDir;
        private bool _masterBusy;
        private TextBox _masterCmdBox;
        private readonly List<MasterTarget> _masterTargets = new List<MasterTarget>();

        private static string MasterWorkRoot => Path.Combine(Path.GetTempPath(), "ADBToolsPro", "Master");

        // ------------------------------------------------------------------ Page

        private async void NavMaster_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl == null) return;
            MainTabControl.SelectedIndex = MasterTabIndex;
            UpdateNavButtonsUI(BtnNavMaster);
            if (!_masterBusy && !_masterTargets.Any(t => t.Outcome.Length > 0)) await LoadMasterDevicesAsync();
        }

        private async void BtnMasterRefresh_Click(object sender, RoutedEventArgs e) => await LoadMasterDevicesAsync();

        /// <summary>Terminaux branchés : source possible (liste déroulante) et cibles (liste à cocher).</summary>
        private async Task LoadMasterDevicesAsync()
        {
            BtnMasterRefresh.IsEnabled = false;
            string previousSource = (CmbMasterSource.SelectedItem as ComboBoxItem)?.Tag as string;
            CmbMasterSource.Items.Clear();
            MasterTargets.Children.Clear();
            _masterTargets.Clear();
            TxtMasterSummary.Text = Loc.T("Recherche des terminaux…");
            try
            {
                var res = await _adbManager.ExecuteAsync("devices");
                var devices = (res.IsSuccess ? ParseAdbDevices(res.StandardOutput) : new List<AndroidDevice>()).Where(d => d.State == "device").ToList();
                var targets = devices.Select(d => new MasterTarget { Serial = d.Serial }).ToList();
                await Task.WhenAll(targets.Select(async t =>
                {
                    try
                    {
                        var s = await _adbManager.RunShellSectionsAsync(t.Serial, TimeSpan.FromSeconds(10), "getprop ro.product.model", "getprop ro.build.version.release", "getprop ro.build.version.sdk");
                        t.Model = s[0]; t.Android = s[1]; int.TryParse(s[2], out t.Sdk);
                    }
                    catch { }
                }));
                foreach (var t in targets)
                {
                    var item = new ComboBoxItem { Content = $"{(t.Model.Length > 0 ? t.Model : Loc.T("Terminal"))} — {t.Serial}", Tag = t.Serial };
                    CmbMasterSource.Items.Add(item);
                    if (t.Serial == previousSource) CmbMasterSource.SelectedItem = item;
                    BuildMasterTargetRow(t);
                    _masterTargets.Add(t);
                }
                if (CmbMasterSource.SelectedItem == null && CmbMasterSource.Items.Count > 0) CmbMasterSource.SelectedIndex = 0;
                if (targets.Count == 0)
                    MasterTargets.Children.Add(MasterMutedText(Loc.T("Aucun terminal détecté. Branchez les terminaux en USB, débogage USB autorisé, puis « Actualiser ».")));
                TxtMasterSummary.Text = Loc.F("{0} terminal(aux) branché(s).", targets.Count);
            }
            catch (Exception ex)
            {
                TxtMasterSummary.Text = Loc.F("Impossible de lister les terminaux : {0}", ex.Message);
            }
            finally { BtnMasterRefresh.IsEnabled = !_masterBusy; }
            UpdateMasterApplyButton();
        }

        private TextBlock MasterMutedText(string text, double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }

        private void BuildMasterTargetRow(MasterTarget t)
        {
            var box = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            var g = new Grid();
            foreach (var w in new[] { new GridLength(30), new GridLength(170), new GridLength(1, GridUnitType.Star), new GridLength(80) })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            t.Check = new CheckBox { Style = (Style)FindResource("ModernCheckBoxStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0), IsChecked = false };
            t.Check.Checked += (s, a) => UpdateMasterApplyButton();
            t.Check.Unchecked += (s, a) => UpdateMasterApplyButton();
            g.Children.Add(t.Check);

            var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var name = new TextBlock { Text = (t.Model.Length > 0 ? t.Model : Loc.T("Terminal")) + (t.Android.Length > 0 ? " · Android " + t.Android : ""), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            var serial = new TextBlock { Text = t.Serial, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            serial.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            who.Children.Add(name); who.Children.Add(serial);
            Grid.SetColumn(who, 1); g.Children.Add(who);

            var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.TxtStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = Loc.T("Prêt") };
            t.TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            t.Bar = new ProgressBar { Style = (Style)FindResource("ModernProgressBarStyle"), Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
            st.Children.Add(t.TxtStatus); st.Children.Add(t.Bar);
            Grid.SetColumn(st, 2); g.Children.Add(st);

            t.BtnDetail = new Button { Content = Loc.T("Détail ▾"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            t.TxtDetail = new TextBox { IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 11.5, MaxHeight = 260, Margin = new Thickness(30, 8, 0, 0), TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
            t.BtnDetail.Click += (s, a) =>
            {
                bool show = t.TxtDetail.Visibility != Visibility.Visible;
                t.TxtDetail.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                t.BtnDetail.Content = show ? Loc.T("Détail ▴") : Loc.T("Détail ▾");
            };
            Grid.SetColumn(t.BtnDetail, 3); g.Children.Add(t.BtnDetail);

            box.Children.Add(g);
            box.Children.Add(t.TxtDetail);
            MasterTargets.Children.Add(box);
        }

        private void UpdateMasterApplyButton()
        {
            int n = _masterTargets.Count(t => t.Check?.IsChecked == true);
            BtnMasterApply.IsEnabled = !_masterBusy && _master != null && n > 0;
            BtnMasterApply.Content = _master == null ? Loc.T("Capturez ou ouvrez d'abord un Master")
                : n > 0 ? Loc.F("Appliquer le Master à {0} terminal(aux)", n) : Loc.T("Appliquer le Master");
            BtnMasterCapture.IsEnabled = !_masterBusy && CmbMasterSource.Items.Count > 0;
            foreach (var b in new Button[] { BtnMasterAddApk, BtnMasterAddFile, BtnMasterAddFolder, BtnMasterAddWifi, BtnMasterSave })
                b.IsEnabled = !_masterBusy && _master != null;
            BtnMasterAddFolder.IsEnabled = BtnMasterAddFolder.IsEnabled && CmbMasterSource.Items.Count > 0;
            BtnMasterOpen.IsEnabled = !_masterBusy;
        }

        // ------------------------------------------------------------------ Capture

        private string NewMasterWorkDir()
        {
            try { if (_masterDir != null && Directory.Exists(_masterDir)) Directory.Delete(_masterDir, true); } catch { }
            _masterDir = Path.Combine(MasterWorkRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_masterDir);
            return _masterDir;
        }

        private async void BtnMasterCapture_Click(object sender, RoutedEventArgs e)
        {
            string serial = (CmbMasterSource.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrEmpty(serial)) return;
            if (_master != null && !await ShowCustomDialogAsync(Loc.T("Nouveau Master"), Loc.T("Le Master affiché sera remplacé par la capture de ce terminal (pensez à l'enregistrer d'abord s'il vous sert encore). Continuer ?"), Loc.T("Capturer"), Loc.T("Annuler"), false))
                return;

            _masterBusy = true;
            UpdateMasterApplyButton();
            AnimateNavButton(BtnNavMaster, true);
            string dir = NewMasterWorkDir();
            var m = new MasterProfile();
            void Step(string text) => TxtMasterSummary.Text = text;
            try
            {
                Step(Loc.T("Capture : identité du terminal…"));
                var id = await _adbManager.RunShellSectionsAsync(serial, TimeSpan.FromSeconds(15),
                    "getprop ro.product.model", "getprop ro.product.manufacturer", "getprop ro.build.version.release", "getprop ro.build.version.sdk", "getprop ro.build.display.id");
                m.SourceModel = id[0]; m.SourceManufacturer = id[1]; m.SourceAndroid = id[2]; m.SourceBuild = id[4];
                int.TryParse(id[3], out int sdk);
                m.Name = TxtMasterName.Text.Trim().Length > 0 ? TxtMasterName.Text.Trim() : Loc.F("Master {0} — {1:dd/MM/yyyy}", m.SourceModel, DateTime.Now);

                // Applications installées par l'utilisateur (hors appli de diagnostic d'ADB Tools).
                Step(Loc.T("Capture : liste des applications…"));
                var list = await _adbManager.ExecuteAsync($"-s {serial} shell pm list packages -3 --show-versioncode", TimeSpan.FromSeconds(30));
                var pkgs = MasterProfile.ParsePackagesWithVersion(list.StandardOutput);
                if (pkgs.Count == 0)
                    pkgs = MasterProfile.ParsePackagesWithVersion((await _adbManager.ExecuteAsync($"-s {serial} shell pm list packages -3", TimeSpan.FromSeconds(30))).StandardOutput);
                pkgs = pkgs.Where(p => p.Key != DiagApp.Package).OrderBy(p => p.Key).ToList();
                int n = 0;
                foreach (var p in pkgs)
                {
                    n++;
                    Step(Loc.F("Capture : application {0}/{1} — {2}", n, pkgs.Count, p.Key));
                    var app = new MasterApp { Package = p.Key, VersionCode = p.Value };
                    var paths = MasterProfile.ParsePmPath((await _adbManager.ExecuteAsync($"-s {serial} shell pm path {p.Key}", TimeSpan.FromSeconds(20))).StandardOutput);
                    string appDir = Path.Combine(dir, "apks", p.Key);
                    Directory.CreateDirectory(appDir);
                    foreach (var remote in paths)
                    {
                        string local = Path.Combine(appDir, Path.GetFileName(remote));
                        await _adbManager.ExecuteAsync($"-s {serial} pull {AdbManager.QuoteLocalArg(remote)} {AdbManager.QuoteLocalArg(local)}", TimeSpan.FromMinutes(5));
                        if (File.Exists(local)) app.Files.Add("apks/" + p.Key + "/" + Path.GetFileName(remote));
                    }
                    var dump = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys package {p.Key}", TimeSpan.FromSeconds(20));
                    app.Permissions = MasterProfile.ParseGrantedRuntimePermissions(dump.StandardOutput);
                    app.Include = app.Files.Count > 0;
                    m.Apps.Add(app);
                }

                Step(Loc.T("Capture : applications désactivées, réglages, Wi-Fi…"));
                var disabled = await _adbManager.ExecuteAsync($"-s {serial} shell pm list packages -d", TimeSpan.FromSeconds(20));
                m.DisabledApps = MasterProfile.ParsePackagesWithVersion(disabled.StandardOutput).Select(p => new MasterItem { Name = p.Key }).ToList();

                foreach (var ns in new[] { "system", "secure", "global" })
                {
                    var r = await _adbManager.ExecuteAsync($"-s {serial} shell settings list {ns}", TimeSpan.FromSeconds(20));
                    foreach (var kv in MasterProfile.ParseSettingsList(r.StandardOutput))
                        if (!MasterProfile.IsDeviceSpecificSetting(kv.Key) && kv.Value != "null")
                            m.Settings.Add(new MasterSetting { Namespace = ns, Key = kv.Key, Value = kv.Value });
                }

                if (sdk >= 30)
                    m.WifiNetworks = MasterProfile.ParseWifiNetworks((await _adbManager.ExecuteAsync($"-s {serial} shell cmd wifi list-networks", TimeSpan.FromSeconds(15))).StandardOutput);

                string home = MasterProfile.ParseHomeActivity((await _adbManager.ExecuteAsync(
                    $"-s {serial} shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME", TimeSpan.FromSeconds(15))).StandardOutput);
                if (home.Length > 0) m.Launcher = new MasterItem { Name = home };

                string owner = MasterProfile.ParseDeviceOwner((await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys device_policy", TimeSpan.FromSeconds(20))).StandardOutput);
                if (owner.Length > 0) m.DeviceOwner = new MasterItem { Name = owner };

                _master = m;
                TxtMasterName.Text = m.Name;
                RenderMasterContent();
                Step(Loc.F("Capture terminée : {0} application(s), {1} réglage(s), {2} réseau(x) Wi-Fi. Vérifiez le contenu, complétez-le puis enregistrez le Master.",
                    m.Apps.Count, m.Settings.Count, m.WifiNetworks.Count));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🧰 Master capturé sur {1} ({2}) : {3} application(s), {4} réglage(s).", DateTime.Now, m.SourceModel, serial, m.Apps.Count, m.Settings.Count));
            }
            catch (Exception ex)
            {
                Step(Loc.F("Capture interrompue : {0}", ex.Message));
            }
            finally
            {
                _masterBusy = false;
                AnimateNavButton(BtnNavMaster, false);
                if (MainTabControl.SelectedIndex == MasterTabIndex) UpdateNavButtonsUI(BtnNavMaster);
                UpdateMasterApplyButton();
            }
        }

        // ------------------------------------------------------------------ Contenu du Master (cases à cocher)

        private void RenderMasterContent()
        {
            MasterContent.Children.Clear();
            if (_master == null) return;
            var m = _master;
            TxtMasterInfo.Text = Loc.F("Capturé sur {0} {1} (Android {2}, {3}) le {4:dd/MM/yyyy HH:mm}.", m.SourceManufacturer, m.SourceModel, m.SourceAndroid, m.SourceBuild, m.CreatedAt);

            AddMasterSection(Loc.F("📦 Applications ({0})", m.Apps.Count),
                Loc.T("Réinstallées avec leurs autorisations. Les données internes des applis ne sont pas copiées (rôle du MDM)."),
                m.Apps.Select(a => (Label: a.FromPc ? Loc.F("{0} (depuis le PC)", a.Package) : a.Package + (a.VersionCode.Length > 0 ? "  v" + a.VersionCode : "") + (a.Files.Count > 1 ? Loc.F("  · {0} fichiers", a.Files.Count) : "") + (a.Permissions.Count > 0 ? Loc.F("  · {0} autorisation(s)", a.Permissions.Count) : ""),
                                     Get: (Func<bool>)(() => a.Include), Set: (Action<bool>)(v => a.Include = v))).ToList(), true);
            AddMasterSection(Loc.F("🚫 Applications désactivées ({0})", m.DisabledApps.Count),
                Loc.T("Applications (souvent système) désactivées sur le terminal de référence."),
                m.DisabledApps.Select(d => (Label: d.Name, Get: (Func<bool>)(() => d.Include), Set: (Action<bool>)(v => d.Include = v))).ToList(), false);
            AddMasterSection(Loc.F("⚙️ Réglages Android ({0})", m.Settings.Count),
                Loc.T("Écran, veille, son, langue, clavier, options… Seules les valeurs différentes sont modifiées ; les réglages propres à chaque terminal sont exclus. Android en protège certains : ils seront signalés."),
                m.Settings.Select(st => (Label: $"{st.Namespace} · {st.Key} = {(st.Value.Length > 60 ? st.Value.Substring(0, 60) + "…" : st.Value)}", Get: (Func<bool>)(() => st.Include), Set: (Action<bool>)(v => st.Include = v))).ToList(), false);
            AddMasterWifiSection();
            var single = new List<(string, Func<bool>, Action<bool>)>();
            if (m.Launcher != null) single.Add((Loc.F("Lanceur par défaut : {0}", m.Launcher.Name), () => m.Launcher.Include, v => m.Launcher.Include = v));
            if (m.DeviceOwner != null) single.Add((Loc.F("Propriétaire du terminal (kiosque / MDM) : {0}", m.DeviceOwner.Name), () => m.DeviceOwner.Include, v => m.DeviceOwner.Include = v));
            AddMasterSection(Loc.T("🏠 Lanceur et propriétaire du terminal"),
                Loc.T("Le propriétaire du terminal ne peut être défini que sur un terminal sans compte, juste après un wipe."),
                single.Select(x => (Label: x.Item1, Get: x.Item2, Set: x.Item3)).ToList(), true);
            AddMasterSection(Loc.F("📁 Fichiers ({0})", m.Files.Count),
                Loc.T("Dossiers récupérés sur le terminal de référence et fichiers ajoutés depuis le PC (ex. configuration DataWedge)."),
                m.Files.Select(f => (Label: $"{(f.IsFolder ? "📁" : "📄")} {Path.GetFileName(f.Local.TrimEnd('/'))} → {f.DevicePath}", Get: (Func<bool>)(() => f.Include), Set: (Action<bool>)(v => f.Include = v))).ToList(), true);

            // Commandes ADB libres, en fin de remise en état.
            var head = new TextBlock { Text = Loc.T("⌨️ Commandes ADB (en dernier)"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            MasterContent.Children.Add(head);
            MasterContent.Children.Add(MasterMutedText(Loc.T("Une commande par ligne, sans « adb » ni « -s ». Ex. : shell settings put global stay_on_while_plugged_in 3"), 11.5));
            _masterCmdBox = new TextBox { AcceptsReturn = true, Height = 70, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(6, 4, 6, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = string.Join(Environment.NewLine, m.Commands) };
            MasterContent.Children.Add(_masterCmdBox);
            UpdateMasterApplyButton();
        }

        /// <summary>Une catégorie : case générale (tout cocher / décocher), explication, puis ses éléments (repliables).</summary>
        private void AddMasterSection(string title, string help, List<(string Label, Func<bool> Get, Action<bool> Set)> items, bool expanded)
        {
            var header = new Grid { Margin = new Thickness(0, 14, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var all = new CheckBox { Content = title, Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0), FontWeight = FontWeights.SemiBold, IsEnabled = items.Count > 0 };
            all.IsChecked = items.Count > 0 && items.All(i => i.Get()) ? true : items.Any(i => i.Get()) ? (bool?)null : false;
            header.Children.Add(all);
            var toggle = new Button { Content = expanded ? Loc.T("Masquer ▴") : Loc.T("Voir ▾"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 24, Padding = new Thickness(8, 0, 8, 0), Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
            Grid.SetColumn(toggle, 1);
            header.Children.Add(toggle);
            MasterContent.Children.Add(header);
            MasterContent.Children.Add(MasterMutedText(items.Count == 0 ? Loc.T("Rien de capturé.") + " " + help : help, 11.5));

            var list = new StackPanel { Margin = new Thickness(30, 0, 0, 0), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
            var boxes = new List<CheckBox>();
            bool syncing = false;
            foreach (var it in items)
            {
                var cb = new CheckBox { Content = it.Label, IsChecked = it.Get(), Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0, 0, 0, 4), FontSize = 12 };
                var item = it;
                cb.Checked += (s, a) => { item.Set(true); if (!syncing) all.IsChecked = boxes.All(b => b.IsChecked == true) ? true : (bool?)null; };
                cb.Unchecked += (s, a) => { item.Set(false); if (!syncing) all.IsChecked = boxes.Any(b => b.IsChecked == true) ? (bool?)null : false; };
                boxes.Add(cb);
                list.Children.Add(cb);
            }
            all.Click += (s, a) =>
            {
                bool v = all.IsChecked == true;
                syncing = true;
                foreach (var b in boxes) b.IsChecked = v;
                syncing = false;
                all.IsChecked = v;
            };
            toggle.Click += (s, a) =>
            {
                bool show = list.Visibility != Visibility.Visible;
                list.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = show ? Loc.T("Masquer ▴") : Loc.T("Voir ▾");
            };
            MasterContent.Children.Add(list);
        }

        /// <summary>Réseaux Wi-Fi : Android ne donne pas les mots de passe, ils se saisissent ici et sont enregistrés dans le Master.</summary>
        private void AddMasterWifiSection()
        {
            var m = _master;
            var head = new TextBlock { Text = Loc.F("📶 Réseaux Wi-Fi ({0})", m.WifiNetworks.Count), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            MasterContent.Children.Add(head);
            MasterContent.Children.Add(MasterMutedText(Loc.T("Android ne donne pas les mots de passe : saisissez-les ici (ils sont enregistrés dans le Master, gardez le fichier en lieu sûr). Réinjection sur Android 11 et plus ; les réseaux d'entreprise (802.1X) restent au MDM."), 11.5));
            foreach (var w in m.WifiNetworks)
            {
                var g = new Grid { Margin = new Thickness(30, 0, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                var cb = new CheckBox { Content = $"{w.Ssid}  ({w.Security})", IsChecked = w.Include, Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var wifi = w;
                cb.Checked += (s, a) => wifi.Include = true;
                cb.Unchecked += (s, a) => wifi.Include = false;
                g.Children.Add(cb);
                string type = MasterProfile.WifiConnectType(w.Security);
                if (type == "wpa2" || type == "wpa3")
                {
                    var pwd = new TextBox { Text = w.Password, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, Tag = Loc.T("Mot de passe"), Style = (Style)FindResource("PlaceholderTextBoxStyle") };
                    pwd.TextChanged += (s, a) => wifi.Password = pwd.Text;
                    Grid.SetColumn(pwd, 1); g.Children.Add(pwd);
                }
                else
                {
                    var note = MasterMutedText(type == "open" ? Loc.T("Réseau ouvert") : Loc.T("Non réinjectable (entreprise / WEP)"), 11.5);
                    note.Margin = new Thickness(0); note.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(note, 1); g.Children.Add(note);
                }
                MasterContent.Children.Add(g);
            }
        }

        private void SyncMasterFromUi()
        {
            if (_master == null) return;
            if (TxtMasterName.Text.Trim().Length > 0) _master.Name = TxtMasterName.Text.Trim();
            if (_masterCmdBox != null)
                _master.Commands = (_masterCmdBox.Text ?? "").Replace("\r", "").Split('\n').Select(NormalizeMassOsCommand).Where(c => c.Length > 0).ToList();
        }

        // ------------------------------------------------------------------ Ajouts depuis le PC ou le terminal

        private void BtnMasterAddApk_Click(object sender, RoutedEventArgs e)
        {
            if (_master == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Applications Android (*.apk)|*.apk"), Multiselect = true, Title = Loc.T("Applications à ajouter au Master") };
            if (dlg.ShowDialog() != true) return;
            SyncMasterFromUi();
            string pcDir = Path.Combine(_masterDir, "apks", "_pc");
            Directory.CreateDirectory(pcDir);
            foreach (var f in dlg.FileNames)
            {
                string name = Path.GetFileName(f);
                File.Copy(f, Path.Combine(pcDir, name), true);
                _master.Apps.RemoveAll(a => a.FromPc && a.Files.Contains("apks/_pc/" + name));
                _master.Apps.Add(new MasterApp { Package = Path.GetFileNameWithoutExtension(f), FromPc = true, Files = new List<string> { "apks/_pc/" + name } });
            }
            RenderMasterContent();
        }

        private async void BtnMasterAddFile_Click(object sender, RoutedEventArgs e)
        {
            if (_master == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Fichier à déposer sur les terminaux") };
            if (dlg.ShowDialog() != true) return;
            // Configuration DataWedge (Zebra) : le dossier d'import automatique est la bonne destination.
            bool dw = Path.GetExtension(dlg.FileName).Equals(".db", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(dlg.FileName).ToLowerInvariant().Contains("dw");
            string suggested = dw ? "/enterprise/device/settings/datawedge/autoimport/" : "/sdcard/Download/";
            if (!await ShowCustomDialogAsync(Loc.T("📄 Destination sur le terminal"),
                    Loc.T("Dossier du terminal où déposer ce fichier. Pour une configuration DataWedge (Zebra) : /enterprise/device/settings/datawedge/autoimport/"),
                    Loc.T("Ajouter"), Loc.T("Annuler"), false, true, suggested)) return;
            string dest = TxtDialogInput.Text.Trim();
            if (dest.Length == 0) return;
            SyncMasterFromUi();
            int idx = _master.Files.Count + 1;
            string rel = $"files/{idx}/{Path.GetFileName(dlg.FileName)}";
            Directory.CreateDirectory(Path.Combine(_masterDir, "files", idx.ToString()));
            File.Copy(dlg.FileName, Path.Combine(_masterDir, rel.Replace('/', Path.DirectorySeparatorChar)), true);
            _master.Files.Add(new MasterFile { Local = rel, DevicePath = dest.EndsWith("/") ? dest : dest + "/" });
            RenderMasterContent();
        }

        private async void BtnMasterAddFolder_Click(object sender, RoutedEventArgs e)
        {
            string serial = (CmbMasterSource.SelectedItem as ComboBoxItem)?.Tag as string;
            if (_master == null || string.IsNullOrEmpty(serial)) return;
            if (!await ShowCustomDialogAsync(Loc.T("📁 Dossier du terminal de référence"),
                    Loc.T("Chemin du dossier à récupérer sur le terminal choisi en haut, puis à recopier au même endroit sur les terminaux remis en état."),
                    Loc.T("Récupérer"), Loc.T("Annuler"), false, true, "/sdcard/Documents/")) return;
            string remote = TxtDialogInput.Text.Trim().TrimEnd('/');
            if (remote.Length < 2) return;
            SyncMasterFromUi();
            int idx = _master.Files.Count + 1;
            string localParent = Path.Combine(_masterDir, "files", idx.ToString());
            Directory.CreateDirectory(localParent);
            TxtMasterSummary.Text = Loc.F("Récupération de {0}…", remote);
            var res = await _adbManager.ExecuteAsync($"-s {serial} pull {AdbManager.QuoteLocalArg(remote)} {AdbManager.QuoteLocalArg(localParent)}", TimeSpan.FromMinutes(10));
            string name = remote.Substring(remote.LastIndexOf('/') + 1);
            if (!Directory.Exists(Path.Combine(localParent, name)) && !File.Exists(Path.Combine(localParent, name)))
            {
                TxtMasterSummary.Text = Loc.F("Impossible de récupérer {0} : {1}", remote, LastLines(res.StandardError + res.StandardOutput, 1));
                return;
            }
            string parent = remote.Substring(0, remote.LastIndexOf('/') + 1);
            _master.Files.Add(new MasterFile { Local = $"files/{idx}/{name}", DevicePath = parent.Length > 0 ? parent : "/sdcard/", IsFolder = Directory.Exists(Path.Combine(localParent, name)) });
            TxtMasterSummary.Text = Loc.F("{0} ajouté au Master.", remote);
            RenderMasterContent();
        }

        private async void BtnMasterAddWifi_Click(object sender, RoutedEventArgs e)
        {
            if (_master == null) return;
            if (!await ShowCustomDialogAsync(Loc.T("📶 Réseau Wi-Fi"), Loc.T("Nom du réseau (SSID) :"), Loc.T("Suivant"), Loc.T("Annuler"), false, true, "")) return;
            string ssid = TxtDialogInput.Text.Trim();
            if (ssid.Length == 0) return;
            if (!await ShowCustomDialogAsync(Loc.T("📶 Réseau Wi-Fi"), Loc.F("Mot de passe de « {0} » (laisser vide pour un réseau ouvert) :", ssid), Loc.T("Ajouter"), Loc.T("Annuler"), false, true, "")) return;
            string pwd = TxtDialogInput.Text;
            SyncMasterFromUi();
            _master.WifiNetworks.RemoveAll(w => w.Ssid == ssid);
            _master.WifiNetworks.Add(new MasterWifi { Ssid = ssid, Security = pwd.Length > 0 ? "wpa2-psk" : "open", Password = pwd });
            RenderMasterContent();
        }

        // ------------------------------------------------------------------ Ouvrir / enregistrer

        private void BtnMasterOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Master ADB Tools (*.adbmaster)|*.adbmaster"), Title = Loc.T("Ouvrir un Master") };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string dir = NewMasterWorkDir();
                _master = MasterProfile.Open(dlg.FileName, dir);
                TxtMasterName.Text = _master.Name;
                RenderMasterContent();
                TxtMasterSummary.Text = Loc.F("Master ouvert : {0}", Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            {
                _master = null;
                MasterContent.Children.Clear();
                TxtMasterSummary.Text = Loc.F("Ouverture impossible : {0}", ex.Message);
            }
            UpdateMasterApplyButton();
        }

        private async void BtnMasterSave_Click(object sender, RoutedEventArgs e)
        {
            if (_master == null) return;
            SyncMasterFromUi();
            string safe = string.Concat((_master.Name.Length > 0 ? _master.Name : "Master").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = Loc.T("Master ADB Tools (*.adbmaster)|*.adbmaster"), FileName = safe + MasterProfile.FileExtension, Title = Loc.T("Enregistrer le Master") };
            if (dlg.ShowDialog() != true) return;
            BtnMasterSave.IsEnabled = false;
            TxtMasterSummary.Text = Loc.T("Enregistrement du Master…");
            try
            {
                string path = dlg.FileName, dir = _masterDir;
                var m = _master;
                await Task.Run(() => m.SaveTo(dir, path));
                TxtMasterSummary.Text = Loc.F("Master enregistré : {0} ({1:0.0} Mo)", Path.GetFileName(path), new FileInfo(path).Length / 1048576.0);
            }
            catch (Exception ex) { TxtMasterSummary.Text = Loc.F("Enregistrement impossible : {0}", ex.Message); }
            finally { UpdateMasterApplyButton(); }
        }

        // ------------------------------------------------------------------ Application

        private async void BtnMasterApply_Click(object sender, RoutedEventArgs e)
        {
            var targets = _masterTargets.Where(t => t.Check.IsChecked == true).ToList();
            if (_master == null || targets.Count == 0) return;
            SyncMasterFromUi();
            var m = _master;
            int apps = m.Apps.Count(a => a.Include), settings = m.Settings.Count(s => s.Include);
            string ownerNote = m.DeviceOwner?.Include == true ? Loc.T("\n\nLe propriétaire du terminal (kiosque / MDM) ne se définit que sur un terminal sans compte : idéalement juste après un wipe.") : "";
            if (!await ShowCustomDialogAsync(Loc.T("🧰 Appliquer le Master"),
                    Loc.F("« {0} » va être appliqué à {1} terminal(aux) : {2} application(s), {3} réglage(s), Wi-Fi, fichiers et commandes cochés.{4}\n\nLes terminaux ne sont pas effacés : ce qui existe déjà est mis à jour ou laissé tel quel. Continuer ?",
                        m.Name, targets.Count, apps, settings, ownerNote), Loc.T("Appliquer"), Loc.T("Annuler"), false))
                return;

            _masterBusy = true;
            UpdateMasterApplyButton();
            BtnMasterRefresh.IsEnabled = false;
            AnimateNavButton(BtnNavMaster, true);
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🧰 Master « {1} » appliqué à {2} terminal(aux).", DateTime.Now, m.Name, targets.Count));
            int done = 0;
            SetTaskbarProgress(0);
            try
            {
                await Task.WhenAll(targets.Select(async t =>
                {
                    await ApplyMasterToAsync(t, m);
                    done++;
                    SetTaskbarProgress((double)done / targets.Count);
                    TxtMasterSummary.Text = Loc.F("En cours : {0} / {1} terminé(s).", done, targets.Count);
                }));
            }
            finally
            {
                _masterBusy = false;
                AnimateNavButton(BtnNavMaster, false);
                SetTaskbarProgress(null);
                FlashTaskbarIfInBackground();
                if (MainTabControl.SelectedIndex == MasterTabIndex) UpdateNavButtonsUI(BtnNavMaster);
                foreach (var t in targets) t.Check.IsChecked = false;
                BtnMasterRefresh.IsEnabled = true;
                UpdateMasterApplyButton();
            }
            int good = targets.Count(t => t.Outcome == "ok"), warn = targets.Count(t => t.Outcome == "warn"), bad = targets.Count(t => t.Outcome == "bad");
            TxtMasterSummary.Text = Loc.F("Terminé : {0} terminal(aux) complet(s), {1} avec des éléments non appliqués, {2} en échec. Ouvrez « Détail » pour la liste.", good, warn, bad);
            AppendConsole("\n" + TxtMasterSummary.Text);
            if (MainTabControl.SelectedIndex != MasterTabIndex)
                await ShowCustomDialogAsync(Loc.T("🧰 Master appliqué"), TxtMasterSummary.Text, "OK", null, false);
        }

        private void MasterStatus(MasterTarget t, string text, string brush = "Ink")
        {
            t.TxtStatus.Text = text;
            t.TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, brush);
        }

        private void MasterAdd(MasterTarget t, string category, string element, string status, string detail = "")
            => t.Results.Add(new MasterResult { Category = category, Element = element, Status = status, Detail = detail });

        /// <summary>Remet un terminal en état, élément par élément, en notant le résultat de chacun.</summary>
        private async Task ApplyMasterToAsync(MasterTarget t, MasterProfile m)
        {
            t.Results.Clear();
            t.Outcome = "";
            t.Check.IsEnabled = false;
            t.Bar.Visibility = Visibility.Visible;
            t.Bar.Value = 0;
            t.BtnDetail.Visibility = Visibility.Collapsed;
            t.TxtDetail.Visibility = Visibility.Collapsed;
            var apps = m.Apps.Where(a => a.Include && a.Files.Count > 0).ToList();
            int total = apps.Count + 6 + m.Files.Count(f => f.Include) + m.Commands.Count, step = 0;
            void Advance() => t.Bar.Value = Math.Min(100, ++step * 100.0 / Math.Max(1, total));
            string catApps = Loc.T("Applications"), catDis = Loc.T("Applications désactivées"), catPerm = Loc.T("Autorisations"), catSet = Loc.T("Réglages"),
                   catWifi = Loc.T("Wi-Fi"), catHome = Loc.T("Lanceur / propriétaire"), catFiles = Loc.T("Fichiers"), catCmd = Loc.T("Commandes");
            try
            {
                // 1. Applications (avant les autorisations, qui en dépendent).
                foreach (var a in apps)
                {
                    MasterStatus(t, Loc.F("Installation de {0}…", a.Package));
                    var files = a.Files.Select(f => AdbManager.QuoteLocalArg(Path.Combine(_masterDir, f.Replace('/', Path.DirectorySeparatorChar)))).ToList();
                    var res = await _adbManager.ExecuteAsync($"-s {t.Serial} {(files.Count > 1 ? "install-multiple" : "install")} -r {string.Join(" ", files)}", TimeSpan.FromMinutes(5));
                    string outp = (res.StandardOutput ?? "") + (res.StandardError ?? "");
                    if (outp.Contains("Success")) MasterAdd(t, catApps, a.Package, "ok", a.VersionCode.Length > 0 ? "v" + a.VersionCode : "");
                    else MasterAdd(t, catApps, a.Package, "ko", ExplainInstallFailure(outp));
                    Advance();
                }

                // 2 à 4. Applications désactivées, autorisations, réglages : un script exécuté sur le terminal (rapide,
                // même avec des centaines de réglages), une ligne de résultat par élément.
                MasterStatus(t, Loc.T("Applications désactivées, autorisations, réglages…"));
                var script = new StringBuilder();
                foreach (var d in m.DisabledApps.Where(x => x.Include))
                    script.Append($"if pm path {d.Name} >/dev/null 2>&1; then if pm disable-user --user 0 {d.Name} >/dev/null 2>&1; then echo 'DIS|OK|{d.Name}'; else echo 'DIS|KO|{d.Name}'; fi; else echo 'DIS|ABS|{d.Name}'; fi\n");
                foreach (var a in apps.Where(x => x.Permissions.Count > 0))
                    foreach (var p in a.Permissions)
                        script.Append($"if pm grant {a.Package} {p} >/dev/null 2>&1; then echo 'PERM|OK|{a.Package} {p}'; else echo 'PERM|KO|{a.Package} {p}'; fi\n");
                // Réglages : comparés sur le PC (une seule lecture par espace), seuls ceux qui diffèrent sont écrits.
                var wanted = m.Settings.Where(x => x.Include).ToList();
                var current = await ReadMasterSettingsAsync(t.Serial);
                var toWrite = wanted.Where(x => !current.TryGetValue(x.Namespace + "|" + x.Key, out var cv) || cv != x.Value).ToList();
                foreach (var st in toWrite)
                    script.Append($"settings put {st.Namespace} {st.Key} {MasterProfile.ShellQuote(st.Value)} >/dev/null 2>&1\n");
                var lines = await RunMasterScriptAsync(t.Serial, script.ToString());
                int permOk = 0;
                foreach (var l in lines)
                {
                    var parts = l.Split(new[] { '|' }, 3);
                    if (parts.Length < 3) continue;
                    switch (parts[0])
                    {
                        case "DIS":
                            MasterAdd(t, catDis, parts[2], parts[1] == "OK" ? "ok" : parts[1] == "ABS" ? "skip" : "ko",
                                parts[1] == "ABS" ? Loc.T("absente de ce terminal") : parts[1] == "KO" ? Loc.T("refusé par Android") : "");
                            break;
                        case "PERM":
                            if (parts[1] == "OK") permOk++; else MasterAdd(t, catPerm, parts[2], "ko", Loc.T("refusée (autorisation système ou appli absente)"));
                            break;
                    }
                }
                int setSame = wanted.Count - toWrite.Count, setOk = 0;
                if (toWrite.Count > 0)
                {
                    var after = await ReadMasterSettingsAsync(t.Serial);
                    foreach (var st in toWrite)
                    {
                        if (after.TryGetValue(st.Namespace + "|" + st.Key, out var av) && av == st.Value) setOk++;
                        else MasterAdd(t, catSet, $"{st.Namespace} · {st.Key}", "ko", Loc.T("protégé par Android"));
                    }
                }
                int permTotal = apps.Sum(a => a.Permissions.Count), setTotal = wanted.Count;
                if (permTotal > 0) MasterAdd(t, catPerm, Loc.F("{0}/{1} autorisation(s) accordée(s)", permOk, permTotal), permOk == permTotal ? "ok" : "info");
                if (setTotal > 0) MasterAdd(t, catSet, Loc.F("{0} modifié(s), {1} déjà identique(s), {2} protégé(s)", setOk, setSame, setTotal - setOk - setSame), setOk + setSame == setTotal ? "ok" : "info");
                step += 3; Advance();

                // 5. Wi-Fi.
                foreach (var w in m.WifiNetworks.Where(x => x.Include))
                {
                    string type = MasterProfile.WifiConnectType(w.Security);
                    if (t.Sdk > 0 && t.Sdk < 30) { MasterAdd(t, catWifi, w.Ssid, "skip", Loc.T("Android 11 ou plus requis : à saisir à la main")); continue; }
                    if (type.Length == 0) { MasterAdd(t, catWifi, w.Ssid, "skip", Loc.T("réseau d'entreprise ou WEP : à configurer à la main ou par le MDM")); continue; }
                    if (type != "open" && string.IsNullOrEmpty(w.Password)) { MasterAdd(t, catWifi, w.Ssid, "skip", Loc.T("mot de passe non saisi dans le Master")); continue; }
                    MasterStatus(t, Loc.F("Wi-Fi : {0}…", w.Ssid));
                    string cmd = $"cmd wifi connect-network {MasterProfile.ShellQuote(w.Ssid)} {type}" + (type == "open" ? "" : " " + MasterProfile.ShellQuote(w.Password));
                    var res = await RunMasterScriptAsync(t.Serial, cmd + " 2>&1\n");
                    string outp = string.Join(" ", res);
                    bool failed = System.Text.RegularExpressions.Regex.IsMatch(outp, "(?i)exception|error|invalid|unknown|fail");
                    MasterAdd(t, catWifi, w.Ssid, failed ? "ko" : "ok", failed ? outp.Trim() : Loc.T("enregistré"));
                }
                Advance();

                // 6. Lanceur et propriétaire du terminal (après les applis, qui doivent être installées).
                if (m.Launcher?.Include == true)
                {
                    var res = await _adbManager.ExecuteAsync($"-s {t.Serial} shell cmd package set-home-activity {m.Launcher.Name}", TimeSpan.FromSeconds(20));
                    string outp = (res.StandardOutput ?? "") + (res.StandardError ?? "");
                    MasterAdd(t, catHome, Loc.F("Lanceur : {0}", m.Launcher.Name), outp.Contains("Success") ? "ok" : "ko", outp.Contains("Success") ? "" : LastLines(outp, 1));
                }
                if (m.DeviceOwner?.Include == true)
                {
                    var res = await _adbManager.ExecuteAsync($"-s {t.Serial} shell dpm set-device-owner {m.DeviceOwner.Name}", TimeSpan.FromSeconds(30));
                    string outp = (res.StandardOutput ?? "") + (res.StandardError ?? "");
                    bool ok = outp.Contains("Success") || outp.Contains("already");
                    MasterAdd(t, catHome, Loc.F("Propriétaire : {0}", m.DeviceOwner.Name), ok ? "ok" : "ko",
                        ok ? "" : outp.Contains("account") ? Loc.T("refusé : des comptes existent sur le terminal (à faire juste après un wipe)") : LastLines(outp, 1));
                }
                Advance();

                // 7. Fichiers.
                foreach (var f in m.Files.Where(x => x.Include))
                {
                    string local = Path.Combine(_masterDir, f.Local.Replace('/', Path.DirectorySeparatorChar));
                    MasterStatus(t, Loc.F("Fichier : {0}…", Path.GetFileName(local)));
                    await _adbManager.ExecuteAsync($"-s {t.Serial} shell mkdir -p {MasterProfile.ShellQuote(f.DevicePath)}", TimeSpan.FromSeconds(15));
                    var res = await _adbManager.ExecuteAsync($"-s {t.Serial} push {AdbManager.QuoteLocalArg(local)} {AdbManager.QuoteLocalArg(f.DevicePath)}", TimeSpan.FromMinutes(10));
                    MasterAdd(t, catFiles, Path.GetFileName(local) + " → " + f.DevicePath, res.IsSuccess ? "ok" : "ko", res.IsSuccess ? "" : LastLines(res.StandardError + res.StandardOutput, 1));
                    Advance();
                }

                // 8. Commandes libres.
                foreach (var c in m.Commands)
                {
                    MasterStatus(t, Loc.F("Commande : {0}", c));
                    var res = await _adbManager.ExecuteAsync($"-s {t.Serial} {c}", TimeSpan.FromMinutes(2));
                    MasterAdd(t, catCmd, c, res.IsSuccess ? "ok" : "ko", res.IsSuccess ? "" : LastLines(res.StandardError + res.StandardOutput, 1));
                    Advance();
                }

                int ko = t.Results.Count(r => r.Status == "ko");
                t.Outcome = ko == 0 ? "ok" : "warn";
                t.Message = ko == 0 ? Loc.T("Remis en état : tout a été appliqué") : Loc.F("Remis en état, {0} élément(s) non appliqué(s) : voir le détail", ko);
            }
            catch (Exception ex)
            {
                t.Outcome = "bad";
                t.Message = Loc.F("Interrompu : {0}", ex.Message);
                MasterAdd(t, Loc.T("Erreur"), ex.Message, "ko");
            }
            t.Bar.Value = 100;
            if (t.Outcome != "ok") t.Bar.Visibility = Visibility.Collapsed;
            MasterStatus(t, (t.Outcome == "ok" ? "✅ " : t.Outcome == "warn" ? "⚠️ " : "❌ ") + t.Message, t.Outcome == "ok" ? "OkFg" : t.Outcome == "warn" ? "WarnFg" : "BadFg");
            t.TxtDetail.Text = MasterDetailText(t);
            t.BtnDetail.Visibility = Visibility.Visible;
            t.Check.IsEnabled = true;
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🧰 {t.Serial}{(t.Model.Length > 0 ? " (" + t.Model + ")" : "")} : {t.Message}");
        }

        /// <summary>Réglages actuels du terminal, clé « espace|nom ».</summary>
        private async Task<Dictionary<string, string>> ReadMasterSettingsAsync(string serial)
        {
            var d = new Dictionary<string, string>();
            foreach (var ns in new[] { "system", "secure", "global" })
            {
                var r = await _adbManager.ExecuteAsync($"-s {serial} shell settings list {ns}", TimeSpan.FromSeconds(30));
                foreach (var kv in MasterProfile.ParseSettingsList(r.StandardOutput)) d[ns + "|" + kv.Key] = kv.Value;
            }
            return d;
        }

        private static string MasterIcon(string status) => status == "ok" ? "✅" : status == "same" ? "＝" : status == "skip" ? "⏭" : status == "info" ? "ℹ️" : "❌";

        private string MasterDetailText(MasterTarget t)
        {
            var sb = new StringBuilder();
            foreach (var group in t.Results.GroupBy(r => r.Category))
            {
                sb.AppendLine(group.Key);
                foreach (var r in group.OrderBy(r => r.Status == "info" ? 0 : r.Status == "ko" ? 1 : r.Status == "skip" ? 2 : 3))
                    sb.AppendLine($"  {MasterIcon(r.Status)} {r.Element}{(r.Detail.Length > 0 ? " — " + r.Detail : "")}");
            }
            return sb.ToString().TrimEnd();
        }

        private static string ExplainInstallFailure(string output)
        {
            var m = System.Text.RegularExpressions.Regex.Match(output ?? "", @"INSTALL_[A-Z_]+");
            if (!m.Success) return LastLines(output, 1);
            switch (m.Value)
            {
                case "INSTALL_FAILED_VERSION_DOWNGRADE": return Loc.T("une version plus récente est déjà installée");
                case "INSTALL_FAILED_UPDATE_INCOMPATIBLE": return Loc.T("signature différente de la version installée : désinstallez-la d'abord");
                case "INSTALL_FAILED_INSUFFICIENT_STORAGE": return Loc.T("stockage insuffisant");
                case "INSTALL_FAILED_OLDER_SDK": return Loc.T("version d'Android trop ancienne pour cette appli");
                case "INSTALL_FAILED_NO_MATCHING_ABIS": return Loc.T("appli non compatible avec ce processeur");
                default: return m.Value;
            }
        }

        /// <summary>Exécute un script shell sur le terminal (envoyé dans /data/local/tmp) et rend ses lignes de sortie.</summary>
        private async Task<List<string>> RunMasterScriptAsync(string serial, string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return new List<string>();
            string local = Path.Combine(MasterWorkRoot, "script_" + Guid.NewGuid().ToString("N") + ".sh");
            Directory.CreateDirectory(MasterWorkRoot);
            File.WriteAllText(local, script.Replace("\r\n", "\n"), new UTF8Encoding(false));
            string remote = "/data/local/tmp/adbtools_master_" + serial.GetHashCode().ToString("x") + ".sh";
            try
            {
                await _adbManager.ExecuteAsync($"-s {serial} push {AdbManager.QuoteLocalArg(local)} {remote}", TimeSpan.FromSeconds(30));
                var res = await _adbManager.ExecuteAsync($"-s {serial} shell sh {remote}", TimeSpan.FromMinutes(5));
                await _adbManager.ExecuteAsync($"-s {serial} shell rm -f {remote}", TimeSpan.FromSeconds(10));
                return (res.StandardOutput ?? "").Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            }
            finally { try { File.Delete(local); } catch { } }
        }

        // ------------------------------------------------------------------ Bilan Excel

        private void BtnMasterExport_Click(object sender, RoutedEventArgs e)
        {
            var done = _masterTargets.Where(t => t.Results.Count > 0).ToList();
            if (done.Count == 0) { TxtMasterSummary.Text = Loc.T("Aucun terminal remis en état pour l'instant : rien à exporter."); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = Loc.T("Classeur Excel (*.xlsx)|*.xlsx"), FileName = $"Master_{DateTime.Now:yyyyMMdd_HHmm}.xlsx", Title = Loc.T("Exporter le bilan du Master") };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var rows = new List<string[]>();
                var styles = new List<uint>();
                foreach (var t in done)
                    foreach (var r in t.Results)
                    {
                        rows.Add(new[] { t.Serial, t.Model, r.Category, r.Element,
                            r.Status == "ok" ? Loc.T("Appliqué") : r.Status == "same" ? Loc.T("Déjà en place") : r.Status == "skip" ? Loc.T("Ignoré") : r.Status == "info" ? Loc.T("Bilan") : Loc.T("Non appliqué"), r.Detail });
                        styles.Add(r.Status == "ko" ? XlsxExport.StyleBad : r.Status == "skip" ? XlsxExport.StyleWarn : r.Status == "info" ? XlsxExport.StyleNormal : XlsxExport.StyleOk);
                    }
                var title = new List<string>
                {
                    Loc.F("Master « {0} » — {1:dd/MM/yyyy HH:mm}", _master?.Name ?? "", DateTime.Now),
                    Loc.F("Capturé sur {0} {1} (Android {2})", _master?.SourceManufacturer, _master?.SourceModel, _master?.SourceAndroid)
                };
                title.AddRange(done.Select(t => $"{t.Serial} ({t.Model}) : {t.Message}"));
                XlsxExport.Write(dlg.FileName, "Master", title,
                    new[] { "N° de série", "Modèle", "Catégorie", "Élément", "Résultat", "Détail" }.Select(Loc.T).ToList(), rows, i => styles[i]);
                TxtMasterSummary.Text = Loc.F("Bilan exporté : {0}", Path.GetFileName(dlg.FileName));
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
            }
            catch (Exception ex) { TxtMasterSummary.Text = Loc.F("Export impossible : {0}", ex.Message); }
        }
    }
}
