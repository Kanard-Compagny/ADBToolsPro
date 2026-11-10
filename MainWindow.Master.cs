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
            // Analyse avant l'envoi : applis installées (paquet → versionCode) et choix du technicien (paquet → install / skip / downgrade).
            public readonly Dictionary<string, string> Installed = new Dictionary<string, string>();
            public readonly Dictionary<string, string> AppChoice = new Dictionary<string, string>();
            public MasterProfile AnalyzedMaster;
            public bool Analyzing;
            public bool ShowingResults;
            public StackPanel PlanPanel;
            public bool HelperReady;
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
            t.Check.Checked += (s, a) =>
            {
                UpdateMasterApplyButton();
                // Terminal coché : on regarde tout de suite ce qu'il contient déjà, pour choisir avant l'envoi.
                if (_master != null && !_masterBusy && t.AnalyzedMaster != _master) _ = AnalyzeMasterTargetAsync(t);
            };
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
            t.PlanPanel = new StackPanel { Margin = new Thickness(30, 8, 0, 0), Visibility = Visibility.Collapsed };
            t.BtnDetail.Click += (s, a) =>
            {
                // Avant l'envoi : choix par appli ; après : bilan détaillé.
                var panel = t.ShowingResults ? (FrameworkElement)t.TxtDetail : t.PlanPanel;
                bool show = panel.Visibility != Visibility.Visible;
                panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                string word = t.ShowingResults ? Loc.T("Détail") : Loc.T("Applis");
                t.BtnDetail.Content = word + (show ? " ▴" : " ▾");
            };
            Grid.SetColumn(t.BtnDetail, 3); g.Children.Add(t.BtnDetail);

            box.Children.Add(g);
            box.Children.Add(t.PlanPanel);
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
            foreach (var b in new Button[] { BtnMasterAddApk, BtnMasterAddFile, BtnMasterAddFolder, BtnMasterAddWifi, BtnMasterAddStep, BtnMasterSave })
                b.IsEnabled = !_masterBusy && _master != null;
            BtnMasterAddFolder.IsEnabled = BtnMasterAddFolder.IsEnabled && CmbMasterSource.Items.Count > 0;
            BtnMasterOpen.IsEnabled = !_masterBusy;
            BtnMasterNew.IsEnabled = !_masterBusy;
        }

        // ------------------------------------------------------------------ Analyse des terminaux cibles (avant l'envoi)

        /// <summary>Situation d'une appli du Master sur un terminal cible : absent, older, same, newer, present (version inconnue).</summary>
        private static string MasterAppSituation(MasterTarget t, MasterApp a)
        {
            if (!t.Installed.TryGetValue(a.Package, out var have)) return "absent";
            if (have.Length == 0 || a.VersionCode.Length == 0) return "present";
            int c = MasterProfile.CompareVersionCodes(have, a.VersionCode);
            return c < 0 ? "older" : c > 0 ? "newer" : "same";
        }

        /// <summary>Choix pour une appli : install, skip ou downgrade (choix du technicien, sinon choix par défaut selon la situation).</summary>
        private static string MasterAppChoice(MasterTarget t, MasterApp a)
        {
            if (t.AppChoice.TryGetValue(a.Package, out var c)) return c;
            switch (MasterAppSituation(t, a))
            {
                case "same":
                case "newer": return "skip";
                default: return "install";
            }
        }

        /// <summary>Analyse un terminal coché : applis déjà installées et leurs versions, modèle et Android comparés au Master.</summary>
        private async Task AnalyzeMasterTargetAsync(MasterTarget t)
        {
            var m = _master;
            if (m == null || t.Analyzing) return;
            t.Analyzing = true;
            MasterStatus(t, Loc.T("Analyse du terminal…"), "Muted");
            try
            {
                var res = await _adbManager.ExecuteAsync($"-s {t.Serial} shell pm list packages --show-versioncode", TimeSpan.FromSeconds(30));
                var list = MasterProfile.ParsePackagesWithVersion(res.StandardOutput);
                if (list.Count == 0) list = MasterProfile.ParsePackagesWithVersion((await _adbManager.ExecuteAsync($"-s {t.Serial} shell pm list packages", TimeSpan.FromSeconds(30))).StandardOutput);
                t.Installed.Clear();
                foreach (var p in list) t.Installed[p.Key] = p.Value;
                t.AnalyzedMaster = m;
                ShowMasterTargetPlan(t);
            }
            catch (Exception ex)
            {
                MasterStatus(t, Loc.F("Analyse impossible : {0}", ex.Message), "BadFg");
            }
            finally { t.Analyzing = false; }
        }

        /// <summary>Résumé de l'analyse et liste des applis avec, pour chacune, le choix à faire.</summary>
        private void ShowMasterTargetPlan(MasterTarget t)
        {
            var m = _master;
            if (m == null || t.AnalyzedMaster != m) return;
            var apps = m.Apps.Where(a => a.Include && a.Files.Count > 0).ToList();
            int install = apps.Count(a => MasterAppSituation(t, a) == "absent"), update = apps.Count(a => MasterAppSituation(t, a) == "older"),
                same = apps.Count(a => MasterAppSituation(t, a) == "same"), newer = apps.Count(a => MasterAppSituation(t, a) == "newer");
            var notes = new List<string>();
            if (m.SourceModel.Length > 0 && t.Model.Length > 0 && !string.Equals(m.SourceModel, t.Model, StringComparison.OrdinalIgnoreCase))
                notes.Add(Loc.F("modèle différent du terminal de référence ({0})", m.SourceModel));
            if (m.SourceAndroid.Length > 0 && t.Android.Length > 0 && m.SourceAndroid.Split('.')[0] != t.Android.Split('.')[0])
                notes.Add(Loc.F("Android {0} ici, {1} sur le terminal de référence", t.Android, m.SourceAndroid));
            string text = apps.Count == 0 ? Loc.T("Analysé : aucune appli à installer")
                : Loc.F("Analysé : {0} à installer, {1} à mettre à jour, {2} déjà à jour", install, update, same)
                  + (newer > 0 ? Loc.F(", {0} plus récente(s) sur ce terminal", newer) : "");
            if (notes.Count > 0) text += " · ⚠ " + string.Join(" · ", notes);
            MasterStatus(t, (newer > 0 || notes.Count > 0 ? "⚠ " : "✅ ") + text, newer > 0 || notes.Count > 0 ? "WarnFg" : "OkFg");

            t.PlanPanel.Children.Clear();
            if (newer > 0)
                t.PlanPanel.Children.Add(MasterMutedText(Loc.T("Une version plus récente est déjà sur ce terminal : elle est gardée par défaut. Installer la version du Master (plus ancienne) peut être refusé par Android ou faire perdre les données de l'appli."), 11.5));
            foreach (var a in apps)
            {
                string sit = MasterAppSituation(t, a);
                t.Installed.TryGetValue(a.Package, out var have);
                string known = MasterProfile.KnownAppName(a.Package);
                var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = a.Package };
                var label = new TextBlock { Text = known.Length > 0 ? known : a.Package, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                label.SetResourceReference(TextBlock.ForegroundProperty, sit == "newer" ? "WarnFg" : "Ink");
                names.Children.Add(label);
                string master = a.VersionCode.Length > 0 ? "v" + a.VersionCode : "";
                names.Children.Add(MasterSub(
                    sit == "absent" ? Loc.T("absente de ce terminal")
                    : sit == "older" ? Loc.F("v{0} sur ce terminal, {1} dans le Master", have, master)
                    : sit == "same" ? Loc.F("déjà installée dans la même version ({0})", master)
                    : sit == "newer" ? Loc.F("v{0} sur ce terminal, plus récente que le Master ({1})", have, master)
                    : Loc.T("déjà installée (version non lue)")));
                g.Children.Add(names);

                var options = new List<(string Value, string Label)>();
                switch (sit)
                {
                    case "absent": options.Add(("install", Loc.T("Installer"))); options.Add(("skip", Loc.T("Ne pas installer"))); break;
                    case "older": options.Add(("install", Loc.T("Mettre à jour"))); options.Add(("skip", Loc.T("Garder la version du terminal"))); break;
                    case "same": options.Add(("skip", Loc.T("Ignorer (déjà à jour)"))); options.Add(("install", Loc.T("Réinstaller quand même"))); break;
                    case "newer": options.Add(("skip", Loc.T("Garder la version du terminal (conseillé)"))); options.Add(("downgrade", Loc.T("Installer la version du Master (plus ancienne)"))); break;
                    default: options.Add(("install", Loc.T("Réinstaller"))); options.Add(("skip", Loc.T("Ne pas installer"))); break;
                }
                var cmb = new ComboBox { Height = 26, FontSize = 11.5, VerticalContentAlignment = VerticalAlignment.Center };
                string current = MasterAppChoice(t, a);
                foreach (var o in options)
                {
                    var it = new ComboBoxItem { Content = o.Label, Tag = o.Value };
                    cmb.Items.Add(it);
                    if (o.Value == current) cmb.SelectedItem = it;
                }
                if (cmb.SelectedItem == null) cmb.SelectedIndex = 0;
                var app = a;
                cmb.SelectionChanged += (s, e) => t.AppChoice[app.Package] = (cmb.SelectedItem as ComboBoxItem)?.Tag as string ?? "skip";
                Grid.SetColumn(cmb, 1); g.Children.Add(cmb);
                t.PlanPanel.Children.Add(g);
            }
            if (apps.Count == 0) t.PlanPanel.Children.Add(MasterMutedText(Loc.T("Aucune appli cochée dans le Master."), 11.5));
            t.ShowingResults = false;
            t.BtnDetail.Content = t.PlanPanel.Visibility == Visibility.Visible ? Loc.T("Applis ▴") : Loc.T("Applis ▾");
            t.BtnDetail.Visibility = Visibility.Visible;
            if (newer > 0 && t.PlanPanel.Visibility != Visibility.Visible)
            {
                t.PlanPanel.Visibility = Visibility.Visible;
                t.TxtDetail.Visibility = Visibility.Collapsed;
                t.BtnDetail.Content = Loc.T("Applis ▴");
            }
        }

        /// <summary>Le Master a changé (capture, ouverture, applis cochées…) : les analyses des terminaux cochés sont refaites.</summary>
        private void RefreshMasterTargetPlans()
        {
            foreach (var t in _masterTargets.Where(x => x.Check?.IsChecked == true && !_masterBusy))
            {
                if (t.AnalyzedMaster == _master && _master != null) ShowMasterTargetPlan(t);
                else if (_master != null) _ = AnalyzeMasterTargetAsync(t);
            }
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
            var m = new MasterProfile { FormatVersion = MasterProfile.CurrentFormat };
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
                    var info = MasterProfile.ParsePackageInfo(dump.StandardOutput);
                    app.Installer = info.Installer;
                    app.VersionName = info.VersionName;
                    app.Origin = MasterProfile.AppOrigin(info.Flags, info.Installer, p.Key, info.CodePath);
                    // Applis installées par le MDM ou à la main : cochées. Celles du fabricant, de Google ou du Play Store
                    // sont déjà sur les terminaux (ou y reviennent seules) : décochées, l'analyse des cibles dira le reste.
                    app.Include = app.Files.Count > 0 && (app.Origin == "mdm" || app.Origin == "manual");
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
                            // Réglages courants (catalogue) cochés ; réglages techniques gardés mais décochés.
                            m.Settings.Add(new MasterSetting { Namespace = ns, Key = kv.Key, Value = kv.Value, Include = MasterSettingsCatalog.Find(ns, kv.Key) != null });
                }

                // Langue et fuseau horaire : propriétés du système, pas des réglages « settings ».
                foreach (var sp in await ReadMasterSpecialsAsync(serial))
                    if (sp.Value.Length > 0) m.Settings.Add(new MasterSetting { Namespace = MasterSettingsCatalog.SpecialNamespace, Key = sp.Key, Value = sp.Value, Include = true });

                if (sdk >= 30)
                    m.WifiNetworks = MasterProfile.ParseWifiNetworks((await _adbManager.ExecuteAsync($"-s {serial} shell cmd wifi list-networks", TimeSpan.FromSeconds(15))).StandardOutput);

                string home = MasterProfile.ParseHomeActivity((await _adbManager.ExecuteAsync(
                    $"-s {serial} shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME", TimeSpan.FromSeconds(15))).StandardOutput);
                if (home.Length > 0) m.Launcher = new MasterItem { Name = home };

                string owner = MasterProfile.ParseDeviceOwner((await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys device_policy", TimeSpan.FromSeconds(20))).StandardOutput);
                if (owner.Length > 0) m.DeviceOwner = new MasterItem { Name = owner };
                m.ApplyManagedOrigins();

                // Comptes (Google ou autres) : ils ne se recopient pas par ADB, ils deviennent des étapes à faire à la main.
                var accounts = MasterProfile.ParseAccounts((await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys account", TimeSpan.FromSeconds(20))).StandardOutput);
                foreach (var acc in accounts.Where(x => !MasterProfile.IsMdmManagedAccount(x.Key, x.Value)))
                {
                    bool google = acc.Value == "com.google";
                    m.Steps.Add(new MasterStep
                    {
                        Title = google ? Loc.F("Ajouter le compte Google {0}", acc.Key) : Loc.F("Recréer le compte {0} ({1})", acc.Key, acc.Value),
                        Detail = google ? Loc.T("le mot de passe ne se récupère pas : à saisir sur le terminal") : Loc.T("depuis l'appli concernée"),
                        Screen = google ? "google" : "accounts",
                        // Un compte empêche de définir le propriétaire du terminal : on l'ajoute après (fin de la remise en état).
                    });
                }
                // Wi-Fi d'entreprise (802.1X) ou WEP : non réinjectables, à configurer à la main.
                foreach (var w in m.WifiNetworks.Where(x => MasterProfile.WifiConnectType(x.Security).Length == 0))
                {
                    w.Include = false;
                    m.Steps.Add(new MasterStep { Title = Loc.F("Configurer le Wi-Fi « {0} » ({1})", w.Ssid, w.Security), Detail = Loc.T("identifiant, certificat… ou par le MDM"), Screen = "wifi" });
                }

                _master = m;
                _masterCmdBox = null;
                _masterView = "";
                TxtMasterName.Text = m.Name;
                RenderMasterContent();
                Step(Loc.F("Capture terminée : {0} application(s), {1} réglage(s), {2} réseau(x) Wi-Fi, {3} étape(s) à faire à la main. Vérifiez le contenu, complétez-le (mots de passe, éléments non récupérés) puis enregistrez le Master.",
                    m.Apps.Count, m.Settings.Count, m.WifiNetworks.Count, m.Steps.Count));
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

        // ------------------------------------------------------------------ Contenu du Master (à vérifier, compléter, modifier)

        /// <summary>Une ligne du contenu : case à cocher, zone de saisie éventuelle, alerte « à compléter », modifier, retirer.</summary>
        private sealed class MasterRow
        {
            public string Label = "";
            public string Sub = "";            // deuxième ligne discrète (nom technique, version…)
            public Func<bool> Get;
            public Action<bool> Set;
            public Func<string> Warn;          // texte orange « à compléter » (vide = rien)
            public Func<Task> Edit;            // ✏️
            public Action Remove;              // ✕
            public FrameworkElement Extra;     // ex. mot de passe Wi-Fi, écran d'une étape
            public bool Locked;                // case non cochable (élément non défini)
        }

        private string _masterView = "";
        private string _masterSettingsFilter = "";
        private bool _masterStepFormOpen;
        private bool _masterWifiFormOpen;
        private string _masterSettingsTheme = MasterSettingsCatalog.ThemeScreen;
        private StackPanel _masterSubHeader;
        private readonly List<(TextBlock Label, Func<string> Warn)> _masterWarns = new List<(TextBlock, Func<string>)>();
        private TextBlock _masterPendingBanner;
        private WrapPanel _masterTabs;
        private StackPanel _masterViewHost;

        // Catégories du contenu, dans l'ordre des onglets.
        private static readonly string[] MasterViews = { "steps", "apps", "settings", "wifi", "home", "files", "commands" };

        /// <summary>Ce qui reste à compléter avant l'envoi (mots de passe Wi-Fi, APK non récupérés…), pour le bandeau et la confirmation.</summary>
        private List<string> MasterPendingItems()
        {
            var list = new List<string>();
            if (_master == null) return list;
            int pwd = MasterMissingPasswords();
            if (pwd > 0) list.Add(Loc.F("{0} mot(s) de passe Wi-Fi", pwd));
            int apk = _master.Apps.Count(a => a.Files.Count == 0);
            if (apk > 0) list.Add(Loc.F("{0} appli(s) non récupérée(s) (APK à ajouter depuis le PC)", apk));
            int steps = _master.Steps.Count(s => s.Include);
            if (steps > 0) list.Add(Loc.F("{0} étape(s) à faire à la main sur chaque terminal", steps));
            return list;
        }

        private int MasterMissingPasswords() => _master == null ? 0 : _master.WifiNetworks.Count(w =>
            w.Include && MasterProfile.WifiConnectType(w.Security) is string ty && (ty == "wpa2" || ty == "wpa3") && string.IsNullOrEmpty(w.Password));

        private void RefreshMasterPending()
        {
            foreach (var w in _masterWarns)
            {
                string text = w.Warn();
                w.Label.Text = text;
                w.Label.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_masterPendingBanner != null)
            {
                var pending = MasterPendingItems();
                _masterPendingBanner.Text = pending.Count == 0 ? Loc.T("✅ Rien à compléter : le Master est prêt à être appliqué.")
                    : Loc.F("⚠️ À compléter ou à vérifier avant l'envoi : {0}.", string.Join(", ", pending));
                _masterPendingBanner.SetResourceReference(TextBlock.ForegroundProperty, pending.Count == 0 ? "OkFg" : "WarnFg");
                if (_masterPendingBanner.Parent is Border box)
                {
                    box.SetResourceReference(Border.BackgroundProperty, pending.Count == 0 ? "OkBg" : "WarnBg");
                    box.SetResourceReference(Border.BorderBrushProperty, pending.Count == 0 ? "OkFg" : "Action");
                }
            }
            RefreshMasterTargetPlans();
            if (_masterTabs != null)
                foreach (RadioButton rb in _masterTabs.Children)
                {
                    rb.Content = MasterTabLabel((string)rb.Tag);
                    rb.Style = (Style)FindResource(MasterTabAlert((string)rb.Tag) ? "AlertPillStyle" : "SettingsPillStyle");
                }
        }

        /// <summary>Libellé d'un onglet, avec le nombre d'éléments cochés et ⚠ s'il reste quelque chose à compléter.</summary>
        private string MasterTabLabel(string view)
        {
            var m = _master;
            switch (view)
            {
                case "steps": return Loc.F("✋ À faire à la main · {0}", m.Steps.Count(x => x.Include));
                case "apps": return Loc.F("📦 Applications · {0}", m.Apps.Count(x => x.Include)) + (m.Apps.Any(a => a.Files.Count == 0) ? " ⚠" : "");
                case "settings": return Loc.F("⚙️ Réglages · {0}", m.Settings.Count(x => x.Include));
                case "wifi": return Loc.F("📶 Wi-Fi · {0}", m.WifiNetworks.Count(x => x.Include)) + (MasterMissingPasswords() > 0 ? " ⚠" : "");
                case "home": return Loc.T("🏠 Lanceur et MDM");
                case "files": return Loc.F("📁 Fichiers · {0}", m.Files.Count(x => x.Include));
                default: return Loc.F("⌨️ Commandes · {0}", m.Commands.Count);
            }
        }

        /// <summary>Onglet à mettre en évidence (orange) : quelque chose y reste à compléter.</summary>
        private bool MasterTabAlert(string view)
        {
            if (_master == null) return false;
            switch (view)
            {
                case "wifi": return MasterMissingPasswords() > 0;
                case "apps": return _master.Apps.Any(a => a.Files.Count == 0);
                default: return false;
            }
        }

        /// <summary>D'où vient l'appli sur le terminal de référence, en clair.</summary>
        private static string MasterOriginText(MasterApp a)
        {
            if (a.FromPc) return Loc.T("ajoutée depuis le PC");
            switch (a.Origin)
            {
                case "system": return Loc.T("appli système du terminal (mise à jour)");
                case "vendor": return Loc.T("livrée avec le terminal (fabricant ou Google)");
                case "store": return Loc.T("installée par le Play Store");
                case "mdmstore": return Loc.T("poussée par le MDM (Play Store d'entreprise)");
                case "owner": return Loc.T("agent MDM, propriétaire du terminal : essentielle");
                case "mdm": return Loc.F("installée par le MDM{0}", MasterProfile.KnownAppName(a.Installer) is string n && n.Length > 0 ? " (" + n + ")" : "");
                case "manual": return Loc.T("installée à la main (APK, ADB)");
                default: return "";
            }
        }

        /// <summary>Saisie d'une valeur (null si annulé).</summary>
        private async Task<string> MasterPromptAsync(string title, string message, string value, string confirm = null)
        {
            if (!await ShowCustomDialogAsync(title, message, confirm ?? Loc.T("Valider"), Loc.T("Annuler"), false, true, value ?? "")) return null;
            return TxtDialogInput.Text;
        }

        /// <summary>Redessine le contenu après une modification.</summary>
        private void RerenderMaster()
        {
            SyncMasterFromUi();
            RenderMasterContent();
        }

        private void RenderMasterContent()
        {
            MasterContent.Children.Clear();
            MasterHeader.Children.Clear();
            _masterWarns.Clear();
            _masterPendingBanner = null;
            _masterTabs = null;
            if (_master == null) return;
            var m = _master;
            TxtMasterInfo.Text = m.SourceModel.Length > 0
                ? Loc.F("Capturé sur {0} {1} (Android {2}, {3}) le {4:dd/MM/yyyy HH:mm}.", m.SourceManufacturer, m.SourceModel, m.SourceAndroid, m.SourceBuild, m.CreatedAt)
                : Loc.T("Master créé à la main.");

            // En-tête fixe (hors défilement) : bandeau, onglets, puis sous-en-tête propre à l'onglet (thèmes des réglages…).
            _masterPendingBanner = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            MasterHeader.Children.Add(new Border { Child = _masterPendingBanner, BorderThickness = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 4, 0, 10) });
            if (!MasterViews.Contains(_masterView))
                _masterView = m.Steps.Any() ? "steps" : m.SourceModel.Length == 0 ? "settings" : "apps";
            _masterTabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            foreach (var v in MasterViews)
            {
                var rb = new RadioButton { GroupName = "MasterView", Tag = v, Content = MasterTabLabel(v), Style = (Style)FindResource(MasterTabAlert(v) ? "AlertPillStyle" : "SettingsPillStyle"), IsChecked = v == _masterView };
                rb.Checked += (s, a) =>
                {
                    SyncMasterFromUi();
                    _masterView = (string)((RadioButton)s).Tag;
                    RenderMasterView();
                    MasterScroll.ScrollToTop();
                };
                _masterTabs.Children.Add(rb);
            }
            MasterHeader.Children.Add(_masterTabs);
            _masterSubHeader = new StackPanel();
            MasterHeader.Children.Add(_masterSubHeader);
            var line = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 0) };
            line.SetResourceReference(Border.BackgroundProperty, "Line");
            MasterHeader.Children.Add(line);
            _masterViewHost = new StackPanel();
            MasterContent.Children.Add(_masterViewHost);
            RenderMasterView();
        }

        /// <summary>Contenu de l'onglet choisi.</summary>
        private void RenderMasterView()
        {
            if (_master == null || _masterViewHost == null) return;
            var m = _master;
            _masterViewHost.Children.Clear();
            _masterSubHeader?.Children.Clear();
            _masterWarns.Clear();
            if (_masterView != "steps") _masterStepFormOpen = false;
            if (_masterView != "wifi") _masterWifiFormOpen = false;
            switch (_masterView)
            {
                case "steps":
                    AddMasterSection(Loc.T("✋ À faire à la main"),
                        Loc.T("Ce qu'ADB ne peut pas réinjecter : compte Google (le mot de passe ne se transmet pas), Wi-Fi d'entreprise, certificat, enrôlement MDM avec identifiant… À la fin de la remise en état, l'écran choisi s'ouvre sur le terminal et l'étape est rappelée dans le bilan."),
                        m.Steps.Select(st => new MasterRow
                        {
                            Label = st.Title, Sub = st.Detail,
                            Get = () => st.Include, Set = v => { st.Include = v; RefreshMasterPending(); },
                            Edit = async () =>
                            {
                                string t = await MasterPromptAsync(Loc.T("✋ Étape à faire à la main"), Loc.T("Ce qu'il faut faire (ex. Ajouter le compte Google atelier@exemple.fr) :"), st.Title);
                                if (t == null || t.Trim().Length == 0) return;
                                string d = await MasterPromptAsync(Loc.T("✋ Étape à faire à la main"), Loc.T("Précision pour le technicien (facultatif) :"), st.Detail);
                                st.Title = t.Trim(); if (d != null) st.Detail = d.Trim();
                                RerenderMaster();
                            },
                            Remove = () => { m.Steps.Remove(st); RerenderMaster(); },
                            Extra = MasterScreenPicker(st),
                        }).ToList(),
                        _masterStepFormOpen ? null : (Func<Task>)(() => { _masterStepFormOpen = true; RenderMasterView(); return Task.CompletedTask; }), Loc.T("＋ Ajouter une étape"));
                    if (_masterStepFormOpen) AddMasterStepForm();
                    break;

                case "apps":
                    AddMasterSection(Loc.T("📦 Applications à réinstaller"),
                        Loc.T("Seules les applis installées par le MDM ou à la main sont cochées : celles livrées avec le terminal y sont déjà. Avant l'envoi, chaque terminal coché est analysé : une appli déjà présente dans la même version est ignorée, et vous choisissez quoi faire quand sa version est différente. Les données internes des applis ne sont pas copiées (rôle du MDM)."),
                        m.Apps.Select(a =>
                        {
                            string known = MasterProfile.KnownAppName(a.Package);
                            return new MasterRow
                            {
                                Label = known.Length > 0 ? known : a.Package,
                                Sub = string.Join(" · ", new[] { MasterOriginText(a), known.Length > 0 ? a.Package : "",
                                        a.VersionName.Length > 0 ? "v" + a.VersionName : a.VersionCode.Length > 0 ? "v" + a.VersionCode : "",
                                        a.Permissions.Count > 0 ? Loc.F("{0} autorisation(s)", a.Permissions.Count) : "" }.Where(x => x.Length > 0)),
                                Get = () => a.Include, Set = v => { a.Include = v; RefreshMasterPending(); },
                                Locked = a.Files.Count == 0,
                                Warn = () => a.Files.Count == 0 ? Loc.T("APK non récupéré : ajoutez-le depuis le PC") : "",
                                Remove = () => { m.Apps.Remove(a); RerenderMaster(); },
                            };
                        }).ToList(),
                        () => { BtnMasterAddApk_Click(null, null); return Task.CompletedTask; }, Loc.T("＋ APK du PC"));
                    AddMasterSection(Loc.T("🚫 Applications à désactiver"),
                        Loc.T("Applications (souvent système) désactivées sur le terminal de référence : elles seront désactivées aussi."),
                        m.DisabledApps.Select(d =>
                        {
                            string known = MasterProfile.KnownAppName(d.Name);
                            return new MasterRow
                            {
                                Label = known.Length > 0 ? known : d.Name, Sub = known.Length > 0 ? d.Name : "",
                                Get = () => d.Include, Set = v => { d.Include = v; RefreshMasterPending(); },
                                Remove = () => { m.DisabledApps.Remove(d); RerenderMaster(); },
                            };
                        }).ToList(),
                        async () =>
                        {
                            string p = await MasterPromptAsync(Loc.T("🚫 Application à désactiver"), Loc.T("Nom du paquet (ex. com.android.chrome) :"), "", Loc.T("Ajouter"));
                            if (p == null || !System.Text.RegularExpressions.Regex.IsMatch(p.Trim(), @"^[A-Za-z0-9_.]+$")) return;
                            m.DisabledApps.RemoveAll(x => x.Name == p.Trim());
                            m.DisabledApps.Add(new MasterItem { Name = p.Trim() });
                            RerenderMaster();
                        }, Loc.T("＋ Ajouter"));
                    break;

                case "settings":
                    RenderMasterSettingsView();
                    break;

                case "wifi":
                    AddMasterSection(Loc.T("📶 Réseaux Wi-Fi"),
                        Loc.T("Android ne donne pas les mots de passe : saisissez-les ici (ils sont enregistrés dans le Master, gardez le fichier en lieu sûr). Comme Android n'autorise plus l'ajout d'un réseau par câble, la mini appli « ADB Tools Helper » (sans icône) est installée pour les enregistrer et reste sur le terminal. En cas de refus, un QR code à scanner s'affiche. Un réseau d'entreprise (802.1X) passe en étape à faire à la main."),
                        m.WifiNetworks.Select(w => MasterWifiRow(m, w)).ToList(),
                        _masterWifiFormOpen ? null : (Func<Task>)(() => { _masterWifiFormOpen = true; RenderMasterView(); return Task.CompletedTask; }), Loc.T("＋ Ajouter un réseau"));
                    if (_masterWifiFormOpen) AddMasterWifiForm();
                    break;

                case "home":
                    AddMasterSection(Loc.T("🏠 Lanceur et propriétaire du terminal"),
                        Loc.T("Le propriétaire du terminal (kiosque / MDM) ne peut être défini que sur un terminal sans compte, juste après un wipe. ✏️ pour le définir ou le corriger."),
                        new List<MasterRow>
                        {
                            MasterSingleRow(Loc.T("Lanceur par défaut"), () => m.Launcher, v => m.Launcher = v, Loc.T("Lanceur par défaut (paquet/activité, ex. com.zebra.launcher/.Main) :")),
                            MasterSingleRow(Loc.T("Propriétaire du terminal (kiosque / MDM)"), () => m.DeviceOwner, v => m.DeviceOwner = v, Loc.T("Composant administrateur (paquet/classe, ex. net.soti.mobicontrol.androidwork/net.soti.mobicontrol.admin.DeviceAdminAdapter) :")),
                        }, null, null);
                    break;

                case "files":
                    AddMasterSection(Loc.T("📁 Fichiers à déposer"),
                        Loc.T("Dossiers récupérés sur le terminal de référence et fichiers ajoutés depuis le PC (ex. configuration DataWedge). ✏️ pour changer la destination."),
                        m.Files.Select(f => new MasterRow
                        {
                            Label = $"{(f.IsFolder ? "📁" : "📄")} {Path.GetFileName(f.Local.TrimEnd('/'))}", Sub = "→ " + f.DevicePath,
                            Get = () => f.Include, Set = v => { f.Include = v; RefreshMasterPending(); },
                            Edit = async () =>
                            {
                                string d = await MasterPromptAsync(Loc.T("📄 Destination sur le terminal"), Loc.T("Dossier du terminal où déposer ce fichier. Pour une configuration DataWedge (Zebra) : /enterprise/device/settings/datawedge/autoimport/"), f.DevicePath);
                                if (d == null || d.Trim().Length == 0) return;
                                f.DevicePath = d.Trim().EndsWith("/") ? d.Trim() : d.Trim() + "/";
                                RerenderMaster();
                            },
                            Remove = () => { m.Files.Remove(f); RerenderMaster(); },
                        }).ToList(),
                        () => { BtnMasterAddFile_Click(null, null); return Task.CompletedTask; }, Loc.T("＋ Fichier du PC"),
                        CmbMasterSource.Items.Count > 0 ? (Func<Task>)(() => { BtnMasterAddFolder_Click(null, null); return Task.CompletedTask; }) : null, Loc.T("＋ Dossier du terminal"));
                    break;

                default:
                    AddMasterSection(Loc.T("⌨️ Commandes ADB"), Loc.T("Passées en dernier, une par ligne, sans « adb » ni « -s ». Ex. : shell settings put global stay_on_while_plugged_in 3"), new List<MasterRow>(), null, null);
                    var box = new TextBox { AcceptsReturn = true, MinHeight = 120, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(6, 4, 6, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = string.Join(Environment.NewLine, m.Commands), Margin = new Thickness(0, 4, 0, 0) };
                    box.TextChanged += (s, a) =>
                    {
                        m.Commands = (box.Text ?? "").Replace("\r", "").Split('\n').Select(NormalizeMassOsCommand).Where(c => c.Length > 0).ToList();
                        RefreshMasterPending();
                    };
                    _masterViewHost.Children.Add(box);
                    break;
            }
            RefreshMasterPending();
            UpdateMasterApplyButton();
        }

        // ------------------------------------------------------------------ Réglages : formulaire par thème

        private const string MasterTechnicalTheme = "__tech";

        /// <summary>
        /// Formulaire par thème, comme les Paramètres d'Android : chaque réglage connu est affiché, « Ne pas modifier » (le
        /// terminal garde sa valeur) ou une valeur imposée. Les réglages techniques capturés ont leur propre onglet.
        /// </summary>
        private void RenderMasterSettingsView()
        {
            var m = _master;
            var advanced = m.Settings.Where(st => MasterSettingsCatalog.Find(st.Namespace, st.Key) == null).ToList();
            if (_masterSettingsTheme != MasterTechnicalTheme && !MasterSettingsCatalog.Themes.Contains(_masterSettingsTheme))
                _masterSettingsTheme = MasterSettingsCatalog.ThemeScreen;

            // Sous-en-tête fixe : thèmes et recherche.
            var themes = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            bool syncingThemes = false;
            foreach (var th in MasterSettingsCatalog.Themes.Concat(advanced.Count > 0 ? new[] { MasterTechnicalTheme } : new string[0]))
            {
                int imposed = th == MasterTechnicalTheme ? advanced.Count(st => st.Include)
                    : m.Settings.Count(st => st.Include && MasterSettingsCatalog.Find(st.Namespace, st.Key)?.Theme == th);
                string name = th == MasterTechnicalTheme ? Loc.T("Techniques") : Loc.T(th);
                var rb = new RadioButton { GroupName = "MasterSettingsTheme", Tag = th, Content = imposed > 0 ? $"{name} · {imposed}" : name,
                    Style = (Style)FindResource("FilterChipStyle"), Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(0, 0, 6, 6), FontSize = 12,
                    IsChecked = th == _masterSettingsTheme && _masterSettingsFilter.Trim().Length == 0 };
                rb.Checked += (s, a) =>
                {
                    if (syncingThemes) return;
                    _masterSettingsTheme = (string)((RadioButton)s).Tag; _masterSettingsFilter = ""; RenderMasterView(); MasterScroll.ScrollToTop();
                };
                themes.Children.Add(rb);
            }
            _masterSubHeader.Children.Add(themes);
            var search = new TextBox { Height = 28, Margin = new Thickness(0, 0, 0, 6), VerticalContentAlignment = VerticalAlignment.Center, Tag = Loc.T("Rechercher un réglage dans tous les thèmes (veille, volume, Wi-Fi, langue…)"), Style = (Style)FindResource("PlaceholderTextBoxStyle"), Text = _masterSettingsFilter };
            _masterSubHeader.Children.Add(search);

            var host = _masterViewHost;
            var body = new StackPanel();
            host.Children.Add(body);
            void Fill()
            {
                body.Children.Clear();
                string f = _masterSettingsFilter.Trim();
                bool Match(string label, string key) => label.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0 || key.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

                if (f.Length == 0 && _masterSettingsTheme == MasterTechnicalTheme)
                {
                    FillTechnical(body, advanced, "");
                    return;
                }
                var themesToShow = f.Length > 0 ? MasterSettingsCatalog.Themes : new[] { _masterSettingsTheme };
                int shown = 0;
                foreach (var th in themesToShow)
                {
                    var defs = MasterSettingsCatalog.All.Where(d => d.Theme == th && (f.Length == 0 || Match(Loc.T(d.Label), d.Key))).ToList();
                    if (defs.Count == 0) continue;
                    var head = new Grid { Margin = new Thickness(0, 12, 0, 2) };
                    head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    head.Children.Add(MasterHeading(Loc.T(th), 13.5));
                    var reset = new Button { Content = Loc.T("Ne rien imposer dans ce thème"), Style = (Style)FindResource("SettingsLinkStyle"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
                    var theme = th;
                    reset.Click += (s, a) =>
                    {
                        foreach (var st in m.Settings.Where(x => MasterSettingsCatalog.Find(x.Namespace, x.Key)?.Theme == theme)) st.Include = false;
                        RenderMasterView();
                    };
                    Grid.SetColumn(reset, 1); head.Children.Add(reset);
                    body.Children.Add(head);
                    if (f.Length == 0)
                        body.Children.Add(MasterMutedText(Loc.T("« Ne pas modifier » : le terminal garde sa propre valeur. Une valeur choisie est imposée à chaque terminal remis en état."), 11.5));
                    foreach (var d in defs) { body.Children.Add(MasterSettingFormRow(d)); shown++; }
                }
                if (f.Length > 0)
                {
                    var adv = advanced.Where(st => Match(Loc.T(MasterSettingsGlossary.Describe(st.Key).Label), st.Key)).ToList();
                    if (adv.Count > 0) FillTechnical(body, adv, f);
                    else if (shown == 0) body.Children.Add(MasterMutedText(Loc.T("Aucun réglage ne correspond à la recherche."), 12));
                }
            }
            search.TextChanged += (s, a) =>
            {
                _masterSettingsFilter = search.Text;
                syncingThemes = true;
                foreach (RadioButton rb in themes.Children) rb.IsChecked = _masterSettingsFilter.Trim().Length == 0 && (string)rb.Tag == _masterSettingsTheme;
                syncingThemes = false;
                Fill();
            };
            Fill();
        }

        private readonly HashSet<string> _masterTechOpen = new HashSet<string>();

        /// <summary>
        /// Réglages techniques capturés hors catalogue, expliqués : nom en français, valeur lisible, regroupés par catégorie
        /// (son, écran, verrouillage…). Les informations internes à Android sont à part : aucune raison de les recopier.
        /// </summary>
        private void FillTechnical(StackPanel body, List<MasterSetting> list, string filter)
        {
            var m = _master;
            body.Children.Add(MasterHeading(Loc.F("Réglages techniques ({0})", list.Count), 13.5, new Thickness(0, 12, 0, 2)));
            body.Children.Add(MasterMutedText(Loc.T("Autres réglages capturés sur le terminal de référence, expliqués et rangés par catégorie. Non cochés par défaut : cochez ceux à imposer, ✏️ pour changer la valeur. Survolez un nom pour voir sa clé Android."), 11.5));
            if (filter.Length == 0)
            {
                var custom = new Button { Content = Loc.T("＋ Réglage technique (nom exact)…"), Style = (Style)FindResource("SettingsLinkStyle"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
                custom.Click += async (s, a) =>
                {
                    string v = await MasterPromptAsync(Loc.T("⚙️ Réglage Android"), Loc.T("Espace, nom et valeur. Ex. : system screen_off_timeout=600000 ou global stay_on_while_plugged_in=3"), "system ", Loc.T("Ajouter"));
                    if (v == null) return;
                    var st = MasterProfile.ParseSettingInput(v);
                    if (st == null) { TxtMasterSummary.Text = Loc.T("Réglage non reconnu : écrivez par exemple « system screen_off_timeout=600000 »."); return; }
                    m.Settings.RemoveAll(x => x.Namespace == st.Namespace && x.Key == st.Key);
                    m.Settings.Insert(0, st);
                    _masterSettingsTheme = MasterSettingsCatalog.Find(st.Namespace, st.Key)?.Theme ?? MasterTechnicalTheme;
                    _masterTechOpen.Add(MasterSettingsGlossary.Describe(st.Key).Category);
                    RenderMasterView();
                };
                body.Children.Add(custom);
            }

            var groups = list.Select(st => (St: st, D: MasterSettingsGlossary.Describe(st.Key)))
                .GroupBy(x => x.D.Category)
                .OrderBy(g => Array.IndexOf(MasterSettingsGlossary.Categories, g.Key));
            foreach (var g in groups)
            {
                string cat = g.Key;
                var items = g.OrderBy(x => Loc.T(x.D.Label), StringComparer.CurrentCultureIgnoreCase).ToList();
                bool internalCat = cat == MasterSettingsGlossary.CatInternal;
                bool open = filter.Length > 0 || _masterTechOpen.Contains(cat);
                int checkedCount = items.Count(x => x.St.Include);

                var head = new Grid { Margin = new Thickness(0, 10, 0, 2) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var all = new CheckBox { Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tout cocher / tout décocher"),
                    IsChecked = checkedCount == items.Count ? true : checkedCount == 0 ? false : (bool?)null };
                all.Click += (s, a) =>
                {
                    bool v = all.IsChecked == true;
                    foreach (var x in items) x.St.Include = v;
                    RenderMasterView();
                };
                head.Children.Add(all);
                var toggle = new Button { Style = (Style)FindResource("SettingsLinkStyle"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Content = (open ? "▾ " : "▸ ") + Loc.T(cat) + "  ·  " + (checkedCount > 0 ? Loc.F("{0} réglage(s), {1} coché(s)", items.Count, checkedCount) : Loc.F("{0} réglage(s)", items.Count)) };
                toggle.Click += (s, a) =>
                {
                    if (!_masterTechOpen.Remove(cat)) _masterTechOpen.Add(cat);
                    RenderMasterView();
                };
                Grid.SetColumn(toggle, 1); head.Children.Add(toggle);
                body.Children.Add(head);
                if (internalCat)
                {
                    var why = MasterMutedText(Loc.T("Informations tenues à jour par Android ou les services Google (journaux, compteurs, migrations…) : aucun effet visible, inutile de les recopier."), 11.5);
                    why.Margin = new Thickness(30, 0, 0, 2);
                    body.Children.Add(why);
                }
                if (!open) continue;

                var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
                foreach (var x in items.Take(200))
                {
                    var st = x.St;
                    string label = x.D.Label == MasterSettingsGlossary.Humanize(st.Key) ? x.D.Label : Loc.T(x.D.Label);
                    string shown = Loc.T(MasterSettingsGlossary.DisplayValue(st.Key, st.Value));
                    var row = MasterRowElement(new MasterRow
                    {
                        Label = label,
                        Sub = Loc.F("valeur : {0}", shown) + "   ·   " + st.Namespace + " · " + st.Key,
                        Get = () => st.Include, Set = v => { st.Include = v; RefreshMasterPending(); },
                        Edit = async () =>
                        {
                            string v = await MasterPromptAsync(Loc.T("⚙️ Réglage Android"),
                                Loc.F("{0}\nValeur actuelle : {1}\n(clé Android : {2} · {3})\n\nNouvelle valeur :", label, shown, st.Namespace, st.Key), st.Value);
                            if (v == null) return;
                            st.Value = v; st.Include = true;
                            RenderMasterView();
                        },
                        Remove = () => { m.Settings.Remove(st); RenderMasterView(); },
                    }, null);
                    row.Margin = new Thickness(18, 0, 0, 6);
                    panel.Children.Add(row);
                }
                if (items.Count > 200) panel.Children.Add(MasterMutedText(Loc.F("… et {0} autre(s) : affinez la recherche.", items.Count - 200), 11.5));
                body.Children.Add(panel);
            }
        }

        private TextBlock MasterHeading(string text, double size, Thickness? margin = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = margin ?? new Thickness(0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return t;
        }

        /// <summary>Une ligne du formulaire : nom en clair, valeur « Ne pas modifier » ou imposée.</summary>
        private FrameworkElement MasterSettingFormRow(MasterSettingDef def)
        {
            var m = _master;
            var st = m.Settings.FirstOrDefault(x => x.Namespace == def.Namespace && x.Key == def.Key);
            bool imposed = st != null && st.Include;

            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            var bg = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5) };
            void Paint(bool on) { if (on) bg.SetResourceReference(Border.BackgroundProperty, "BrandSoft"); else bg.Background = System.Windows.Media.Brushes.Transparent; }
            Paint(imposed);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = $"{def.Namespace} · {def.Key}" };
            var label = new TextBlock { Text = Loc.T(def.Label), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, FontWeight = imposed ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            names.Children.Add(label);
            var subs = new List<string>();
            if (def.Hint.Length > 0) subs.Add(Loc.T(def.Hint));
            if (st != null && !st.Include && m.SourceModel.Length > 0) subs.Add(Loc.F("sur le terminal de référence : {0}", Loc.T(MasterSettingsCatalog.DisplayValue(def, st.Value))));
            if (subs.Count > 0) { var sub = MasterSub(string.Join(" · ", subs)); sub.TextWrapping = TextWrapping.Wrap; names.Children.Add(sub); }
            g.Children.Add(names);

            // Imposer une valeur : crée ou réactive le réglage dans le Master ; « Ne pas modifier » le désactive.
            void Impose(string value)
            {
                if (st == null) { st = new MasterSetting { Namespace = def.Namespace, Key = def.Key, Value = value, Include = true }; m.Settings.Add(st); }
                else { st.Value = value; st.Include = true; }
                label.FontWeight = FontWeights.SemiBold; Paint(true);
                RefreshMasterPending();
            }
            void Release()
            {
                if (st != null) st.Include = false;
                label.FontWeight = FontWeights.Normal; Paint(false);
                RefreshMasterPending();
            }

            FrameworkElement editor;
            var cmb = new ComboBox { Height = 28, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
            var keep = new ComboBoxItem { Content = Loc.T("Ne pas modifier"), Tag = null };
            keep.SetResourceReference(ComboBoxItem.ForegroundProperty, "Muted");
            cmb.Items.Add(keep);
            if (def.Kind == MasterSettingKind.Toggle || def.Kind == MasterSettingKind.Choice)
            {
                foreach (var o in def.Options)
                {
                    var it = new ComboBoxItem { Content = Loc.T(o.Label), Tag = o.Value };
                    cmb.Items.Add(it);
                    if (imposed && cmb.SelectedItem == null && MasterSettingsCatalog.SameValue(o.Value, st.Value)) cmb.SelectedItem = it;
                }
                if (imposed && cmb.SelectedItem == null)
                {
                    var cur = new ComboBoxItem { Content = Loc.F("Autre valeur : {0}", st.Value), Tag = st.Value };
                    cmb.Items.Add(cur);
                    cmb.SelectedItem = cur;
                }
                if (!imposed) cmb.SelectedItem = keep;
                cmb.SelectionChanged += (s, a) =>
                {
                    var tag = (cmb.SelectedItem as ComboBoxItem)?.Tag as string;
                    if (tag == null) Release(); else Impose(tag);
                };
                editor = cmb;
            }
            else
            {
                // Nombre ou texte : « Ne pas modifier » / « Imposer » + zone de saisie.
                var set = new ComboBoxItem { Content = Loc.T("Imposer :"), Tag = "set" };
                cmb.Items.Add(set);
                cmb.SelectedItem = imposed ? set : keep;
                cmb.Width = def.Kind == MasterSettingKind.Number ? 140 : 110;
                var box = new TextBox { Height = 28, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center,
                    Text = st?.Value ?? def.Default, IsEnabled = imposed, ToolTip = def.Kind == MasterSettingKind.Number ? Loc.F("Valeur de {0} à {1}", def.Min, def.Max) : Loc.T(def.Hint) };
                var dock = new DockPanel();
                DockPanel.SetDock(cmb, Dock.Left);
                dock.Children.Add(cmb);
                dock.Children.Add(box);
                bool Valid() => def.Kind != MasterSettingKind.Number || MasterSettingsCatalog.IsValid(def, box.Text);
                cmb.SelectionChanged += (s, a) =>
                {
                    bool on = (cmb.SelectedItem as ComboBoxItem)?.Tag as string == "set";
                    box.IsEnabled = on;
                    if (on && Valid()) Impose(box.Text.Trim()); else if (!on) Release();
                    if (on) box.Focus();
                };
                box.TextChanged += (s, a) =>
                {
                    bool ok = Valid();
                    box.SetResourceReference(TextBox.ForegroundProperty, ok ? "Ink" : "BadFg");
                    if (ok && box.IsEnabled) Impose(box.Text.Trim());
                };
                editor = dock;
            }
            editor.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(editor, 1); g.Children.Add(editor);
            bg.Child = g;
            return bg;
        }

        private TextBlock MasterSub(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }

        // ------------------------------------------------------------------ Formulaires d'ajout guidés (étape, Wi-Fi)

        /// <summary>Modèles d'étapes à faire à la main : ce qu'on demande, l'exemple, l'écran ouvert, l'intitulé produit.</summary>
        private static readonly (string Key, string Name, string Field, string Example, string Screen, string TitleFormat, string Detail, bool Required)[] MasterStepTemplates =
        {
            ("google", "Compte Google", "Adresse du compte (facultatif)", "atelier@entreprise.fr", "google", "Ajouter le compte Google {0}", "le mot de passe se saisit sur le terminal", false),
            ("wifi", "Wi-Fi d'entreprise", "Nom du réseau (SSID)", "WIFI-ENTREPRISE", "wifi", "Configurer le Wi-Fi d'entreprise « {0} »", "identifiant, mot de passe ou certificat du réseau", true),
            ("cert", "Certificat", "Nom du certificat", "Certificat proxy", "security", "Installer le certificat « {0} »", "fichier à déposer avec l'onglet Fichiers (ex. /sdcard/Download/)", true),
            ("mdm", "Enrôlement MDM", "MDM et identifiant d'enrôlement", "SOTI — code d'enrôlement", "", "Enrôler le terminal : {0}", "", true),
            ("date", "Date et fuseau", "Fuseau horaire voulu", "Europe/Paris", "date", "Régler la date, l'heure et le fuseau ({0})", "", false),
            ("locale", "Langue", "Langue voulue", "Français (France)", "locale", "Régler la langue ({0})", "", false),
            ("other", "Autre", "Ce qu'il faut faire", "Coller l'étiquette d'inventaire au dos", "", "{0}", "", true),
        };

        private string _masterStepTemplate = "google";

        private Border MasterFormCard(string title)
        {
            var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 10, 0, 6) };
            card.SetResourceReference(Border.BackgroundProperty, "Surface2");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            return card;
        }

        private TextBlock MasterFieldLabel(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return t;
        }

        /// <summary>Ajout guidé d'une étape : on choisit le type, on remplit un seul champ avec un exemple, l'aperçu montre le résultat.</summary>
        private void AddMasterStepForm()
        {
            var m = _master;
            var card = MasterFormCard("");
            var p = new StackPanel();
            p.Children.Add(MasterHeading(Loc.T("Nouvelle étape à faire à la main"), 13.5));
            p.Children.Add(MasterFieldLabel(Loc.T("Que faut-il faire ?")));
            var types = new WrapPanel();
            foreach (var tpl in MasterStepTemplates)
            {
                var rb = new RadioButton { GroupName = "MasterStepTpl", Tag = tpl.Key, Content = Loc.T(tpl.Name), Style = (Style)FindResource("FilterChipStyle"), Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(0, 0, 6, 6), FontSize = 12, IsChecked = tpl.Key == _masterStepTemplate };
                types.Children.Add(rb);
            }
            p.Children.Add(types);
            var fieldLabel = MasterFieldLabel("");
            p.Children.Add(fieldLabel);
            var field = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Style = (Style)FindResource("PlaceholderTextBoxStyle") };
            p.Children.Add(field);
            var screenRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var screenLabel = MasterSub(Loc.T("Écran à ouvrir sur le terminal :  ")); screenLabel.VerticalAlignment = VerticalAlignment.Center;
            var tmpStep = new MasterStep();
            var screen = MasterScreenPicker(tmpStep);
            screenRow.Children.Add(screenLabel); screenRow.Children.Add(screen);
            p.Children.Add(screenRow);
            p.Children.Add(MasterFieldLabel(Loc.T("Précision pour le technicien (facultatif)")));
            var detail = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Style = (Style)FindResource("PlaceholderTextBoxStyle") };
            p.Children.Add(detail);
            var preview = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "BrandText");
            p.Children.Add(preview);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = Loc.T("Ajouter l'étape"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 30, Padding = new Thickness(14, 0, 14, 0) };
            var cancel = new Button { Content = Loc.T("Annuler"), Style = (Style)FindResource("SettingsLinkStyle"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            p.Children.Add(buttons);
            card.Child = p;
            _masterViewHost.Children.Add(card);

            (string Key, string Name, string Field, string Example, string Screen, string TitleFormat, string Detail, bool Required) Tpl() =>
                MasterStepTemplates.First(t => t.Key == _masterStepTemplate);
            string Title()
            {
                var t = Tpl();
                string v = field.Text.Trim();
                if (t.Key == "google" && v.Length == 0) return Loc.T("Ajouter un compte Google");
                if (t.Key == "date" && v.Length == 0) return Loc.T("Régler la date, l'heure et le fuseau");
                if (t.Key == "locale" && v.Length == 0) return Loc.T("Régler la langue");
                return v.Length == 0 ? "" : Loc.F(t.TitleFormat, v);
            }
            void Update()
            {
                var t = Tpl();
                string screenName = Loc.T(MasterProfile.StepScreens.First(s => s[0] == tmpStep.Screen)[1]);
                string title = Title();
                ok.IsEnabled = title.Length > 0;
                preview.Text = title.Length == 0 ? Loc.F("Renseignez « {0} ».", Loc.T(t.Field))
                    : Loc.F("Dans le bilan de chaque terminal : ✋ {0}{1}", title, (detail.Text.Trim().Length > 0 ? " — " + detail.Text.Trim() : t.Detail.Length > 0 ? " — " + Loc.T(t.Detail) : ""))
                      + (tmpStep.Screen.Length > 0 ? Loc.F("\nÉcran ouvert sur le terminal : {0}", screenName) : "");
            }
            void ApplyTemplate()
            {
                var t = Tpl();
                fieldLabel.Text = Loc.T(t.Field);
                field.Tag = Loc.F("ex. {0}", Loc.T(t.Example));
                detail.Tag = t.Detail.Length > 0 ? Loc.F("par défaut : {0}", Loc.T(t.Detail)) : Loc.T("ex. demander l'identifiant au responsable du site");
                foreach (ComboBoxItem it in screen.Items) if ((string)it.Tag == t.Screen) screen.SelectedItem = it;
                tmpStep.Screen = t.Screen;
                Update();
            }
            foreach (RadioButton rb in types.Children)
                rb.Checked += (s, a) => { _masterStepTemplate = (string)((RadioButton)s).Tag; ApplyTemplate(); field.Focus(); };
            field.TextChanged += (s, a) => Update();
            detail.TextChanged += (s, a) => Update();
            screen.SelectionChanged += (s, a) => Update();
            ok.Click += (s, a) =>
            {
                var t = Tpl();
                string title = Title();
                if (title.Length == 0) return;
                m.Steps.Add(new MasterStep { Title = title, Detail = detail.Text.Trim().Length > 0 ? detail.Text.Trim() : Loc.T(t.Detail), Screen = tmpStep.Screen });
                _masterStepFormOpen = false;
                RerenderMaster();
            };
            cancel.Click += (s, a) => { _masterStepFormOpen = false; RenderMasterView(); };
            ApplyTemplate();
            field.Loaded += (s, a) => field.Focus();
        }

        /// <summary>Types de sécurité Wi-Fi proposés (valeur enregistrée, libellé).</summary>
        private static readonly (string Value, string Label)[] MasterWifiSecurities =
        {
            ("wpa2-psk", "WPA2-Personnel"),
            ("wpa2-psk/wpa3-sae", "WPA2/WPA3 (mode transition)"),
            ("wpa3-sae", "WPA3-Personnel"),
            ("open", "Ouvert (sans mot de passe)"),
            ("wpa2-eap", "Entreprise 802.1X (étape à la main)"),
            ("wep", "WEP (étape à la main)"),
        };

        private ComboBox MasterWifiSecurityPicker(string current, Action<string> changed)
        {
            var cmb = new ComboBox { Height = 28, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Type de sécurité du réseau") };
            foreach (var o in MasterWifiSecurities)
            {
                var it = new ComboBoxItem { Content = Loc.T(o.Label), Tag = o.Value };
                cmb.Items.Add(it);
                if (string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase)) cmb.SelectedItem = it;
            }
            if (cmb.SelectedItem == null)
            {
                var cur = new ComboBoxItem { Content = current, Tag = current };
                cmb.Items.Add(cur);
                cmb.SelectedItem = cur;
            }
            cmb.SelectionChanged += (s, a) => changed((cmb.SelectedItem as ComboBoxItem)?.Tag as string ?? current);
            return cmb;
        }

        /// <summary>Réseau d'entreprise ou WEP : non réinjectable, une étape à la main le rappelle.</summary>
        private void EnsureMasterWifiStep(MasterWifi w)
        {
            string title = Loc.F("Configurer le Wi-Fi d'entreprise « {0} »", w.Ssid);
            if (!_master.Steps.Any(s => s.Title == title))
                _master.Steps.Add(new MasterStep { Title = title, Detail = Loc.T("identifiant, mot de passe ou certificat du réseau"), Screen = "wifi" });
        }

        /// <summary>Ajout guidé d'un réseau Wi-Fi : nom, sécurité, mot de passe, réseau masqué.</summary>
        private void AddMasterWifiForm()
        {
            var m = _master;
            var card = MasterFormCard("");
            var p = new StackPanel();
            p.Children.Add(MasterHeading(Loc.T("Nouveau réseau Wi-Fi"), 13.5));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new StackPanel(); var right = new StackPanel();
            Grid.SetColumn(right, 2);
            grid.Children.Add(left); grid.Children.Add(right);
            left.Children.Add(MasterFieldLabel(Loc.T("Nom du réseau (SSID)")));
            var ssid = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Tag = Loc.F("ex. {0}", "WIFI-ATELIER"), Style = (Style)FindResource("PlaceholderTextBoxStyle") };
            left.Children.Add(ssid);
            right.Children.Add(MasterFieldLabel(Loc.T("Sécurité")));
            string security = "wpa2-psk";
            var pwdLabel = MasterFieldLabel(Loc.T("Mot de passe"));
            var pwd = new TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
            var note = MasterSub(""); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 6, 0, 0);
            var ok = new Button { Content = Loc.T("Ajouter le réseau"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 30, Padding = new Thickness(14, 0, 14, 0) };
            void Update()
            {
                string type = MasterProfile.WifiConnectType(security);
                bool needPwd = type == "wpa2" || type == "wpa3";
                pwdLabel.Visibility = pwd.Visibility = needPwd ? Visibility.Visible : Visibility.Collapsed;
                note.Text = type.Length == 0 ? Loc.T("Ce type ne se réinjecte pas par ADB : une étape « à faire à la main » sera ajoutée pour ce réseau.")
                    : type == "open" ? Loc.T("Réseau sans mot de passe.") : Loc.T("Le mot de passe est enregistré en clair dans le Master : gardez le fichier en lieu sûr.");
                ok.IsEnabled = ssid.Text.Trim().Length > 0 && (!needPwd || pwd.Text.Length >= 8);
                ok.ToolTip = needPwd && pwd.Text.Length < 8 ? Loc.T("8 caractères minimum pour un mot de passe WPA") : null;
            }
            right.Children.Add(MasterWifiSecurityPicker(security, v => { security = v; Update(); }));
            p.Children.Add(grid);
            p.Children.Add(pwdLabel);
            p.Children.Add(pwd);
            var hidden = new CheckBox { Content = Loc.T("Réseau masqué (son nom n'est pas diffusé)"), Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
            p.Children.Add(hidden);
            p.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var cancel = new Button { Content = Loc.T("Annuler"), Style = (Style)FindResource("SettingsLinkStyle"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            p.Children.Add(buttons);
            card.Child = p;
            _masterViewHost.Children.Add(card);
            ssid.TextChanged += (s, a) => Update();
            pwd.TextChanged += (s, a) => Update();
            ok.Click += (s, a) =>
            {
                string name = ssid.Text.Trim();
                m.WifiNetworks.RemoveAll(x => x.Ssid == name);
                var w = new MasterWifi { Ssid = name, Security = security, Password = MasterProfile.WifiConnectType(security) == "open" ? "" : pwd.Text, Hidden = hidden.IsChecked == true };
                if (MasterProfile.WifiConnectType(security).Length == 0) { w.Include = false; EnsureMasterWifiStep(w); }
                m.WifiNetworks.Add(w);
                _masterWifiFormOpen = false;
                RerenderMaster();
            };
            cancel.Click += (s, a) => { _masterWifiFormOpen = false; RenderMasterView(); };
            Update();
            ssid.Loaded += (s, a) => ssid.Focus();
        }

        // ------------------------------------------------------------------ Lignes génériques

        private MasterRow MasterWifiRow(MasterProfile m, MasterWifi w)
        {
            string type = MasterProfile.WifiConnectType(w.Security);
            var extra = new StackPanel { Orientation = Orientation.Horizontal };
            var sec = MasterWifiSecurityPicker(w.Security, v =>
            {
                w.Security = v;
                if (MasterProfile.WifiConnectType(v).Length == 0) { w.Include = false; EnsureMasterWifiStep(w); }
                else w.Include = true;
                RerenderMaster();
            });
            sec.Width = 210;
            extra.Children.Add(sec);
            if (type == "wpa2" || type == "wpa3")
            {
                var pwd = new TextBox { Text = w.Password, Width = 170, Height = 28, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, Tag = Loc.T("Mot de passe à saisir"), Style = (Style)FindResource("PlaceholderTextBoxStyle") };
                // Mot de passe manquant : champ encadré d'orange, pour qu'on le voie tout de suite.
                void Mark()
                {
                    bool missing = w.Include && string.IsNullOrEmpty(pwd.Text);
                    if (missing) { pwd.SetResourceReference(Control.BorderBrushProperty, "Action"); pwd.BorderThickness = new Thickness(2); }
                    else { pwd.ClearValue(Control.BorderBrushProperty); pwd.ClearValue(Control.BorderThicknessProperty); }
                }
                pwd.TextChanged += (s, a) => { w.Password = pwd.Text; Mark(); RefreshMasterPending(); };
                Mark();
                extra.Children.Add(pwd);
            }
            else
            {
                var note = MasterSub(type == "open" ? Loc.T("Réseau ouvert") : Loc.T("étape à la main"));
                note.Width = 170; note.Margin = new Thickness(10, 0, 0, 0); note.VerticalAlignment = VerticalAlignment.Center;
                extra.Children.Add(note);
            }
            if (type.Length > 0)
            {
                var qr = new Button { Content = "QR", ToolTip = Loc.T("Afficher le QR code de ce réseau, à scanner depuis le terminal"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 26, Width = 36, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0) };
                qr.Click += (s, a) =>
                {
                    if (type != "open" && string.IsNullOrEmpty(w.Password)) { TxtMasterSummary.Text = Loc.T("Saisissez d'abord le mot de passe de ce réseau."); return; }
                    ShowMasterWifiQr(new List<MasterWifi> { w }, Loc.T("Sur le terminal : Paramètres > Wi-Fi, touchez l'icône QR (ou « Ajouter un réseau » puis l'icône QR) et scannez ce code."));
                };
                extra.Children.Add(qr);
            }
            return new MasterRow
            {
                Label = w.Ssid, Sub = w.Hidden ? Loc.T("réseau masqué") : "",
                Get = () => w.Include, Set = v => { w.Include = v; RefreshMasterPending(); },
                Locked = type.Length == 0,
                Warn = () => w.Include && (type == "wpa2" || type == "wpa3") && string.IsNullOrEmpty(w.Password) ? Loc.T("mot de passe à saisir") : "",
                Extra = extra,
                Edit = async () =>
                {
                    string ssid = await MasterPromptAsync(Loc.T("📶 Réseau Wi-Fi"), Loc.T("Nom du réseau (SSID) :"), w.Ssid);
                    if (ssid == null || ssid.Trim().Length == 0) return;
                    w.Ssid = ssid.Trim();
                    RerenderMaster();
                },
                Remove = () => { m.WifiNetworks.Remove(w); RerenderMaster(); },
            };
        }

        /// <summary>Lanceur ou propriétaire : une seule valeur, à définir, corriger ou retirer.</summary>
        private MasterRow MasterSingleRow(string label, Func<MasterItem> get, Action<MasterItem> set, string prompt)
        {
            var item = get();
            bool defined = item != null && item.Name.Length > 0;
            string known = defined ? MasterProfile.KnownAppName(item.Name) : "";
            return new MasterRow
            {
                Label = defined ? label + " : " + (known.Length > 0 ? known : item.Name) : Loc.F("{0} : non défini", label),
                Sub = defined && known.Length > 0 ? item.Name : defined ? "" : Loc.T("✏️ pour le définir"),
                Get = () => get()?.Include == true, Set = v => { if (get() != null) get().Include = v; },
                Locked = !defined,
                Edit = async () =>
                {
                    string v = await MasterPromptAsync("🏠 " + label, prompt, get()?.Name ?? "");
                    if (v == null) return;
                    set(v.Trim().Length > 0 ? new MasterItem { Name = v.Trim() } : null);
                    RerenderMaster();
                },
                Remove = item == null ? (Action)null : () => { set(null); RerenderMaster(); },
            };
        }

        /// <summary>Liste « écran à ouvrir » d'une étape à faire à la main.</summary>
        private ComboBox MasterScreenPicker(MasterStep st)
        {
            var cmb = new ComboBox { Width = 200, Height = 28, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Écran ouvert sur le terminal à la fin de la remise en état") };
            foreach (var sc in MasterProfile.StepScreens)
            {
                var it = new ComboBoxItem { Content = Loc.T(sc[1]), Tag = sc[0] };
                cmb.Items.Add(it);
                if (sc[0] == (st.Screen ?? "")) cmb.SelectedItem = it;
            }
            if (cmb.SelectedItem == null) cmb.SelectedIndex = 0;
            cmb.SelectionChanged += (s, a) => st.Screen = (cmb.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            return cmb;
        }

        /// <summary>Une catégorie : case générale (tout cocher / décocher), boutons d'ajout, explication, puis ses lignes.</summary>
        private void AddMasterSection(string title, string help, List<MasterRow> items, Func<Task> add, string addLabel, Func<Task> add2 = null, string add2Label = null)
        {
            var header = new Grid { Margin = new Thickness(0, 12, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var usable = items.Where(i => !i.Locked).ToList();
            var boxes = new List<CheckBox>();
            CheckBox all = null;
            if (items.Count > 0)
            {
                all = new CheckBox { Content = title, Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0), FontWeight = FontWeights.SemiBold, FontSize = 13, IsEnabled = usable.Count > 0, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Tout cocher / tout décocher") };
                all.IsChecked = usable.Count > 0 && usable.All(i => i.Get()) ? true : usable.Any(i => i.Get()) ? (bool?)null : false;
                header.Children.Add(all);
            }
            else header.Children.Add(MasterHeading(title, 13));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (fn, text) in new[] { (add, addLabel), (add2, add2Label) })
            {
                if (fn == null) continue;
                var b = new Button { Content = text, Style = (Style)FindResource("OutlineButtonStyle"), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = !_masterBusy };
                var f = fn;
                b.Click += async (s, a) => await f();
                buttons.Children.Add(b);
            }
            Grid.SetColumn(buttons, 1); header.Children.Add(buttons);
            _masterViewHost.Children.Add(header);
            _masterViewHost.Children.Add(MasterMutedText(help, 11.5));
            if (items.Count == 0 && add != null)
                _masterViewHost.Children.Add(MasterMutedText(Loc.T("Rien pour l'instant."), 12));

            var list = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            bool syncing = false;
            foreach (var it in items)
            {
                var item = it;
                list.Children.Add(MasterRowElement(it, cb =>
                {
                    if (item.Locked) return;
                    cb.Checked += (s, a) => { if (!syncing && all != null) all.IsChecked = boxes.All(b => b.IsChecked == true) ? true : (bool?)null; };
                    cb.Unchecked += (s, a) => { if (!syncing && all != null) all.IsChecked = boxes.Any(b => b.IsChecked == true) ? (bool?)null : false; };
                    boxes.Add(cb);
                }));
            }
            if (all != null)
                all.Click += (s, a) =>
                {
                    bool v = all.IsChecked == true;
                    syncing = true;
                    foreach (var b in boxes) b.IsChecked = v;
                    syncing = false;
                    all.IsChecked = v;
                    RefreshMasterPending();
                };
            _masterViewHost.Children.Add(list);
        }

        /// <summary>Une ligne : case, nom (et sous-titre), alerte, zone de saisie, ✏️, ✕.</summary>
        private FrameworkElement MasterRowElement(MasterRow it, Action<CheckBox> hook)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int c = 0; c < 4; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cb = new CheckBox { IsChecked = !it.Locked && it.Get(), IsEnabled = !it.Locked, Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            if (!it.Locked)
            {
                cb.Checked += (s, a) => it.Set(true);
                cb.Unchecked += (s, a) => it.Set(false);
            }
            hook?.Invoke(cb);
            row.Children.Add(cb);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = it.Label + (it.Sub.Length > 0 ? "\n" + it.Sub : "") };
            var label = new TextBlock { Text = it.Label, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, it.Locked ? "Muted" : "Ink");
            names.Children.Add(label);
            if (it.Sub.Length > 0) names.Children.Add(MasterSub(it.Sub));
            Grid.SetColumn(names, 1); row.Children.Add(names);

            if (it.Warn != null)
            {
                var warn = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                warn.SetResourceReference(TextBlock.ForegroundProperty, "WarnFg");
                Grid.SetColumn(warn, 2); row.Children.Add(warn);
                _masterWarns.Add((warn, it.Warn));
                string w = it.Warn();
                warn.Text = w; warn.Visibility = w.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (it.Extra != null)
            {
                it.Extra.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(it.Extra, 3); row.Children.Add(it.Extra);
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (it.Edit != null)
            {
                var edit = new Button { Content = "✏️", ToolTip = Loc.T("Modifier"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 26, Width = 30, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0) };
                edit.Click += async (s, a) => await it.Edit();
                actions.Children.Add(edit);
            }
            if (it.Remove != null)
            {
                var rm = new Button { Content = "✕", ToolTip = Loc.T("Retirer du Master"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 26, Width = 30, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
                rm.Click += (s, a) => it.Remove();
                actions.Children.Add(rm);
            }
            Grid.SetColumn(actions, 4); row.Children.Add(actions);
            return row;
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

        private void BtnMasterAddWifi_Click(object sender, RoutedEventArgs e)
        {
            if (_master == null) return;
            _masterView = "wifi";
            _masterWifiFormOpen = true;
            RerenderMaster();
        }

        private void BtnMasterAddStep_Click(object sender, RoutedEventArgs e)
        {
            if (_master == null) return;
            _masterView = "steps";
            _masterStepFormOpen = true;
            RerenderMaster();
        }

        /// <summary>Nouveau Master rempli à la main (APK, fichiers, réglages, Wi-Fi, étapes…), sans terminal de référence.</summary>
        private async void BtnMasterNew_Click(object sender, RoutedEventArgs e)
        {
            if (_master != null && !await ShowCustomDialogAsync(Loc.T("Nouveau Master"), Loc.T("Le Master affiché sera remplacé par un nouveau Master à remplir à la main (pensez à l'enregistrer d'abord s'il vous sert encore). Continuer ?"), Loc.T("Continuer"), Loc.T("Annuler"), false))
                return;
            NewMasterWorkDir();
            _master = new MasterProfile { Name = TxtMasterName.Text.Trim().Length > 0 ? TxtMasterName.Text.Trim() : Loc.F("Master — {0:dd/MM/yyyy}", DateTime.Now), FormatVersion = MasterProfile.CurrentFormat };
            _masterCmdBox = null;
            _masterView = "settings";
            _masterSettingsTheme = MasterSettingsCatalog.ThemeScreen;
            TxtMasterName.Text = _master.Name;
            RenderMasterContent();
            TxtMasterSummary.Text = Loc.T("Nouveau Master : choisissez les réglages, puis ajoutez applis, Wi-Fi, fichiers et étapes à l'aide des onglets, et enregistrez-le.");
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
                _masterCmdBox = null;
                _masterView = "";
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
            var pending = MasterPendingItems();
            if (pending.Count > 0) ownerNote += Loc.F("\n\n⚠️ À compléter ou à vérifier : {0}.", string.Join(", ", pending));
            if (!await ShowCustomDialogAsync(Loc.T("🧰 Appliquer le Master"),
                    Loc.F("« {0} » va être appliqué à {1} terminal(aux) : {2} application(s), {3} réglage(s), Wi-Fi, fichiers et commandes cochés.{4}\n\nLes terminaux ne sont pas effacés : ce qui existe déjà est mis à jour ou laissé tel quel. Continuer ?",
                        m.Name, targets.Count, apps, settings, ownerNote), Loc.T("Appliquer"), Loc.T("Annuler"), false))
                return;

            _masterBusy = true;
            UpdateMasterApplyButton();
            BtnMasterRefresh.IsEnabled = false;
            AnimateNavButton(BtnNavMaster, true);
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🧰 Master « {1} » appliqué à {2} terminal(aux).", DateTime.Now, m.Name, targets.Count));
            _masterQrNeeded.Clear();
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
            if (_masterQrNeeded.Count > 0) ShowMasterWifiQr(_masterQrNeeded.ToList(), Loc.T("Ces réseaux n'ont pas pu être enregistrés automatiquement. Sur chaque terminal (l'écran Wi-Fi est ouvert), touchez l'icône QR (ou « Ajouter un réseau » puis l'icône QR) et scannez le code."));
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
            t.PlanPanel.Visibility = Visibility.Collapsed;
            t.HelperReady = false;
            if (t.AnalyzedMaster != m) await AnalyzeMasterTargetAsync(t);
            var apps = m.Apps.Where(a => a.Include && a.Files.Count > 0).ToList();
            int total = apps.Count + 6 + m.Files.Count(f => f.Include) + m.Commands.Count, step = 0;
            void Advance() => t.Bar.Value = Math.Min(100, ++step * 100.0 / Math.Max(1, total));
            string catApps = Loc.T("Applications"), catDis = Loc.T("Applications désactivées"), catPerm = Loc.T("Autorisations"), catSet = Loc.T("Réglages"),
                   catWifi = Loc.T("Wi-Fi"), catHome = Loc.T("Lanceur / propriétaire"), catFiles = Loc.T("Fichiers"), catCmd = Loc.T("Commandes"), catSteps = Loc.T("À faire à la main");
            try
            {
                // 1. Applications (avant les autorisations, qui en dépendent).
                foreach (var a in apps)
                {
                    // Choix fait à l'analyse : déjà à jour ou plus récente sur le terminal → gardée ; sinon installée.
                    string choice = MasterAppChoice(t, a), sit = MasterAppSituation(t, a);
                    t.Installed.TryGetValue(a.Package, out var have);
                    if (choice == "skip")
                    {
                        MasterAdd(t, catApps, a.Package, sit == "same" || sit == "newer" ? "same" : "skip",
                            sit == "same" ? Loc.T("déjà installée dans cette version")
                            : sit == "newer" ? Loc.F("version plus récente gardée (v{0})", have)
                            : sit == "older" ? Loc.F("version du terminal gardée (v{0}), au choix", have)
                            : Loc.T("non installée, au choix"));
                        Advance();
                        continue;
                    }
                    MasterStatus(t, Loc.F("Installation de {0}…", a.Package));
                    var files = a.Files.Select(f => AdbManager.QuoteLocalArg(Path.Combine(_masterDir, f.Replace('/', Path.DirectorySeparatorChar)))).ToList();
                    var res = await _adbManager.ExecuteAsync($"-s {t.Serial} {(files.Count > 1 ? "install-multiple" : "install")} -r{(choice == "downgrade" ? " -d" : "")} {string.Join(" ", files)}", TimeSpan.FromMinutes(5));
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
                    // Absente → ignorée ; déjà désactivée → rien à faire ; sinon on garde le message d'Android en cas de refus.
                    script.Append($"if ! pm path {d.Name} >/dev/null 2>&1; then echo 'DIS|ABS|{d.Name}'; " +
                                  $"elif pm list packages -d 2>/dev/null | grep -qx 'package:{d.Name}'; then echo 'DIS|SAME|{d.Name}'; " +
                                  $"else o=$(pm disable-user --user 0 {d.Name} 2>&1 | tr '\\n' ' '); case \"$o\" in *disabled*) echo 'DIS|OK|{d.Name}';; *) echo \"DIS|KO|{d.Name}|$o\";; esac; fi\n");
                foreach (var a in apps.Where(x => x.Permissions.Count > 0))
                    foreach (var p in a.Permissions)
                        script.Append($"if pm grant {a.Package} {p} >/dev/null 2>&1; then echo 'PERM|OK|{a.Package} {p}'; else echo 'PERM|KO|{a.Package} {p}'; fi\n");
                // Réglages : comparés sur le PC (une seule lecture par espace), seuls ceux qui diffèrent sont écrits.
                var wanted = m.Settings.Where(x => x.Include).ToList();
                var current = await ReadMasterSettingsAsync(t.Serial);
                var toWrite = wanted.Where(x => !current.TryGetValue(x.Namespace + "|" + x.Key, out var cv) || cv != x.Value).ToList();
                foreach (var st in toWrite.Where(x => x.Namespace != MasterSettingsCatalog.SpecialNamespace))
                    script.Append($"settings put {st.Namespace} {st.Key} {MasterProfile.ShellQuote(st.Value)} >/dev/null 2>&1\n");
                var lines = await RunMasterScriptAsync(t.Serial, script.ToString());
                int permOk = 0;
                foreach (var l in lines)
                {
                    var parts = l.Split(new[] { '|' }, 4);
                    if (parts.Length < 3) continue;
                    switch (parts[0])
                    {
                        case "DIS":
                            MasterAdd(t, catDis, parts[2], parts[1] == "OK" ? "ok" : parts[1] == "SAME" ? "same" : parts[1] == "ABS" ? "skip" : "ko",
                                parts[1] == "ABS" ? Loc.T("absente de ce terminal") : parts[1] == "SAME" ? Loc.T("déjà désactivée")
                                : parts[1] == "KO" ? Loc.T(MasterProfile.ExplainAdbError(parts.Length > 3 ? parts[3] : "", "disable")) : "");
                            break;
                        case "PERM":
                            if (parts[1] == "OK") permOk++; else MasterAdd(t, catPerm, parts[2], "ko", Loc.T("refusée (autorisation système ou appli absente)"));
                            break;
                    }
                }
                // Langue et fuseau horaire : par l'appli d'aide et par « cmd alarm ».
                string localeAnswer = "";
                foreach (var st in toWrite.Where(x => x.Namespace == MasterSettingsCatalog.SpecialNamespace))
                {
                    if (st.Key == MasterSettingsCatalog.SpecialLocale)
                    {
                        MasterStatus(t, Loc.F("Langue : {0}…", st.Value));
                        if (await EnsureMasterHelperAsync(t))
                        {
                            var raw = string.Join("\n", await RunMasterScriptAsync(t.Serial, MasterHelperApp.SetLocaleCommand(st.Value) + " 2>&1\n"));
                            var r = MasterProfile.ParseBroadcastResult(raw);
                            localeAnswer = r.Code == 0 ? LastLines(raw, 1) : r.Data;
                            if (r.Code != 1) AppendConsole("\n" + Loc.F("{0} : langue non changée ({1})", t.Serial, localeAnswer));
                        }
                    }
                    else if (st.Key == MasterSettingsCatalog.SpecialTimeZone)
                    {
                        var r = await _adbManager.ExecuteAsync($"-s {t.Serial} shell cmd alarm set-timezone {MasterProfile.ShellQuote(st.Value)}", TimeSpan.FromSeconds(15));
                        if (!r.IsSuccess || ((r.StandardOutput ?? "") + (r.StandardError ?? "")).IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                            await _adbManager.ExecuteAsync($"-s {t.Serial} shell service call alarm 3 s16 {MasterProfile.ShellQuote(st.Value)}", TimeSpan.FromSeconds(15));
                    }
                }

                // Vérification : relecture du terminal ; chaque réglage modifié est listé avec son ancienne et sa nouvelle valeur.
                int setSame = wanted.Count - toWrite.Count, setOk = 0;
                if (toWrite.Count > 0)
                {
                    await Task.Delay(1500);
                    var after = await ReadMasterSettingsAsync(t.Serial);
                    foreach (var st in toWrite)
                    {
                        var def = MasterSettingsCatalog.Find(st.Namespace, st.Key);
                        string name = def != null ? Loc.T(def.Label) : Loc.T(MasterSettingsGlossary.Describe(st.Key).Label);
                        string Show(string v) => def != null ? Loc.T(MasterSettingsCatalog.DisplayValue(def, v)) : Loc.T(MasterSettingsGlossary.DisplayValue(st.Key, v));
                        current.TryGetValue(st.Namespace + "|" + st.Key, out var before);
                        bool same = after.TryGetValue(st.Namespace + "|" + st.Key, out var av) &&
                                    (av == st.Value || MasterSettingsCatalog.SameValue(av, st.Value) || (st.Key == MasterSettingsCatalog.SpecialLocale && av.StartsWith(st.Value.Split(',')[0], StringComparison.OrdinalIgnoreCase)));
                        if (same) { setOk++; MasterAdd(t, catSet, name, "ok", (before ?? "").Length > 0 ? Loc.F("{0} → {1}", Show(before), Show(st.Value)) : Show(st.Value)); }
                        else MasterAdd(t, catSet, name, "ko",
                            st.Key == MasterSettingsCatalog.SpecialLocale ? (localeAnswer.StartsWith("OK") ? Loc.F("la mini appli a répondu « {0} », mais la langue lue sur le terminal n'a pas changé", localeAnswer) : Loc.F("langue non changée : {0}", localeAnswer.Length > 0 ? localeAnswer : Loc.T("mini appli ADB Tools Helper non installée")))
                            : st.Key == MasterSettingsCatalog.SpecialTimeZone ? Loc.T("fuseau non changé : Android a refusé")
                            : Loc.T("protégé par Android"));
                    }
                }
                int permTotal = apps.Sum(a => a.Permissions.Count), setTotal = wanted.Count;
                if (permTotal > 0) MasterAdd(t, catPerm, Loc.F("{0}/{1} autorisation(s) accordée(s)", permOk, permTotal), permOk == permTotal ? "ok" : "info");
                if (setTotal > 0) MasterAdd(t, catSet, Loc.F("{0} modifié(s), {1} déjà identique(s), {2} protégé(s)", setOk, setSame, setTotal - setOk - setSame), setOk + setSame == setTotal ? "ok" : "info");
                step += 3; Advance();

                // 5. Wi-Fi : commande ADB si Android l'autorise, sinon mini appli ADB Tools Helper, sinon QR code à scanner.
                foreach (var w in m.WifiNetworks.Where(x => x.Include))
                {
                    string type = MasterProfile.WifiConnectType(w.Security);
                    if (type.Length == 0) { MasterAdd(t, catWifi, w.Ssid, "skip", Loc.T("réseau d'entreprise ou WEP : à configurer à la main ou par le MDM")); continue; }
                    if (type != "open" && string.IsNullOrEmpty(w.Password)) { MasterAdd(t, catWifi, w.Ssid, "skip", Loc.T("mot de passe non saisi dans le Master")); continue; }
                    MasterStatus(t, Loc.F("Wi-Fi : {0}…", w.Ssid));
                    bool saved = false;
                    string why = "";
                    if (t.Sdk == 0 || t.Sdk >= 30)
                    {
                        string cmd = $"cmd wifi connect-network {MasterProfile.ShellQuote(w.Ssid)} {type}" + (type == "open" ? "" : " " + MasterProfile.ShellQuote(w.Password)) + (w.Hidden ? " -h" : "");
                        string outp = string.Join(" ", await RunMasterScriptAsync(t.Serial, cmd + " 2>&1\n"));
                        saved = !System.Text.RegularExpressions.Regex.IsMatch(outp, "(?i)exception|error|invalid|unknown|fail|denied|does not have");
                        if (saved) MasterAdd(t, catWifi, w.Ssid, "ok", Loc.T("enregistré"));
                    }
                    if (!saved && await EnsureMasterHelperAsync(t))
                    {
                        // Par un script envoyé sur le terminal : le mot de passe passe tel quel, quels que soient ses caractères.
                        var r = await RunMasterScriptAsync(t.Serial, MasterHelperApp.AddNetworkCommand(w.Ssid, type, w.Password, w.Hidden) + " 2>&1\n");
                        var br = MasterProfile.ParseBroadcastResult(string.Join("\n", r));
                        saved = br.Code == 1;
                        if (saved) MasterAdd(t, catWifi, w.Ssid, "ok", Loc.T("enregistré (mini appli ADB Tools Helper)"));
                        else why = br.Data;
                    }
                    if (!saved)
                    {
                        // Dernier recours : QR code à scanner depuis l'écran Wi-Fi du terminal (affiché à la fin de l'envoi).
                        MasterAdd(t, catWifi, w.Ssid, "todo", Loc.T("à ajouter en scannant son QR code (Paramètres > Wi-Fi > icône QR)") + (why.Length > 0 ? " — " + why : ""));
                        lock (_masterQrNeeded) _masterQrNeeded.Add(w);
                        await _adbManager.ExecuteAsync($"-s {t.Serial} shell am start -a android.settings.WIFI_SETTINGS", TimeSpan.FromSeconds(15));
                    }
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
                    string ownerPkg = m.DeviceOwner.Name.Split('/')[0];
                    string ownerName = MasterProfile.KnownAppName(ownerPkg) is string kn && kn.Length > 0 ? kn : ownerPkg;
                    var has = await _adbManager.ExecuteAsync($"-s {t.Serial} shell pm path {ownerPkg}", TimeSpan.FromSeconds(15));
                    if (!(has.StandardOutput ?? "").Contains("package:"))
                        MasterAdd(t, catHome, Loc.F("Propriétaire : {0}", ownerName), "skip", Loc.T("agent MDM absent de ce terminal : propriétaire non défini (installez l'agent pour le définir)"));
                    else
                    {
                        var res = await _adbManager.ExecuteAsync($"-s {t.Serial} shell dpm set-device-owner {m.DeviceOwner.Name}", TimeSpan.FromSeconds(30));
                        string outp = (res.StandardOutput ?? "") + (res.StandardError ?? "");
                        bool ok = outp.Contains("Success");
                        MasterAdd(t, catHome, Loc.F("Propriétaire : {0}", ownerName), ok ? "ok" : "ko", ok ? "" : Loc.T(MasterProfile.ExplainAdbError(outp, "owner")));
                    }
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

                // 9. Étapes à faire à la main : rappelées dans le bilan, le premier écran utile est ouvert sur le terminal.
                var steps = m.Steps.Where(x => x.Include).ToList();
                string screenOpened = "";
                foreach (var st in steps)
                {
                    string cmd = MasterProfile.StepScreenCommand(st.Screen);
                    if (cmd.Length > 0 && screenOpened.Length == 0)
                    {
                        await _adbManager.ExecuteAsync($"-s {t.Serial} shell input keyevent KEYCODE_WAKEUP", TimeSpan.FromSeconds(10));
                        var res = await _adbManager.ExecuteAsync($"-s {t.Serial} shell {cmd}", TimeSpan.FromSeconds(20));
                        if (res.IsSuccess && !(res.StandardOutput + res.StandardError).Contains("Error")) screenOpened = st.Title;
                    }
                    MasterAdd(t, catSteps, st.Title + (st.Detail.Length > 0 ? " — " + st.Detail : ""), "todo",
                        screenOpened == st.Title ? Loc.T("écran ouvert sur le terminal") : "");
                }

                int ko = t.Results.Count(r => r.Status == "ko"), todo = steps.Count;
                t.Outcome = ko == 0 && todo == 0 ? "ok" : "warn";
                t.Message = ko == 0 && todo == 0 ? Loc.T("Remis en état : tout a été appliqué")
                    : ko == 0 ? Loc.F("Remis en état, {0} étape(s) à faire à la main : voir le détail", todo)
                    : todo == 0 ? Loc.F("Remis en état, {0} élément(s) non appliqué(s) : voir le détail", ko)
                    : Loc.F("Remis en état, {0} élément(s) non appliqué(s) et {1} étape(s) à faire à la main : voir le détail", ko, todo);
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
            t.ShowingResults = true;
            t.PlanPanel.Visibility = Visibility.Collapsed;
            t.BtnDetail.Content = Loc.T("Détail ▾");
            t.BtnDetail.Visibility = Visibility.Visible;
            t.AnalyzedMaster = null;   // l'état du terminal a changé : nouvelle analyse au prochain coup
            t.Check.IsEnabled = true;
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🧰 {t.Serial}{(t.Model.Length > 0 ? " (" + t.Model + ")" : "")} : {t.Message}");
        }

        /// <summary>Réglages actuels du terminal, clé « espace|nom ».</summary>
        private readonly HashSet<MasterWifi> _masterQrNeeded = new HashSet<MasterWifi>();

        /// <summary>QR codes Wi-Fi à scanner depuis le terminal (Paramètres > Wi-Fi > icône QR), un par réseau.</summary>
        private void ShowMasterWifiQr(List<MasterWifi> networks, string intro)
        {
            var list = networks.Where(w => MasterProfile.WifiConnectType(w.Security).Length > 0).ToList();
            if (list.Count == 0) return;
            var root = new StackPanel { Margin = new Thickness(20) };
            var head = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 14), MaxWidth = 760 };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            root.Children.Add(head);
            var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var w in list)
            {
                string type = MasterProfile.WifiConnectType(w.Security);
                var card = new Border { Background = System.Windows.Media.Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 0, 14, 14), Width = 280 };
                var p = new StackPanel();
                p.Children.Add(new TextBlock { Text = w.Ssid, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
                try
                {
                    using (var gen = new QRCoder.QRCodeGenerator())
                    using (var data = gen.CreateQrCode(MasterProfile.WifiQrPayload(w.Ssid, type, w.Password, w.Hidden), QRCoder.QRCodeGenerator.ECCLevel.M))
                    {
                        byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(10);
                        var img = new System.Windows.Media.Imaging.BitmapImage();
                        using (var ms = new MemoryStream(png))
                        {
                            img.BeginInit();
                            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            img.StreamSource = ms;
                            img.EndInit();
                        }
                        var image = new Image { Source = img, Width = 240, Height = 240, Margin = new Thickness(0, 10, 0, 6) };
                        System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                        p.Children.Add(image);
                    }
                }
                catch (Exception ex) { p.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }); }
                p.Children.Add(new TextBlock { Text = (w.Hidden ? Loc.T("réseau masqué") + " · " : "") + w.Security, FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Center });
                card.Child = p;
                wrap.Children.Add(card);
            }
            root.Children.Add(wrap);
            var warn = MasterMutedText(Loc.T("Le QR code contient le mot de passe : ne laissez pas cette fenêtre ouverte sans surveillance."), 11.5);
            root.Children.Add(warn);
            var win = new Window
            {
                Title = Loc.T("QR codes Wi-Fi à scanner"),
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                MaxHeight = SystemParameters.WorkArea.Height - 40,
                MaxWidth = SystemParameters.WorkArea.Width - 40,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            };
            win.SetResourceReference(Window.BackgroundProperty, "Bg");
            win.Show();
        }

        /// <summary>
        /// Installe la mini appli ADB Tools Helper si elle manque (jamais par-dessus une version déjà présente : elle porte les
        /// réseaux Wi-Fi qu'elle a enregistrés) et lui accorde le droit de changer la langue.
        /// </summary>
        private async Task<bool> EnsureMasterHelperAsync(MasterTarget t)
        {
            if (t.HelperReady) return true;
            var has = await _adbManager.ExecuteAsync($"-s {t.Serial} shell dumpsys package {MasterHelperApp.Package}", TimeSpan.FromSeconds(15));
            var vc = System.Text.RegularExpressions.Regex.Match(has.StandardOutput ?? "", @"versionCode=(\d+)");
            bool present = vc.Success;
            if (present && int.Parse(vc.Groups[1].Value) < MasterHelperApp.VersionCode)
            {
                // Ancienne version : mise à jour ; si la signature diffère, désinstallation puis réinstallation
                // (les réseaux Wi-Fi du Master sont réenregistrés juste après, à l'étape Wi-Fi).
                var up = await _adbManager.ExecuteAsync($"-s {t.Serial} install -r -g {AdbManager.QuoteLocalArg(MasterHelperApp.ApkPath)}", TimeSpan.FromMinutes(2));
                if (!((up.StandardOutput ?? "") + (up.StandardError ?? "")).Contains("Success"))
                {
                    await _adbManager.ExecuteAsync($"-s {t.Serial} uninstall {MasterHelperApp.Package}", TimeSpan.FromSeconds(30));
                    present = false;
                }
            }
            if (!present)
            {
                if (!File.Exists(MasterHelperApp.ApkPath))
                {
                    AppendConsole("\n" + Loc.F("Mini appli introuvable : {0}", MasterHelperApp.ApkPath));
                    return false;
                }
                var inst = await _adbManager.ExecuteAsync($"-s {t.Serial} install -r -g {AdbManager.QuoteLocalArg(MasterHelperApp.ApkPath)}", TimeSpan.FromMinutes(2));
                if (!((inst.StandardOutput ?? "") + (inst.StandardError ?? "")).Contains("Success"))
                {
                    AppendConsole("\n" + Loc.F("{0} : installation de la mini appli ADB Tools Helper refusée : {1}", t.Serial, LastLines(inst.StandardError + inst.StandardOutput, 1)));
                    return false;
                }
            }
            var grant = await _adbManager.ExecuteAsync($"-s {t.Serial} shell pm grant {MasterHelperApp.Package} android.permission.CHANGE_CONFIGURATION", TimeSpan.FromSeconds(15));
            string grantOut = ((grant.StandardOutput ?? "") + (grant.StandardError ?? "")).Trim();
            if (grantOut.Length > 0) AppendConsole("\n" + Loc.F("{0} : autorisation de changer la langue : {1}", t.Serial, LastLines(grantOut, 1)));
            // Changer la langue demande aussi « modifier les paramètres système » (WRITE_SETTINGS), accordée par appops et non par pm grant.
            var ops = await _adbManager.ExecuteAsync($"-s {t.Serial} shell appops set {MasterHelperApp.Package} WRITE_SETTINGS allow", TimeSpan.FromSeconds(15));
            string opsOut = ((ops.StandardOutput ?? "") + (ops.StandardError ?? "")).Trim();
            var opsGet = await _adbManager.ExecuteAsync($"-s {t.Serial} shell appops get {MasterHelperApp.Package} WRITE_SETTINGS", TimeSpan.FromSeconds(15));
            if (opsOut.Length > 0 || !(opsGet.StandardOutput ?? "").Contains("allow"))
                AppendConsole("\n" + Loc.F("{0} : autorisation de modifier les paramètres système : {1}", t.Serial, LastLines(opsOut + " " + opsGet.StandardOutput, 1)));
            t.HelperReady = true;
            return true;
        }

        /// <summary>Langue (ex. fr-FR) et fuseau horaire (ex. Europe/Paris) du terminal.</summary>
        private async Task<Dictionary<string, string>> ReadMasterSpecialsAsync(string serial)
        {
            var p = await _adbManager.RunShellSectionsAsync(serial, TimeSpan.FromSeconds(15), "getprop persist.sys.locale", "getprop ro.product.locale", "getprop persist.sys.timezone");
            return new Dictionary<string, string>
            {
                { MasterSettingsCatalog.SpecialLocale, p[0].Length > 0 ? p[0] : p[1] },
                { MasterSettingsCatalog.SpecialTimeZone, p[2] },
            };
        }

        private async Task<Dictionary<string, string>> ReadMasterSettingsAsync(string serial)
        {
            var d = new Dictionary<string, string>();
            foreach (var sp in await ReadMasterSpecialsAsync(serial)) d[MasterSettingsCatalog.SpecialNamespace + "|" + sp.Key] = sp.Value;
            foreach (var ns in new[] { "system", "secure", "global" })
            {
                var r = await _adbManager.ExecuteAsync($"-s {serial} shell settings list {ns}", TimeSpan.FromSeconds(30));
                foreach (var kv in MasterProfile.ParseSettingsList(r.StandardOutput)) d[ns + "|" + kv.Key] = kv.Value;
            }
            return d;
        }

        private static string MasterIcon(string status) => status == "ok" ? "✅" : status == "same" ? "＝" : status == "skip" ? "⏭" : status == "info" ? "ℹ️" : status == "todo" ? "✋" : "❌";

        private string MasterDetailText(MasterTarget t)
        {
            var sb = new StringBuilder();
            foreach (var group in t.Results.GroupBy(r => r.Category))
            {
                sb.AppendLine(group.Key);
                foreach (var r in group.OrderBy(r => r.Status == "info" ? 0 : r.Status == "todo" ? 0 : r.Status == "ko" ? 1 : r.Status == "skip" ? 2 : 3))
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
                            r.Status == "ok" ? Loc.T("Appliqué") : r.Status == "same" ? Loc.T("Déjà en place") : r.Status == "skip" ? Loc.T("Ignoré") : r.Status == "info" ? Loc.T("Bilan") : r.Status == "todo" ? Loc.T("À faire à la main") : Loc.T("Non appliqué"), r.Detail });
                        styles.Add(r.Status == "ko" ? XlsxExport.StyleBad : r.Status == "skip" || r.Status == "todo" ? XlsxExport.StyleWarn : r.Status == "info" ? XlsxExport.StyleNormal : XlsxExport.StyleOk);
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
