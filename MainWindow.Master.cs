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
            foreach (var b in new Button[] { BtnMasterAddApk, BtnMasterAddFile, BtnMasterAddFolder, BtnMasterAddWifi, BtnMasterAddStep, BtnMasterSave })
                b.IsEnabled = !_masterBusy && _master != null;
            BtnMasterAddFolder.IsEnabled = BtnMasterAddFolder.IsEnabled && CmbMasterSource.Items.Count > 0;
            BtnMasterOpen.IsEnabled = !_masterBusy;
            BtnMasterNew.IsEnabled = !_masterBusy;
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

                // Comptes (Google ou autres) : ils ne se recopient pas par ADB, ils deviennent des étapes à faire à la main.
                var accounts = MasterProfile.ParseAccounts((await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys account", TimeSpan.FromSeconds(20))).StandardOutput);
                foreach (var acc in accounts)
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
            public Func<bool> Get;
            public Action<bool> Set;
            public Func<string> Warn;          // texte orange « à compléter » (vide = rien)
            public Func<Task> Edit;            // ✏️
            public Action Remove;              // ✕
            public FrameworkElement Extra;     // ex. mot de passe Wi-Fi, écran d'une étape
            public bool Locked;                // case non cochable (élément non défini)
        }

        private readonly HashSet<string> _masterExpanded = new HashSet<string> { "apps", "home", "files", "steps", "wifi" };
        private readonly List<(TextBlock Label, Func<string> Warn)> _masterWarns = new List<(TextBlock, Func<string>)>();
        private TextBlock _masterPendingBanner;

        /// <summary>Ce qui reste à compléter avant l'envoi (mots de passe Wi-Fi, APK non récupérés…), pour le bandeau et la confirmation.</summary>
        private List<string> MasterPendingItems()
        {
            var list = new List<string>();
            if (_master == null) return list;
            int pwd = _master.WifiNetworks.Count(w => w.Include && MasterProfile.WifiConnectType(w.Security) is string ty && (ty == "wpa2" || ty == "wpa3") && string.IsNullOrEmpty(w.Password));
            if (pwd > 0) list.Add(Loc.F("{0} mot(s) de passe Wi-Fi", pwd));
            int apk = _master.Apps.Count(a => a.Files.Count == 0);
            if (apk > 0) list.Add(Loc.F("{0} appli(s) non récupérée(s) (APK à ajouter depuis le PC)", apk));
            int steps = _master.Steps.Count(s => s.Include);
            if (steps > 0) list.Add(Loc.F("{0} étape(s) à faire à la main sur chaque terminal", steps));
            return list;
        }

        private void RefreshMasterPending()
        {
            foreach (var w in _masterWarns)
            {
                string text = w.Warn();
                w.Label.Text = text;
                w.Label.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_masterPendingBanner == null) return;
            var pending = MasterPendingItems();
            _masterPendingBanner.Text = pending.Count == 0 ? Loc.T("✅ Rien à compléter : le Master est prêt à être appliqué.")
                : Loc.F("⚠️ À compléter ou à vérifier avant l'envoi : {0}.", string.Join(", ", pending));
            _masterPendingBanner.SetResourceReference(TextBlock.ForegroundProperty, pending.Count == 0 ? "OkFg" : "WarnFg");
        }

        /// <summary>Saisie d'une valeur (null si annulé).</summary>
        private async Task<string> MasterPromptAsync(string title, string message, string value, string confirm = null)
        {
            if (!await ShowCustomDialogAsync(title, message, confirm ?? Loc.T("Valider"), Loc.T("Annuler"), false, true, value ?? "")) return null;
            return TxtDialogInput.Text;
        }

        /// <summary>Redessine le contenu après une modification (le texte des commandes est conservé).</summary>
        private void RerenderMaster()
        {
            SyncMasterFromUi();
            RenderMasterContent();
        }

        private void RenderMasterContent()
        {
            MasterContent.Children.Clear();
            _masterWarns.Clear();
            _masterPendingBanner = null;
            if (_master == null) return;
            var m = _master;
            TxtMasterInfo.Text = m.SourceModel.Length > 0
                ? Loc.F("Capturé sur {0} {1} (Android {2}, {3}) le {4:dd/MM/yyyy HH:mm}.", m.SourceManufacturer, m.SourceModel, m.SourceAndroid, m.SourceBuild, m.CreatedAt)
                : Loc.T("Master créé à la main.");

            _masterPendingBanner = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            MasterContent.Children.Add(_masterPendingBanner);
            MasterContent.Children.Add(MasterMutedText(Loc.T("Tout se vérifie et se modifie ici avant l'envoi : décochez ce qui ne doit pas partir, ✏️ pour corriger une valeur, ✕ pour retirer, « ＋ Ajouter » pour compléter ce qui n'a pas pu être récupéré."), 11.5));

            // Étapes à faire à la main (en tête : c'est ce qu'il faut compléter).
            AddMasterSection("steps", Loc.F("✋ À faire à la main ({0})", m.Steps.Count),
                Loc.T("Ce qu'ADB ne peut pas réinjecter : compte Google (le mot de passe ne se transmet pas), Wi-Fi d'entreprise, certificat, enrôlement MDM avec identifiant… À la fin de la remise en état, l'écran choisi s'ouvre sur le terminal et l'étape est rappelée dans le bilan."),
                m.Steps.Select(st => new MasterRow
                {
                    Label = st.Title + (st.Detail.Length > 0 ? " — " + st.Detail : ""),
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
                async () =>
                {
                    string t = await MasterPromptAsync(Loc.T("✋ Étape à faire à la main"), Loc.T("Ce qu'il faut faire (ex. Ajouter le compte Google atelier@exemple.fr) :"), "", Loc.T("Ajouter"));
                    if (t == null || t.Trim().Length == 0) return;
                    bool google = t.IndexOf("google", StringComparison.OrdinalIgnoreCase) >= 0;
                    m.Steps.Add(new MasterStep { Title = t.Trim(), Screen = google ? "google" : t.IndexOf("wi", StringComparison.OrdinalIgnoreCase) >= 0 && t.IndexOf("fi", StringComparison.OrdinalIgnoreCase) >= 0 ? "wifi" : "" });
                    _masterExpanded.Add("steps");
                    RerenderMaster();
                });

            AddMasterSection("apps", Loc.F("📦 Applications ({0})", m.Apps.Count),
                Loc.T("Réinstallées avec leurs autorisations. Les données internes des applis ne sont pas copiées (rôle du MDM). « ＋ Ajouter » : APK depuis le PC."),
                m.Apps.Select(a => new MasterRow
                {
                    Label = a.FromPc ? Loc.F("{0} (depuis le PC)", a.Package) : a.Package + (a.VersionCode.Length > 0 ? "  v" + a.VersionCode : "") + (a.Files.Count > 1 ? Loc.F("  · {0} fichiers", a.Files.Count) : "") + (a.Permissions.Count > 0 ? Loc.F("  · {0} autorisation(s)", a.Permissions.Count) : ""),
                    Get = () => a.Include, Set = v => a.Include = v,
                    Locked = a.Files.Count == 0,
                    Warn = () => a.Files.Count == 0 ? Loc.T("APK non récupéré : ajoutez-le depuis le PC") : "",
                    Remove = () => { m.Apps.Remove(a); RerenderMaster(); },
                }).ToList(),
                () => { BtnMasterAddApk_Click(null, null); return Task.CompletedTask; });

            AddMasterSection("disabled", Loc.F("🚫 Applications désactivées ({0})", m.DisabledApps.Count),
                Loc.T("Applications (souvent système) désactivées sur le terminal de référence."),
                m.DisabledApps.Select(d => new MasterRow
                {
                    Label = d.Name, Get = () => d.Include, Set = v => d.Include = v,
                    Remove = () => { m.DisabledApps.Remove(d); RerenderMaster(); },
                }).ToList(),
                async () =>
                {
                    string p = await MasterPromptAsync(Loc.T("🚫 Application à désactiver"), Loc.T("Nom du paquet (ex. com.android.chrome) :"), "", Loc.T("Ajouter"));
                    if (p == null || !System.Text.RegularExpressions.Regex.IsMatch(p.Trim(), @"^[A-Za-z0-9_.]+$")) return;
                    m.DisabledApps.RemoveAll(x => x.Name == p.Trim());
                    m.DisabledApps.Add(new MasterItem { Name = p.Trim() });
                    _masterExpanded.Add("disabled");
                    RerenderMaster();
                });

            AddMasterSection("settings", Loc.F("⚙️ Réglages Android ({0})", m.Settings.Count),
                Loc.T("Écran, veille, son, langue, clavier, options… Seules les valeurs différentes sont modifiées ; les réglages propres à chaque terminal sont exclus. Android en protège certains : ils seront signalés. ✏️ pour changer une valeur."),
                m.Settings.Select(st => new MasterRow
                {
                    Label = $"{st.Namespace} · {st.Key} = {(st.Value.Length > 60 ? st.Value.Substring(0, 60) + "…" : st.Value)}",
                    Get = () => st.Include, Set = v => st.Include = v,
                    Edit = async () =>
                    {
                        string v = await MasterPromptAsync(Loc.T("⚙️ Réglage Android"), Loc.F("Nouvelle valeur de {0} ({1}) :", st.Key, st.Namespace), st.Value);
                        if (v == null) return;
                        st.Value = v; st.Include = true;
                        RerenderMaster();
                    },
                    Remove = () => { m.Settings.Remove(st); RerenderMaster(); },
                }).ToList(),
                async () =>
                {
                    string v = await MasterPromptAsync(Loc.T("⚙️ Réglage Android"), Loc.T("Espace, nom et valeur. Ex. : system screen_off_timeout=600000 ou global stay_on_while_plugged_in=3"), "system ", Loc.T("Ajouter"));
                    if (v == null) return;
                    var st = MasterProfile.ParseSettingInput(v);
                    if (st == null) { TxtMasterSummary.Text = Loc.T("Réglage non reconnu : écrivez par exemple « system screen_off_timeout=600000 »."); return; }
                    m.Settings.RemoveAll(x => x.Namespace == st.Namespace && x.Key == st.Key);
                    m.Settings.Insert(0, st);
                    _masterExpanded.Add("settings");
                    RerenderMaster();
                });

            AddMasterSection("wifi", Loc.F("📶 Réseaux Wi-Fi ({0})", m.WifiNetworks.Count),
                Loc.T("Android ne donne pas les mots de passe : saisissez-les ici (ils sont enregistrés dans le Master, gardez le fichier en lieu sûr). Réinjection sur Android 11 et plus ; un réseau d'entreprise (802.1X) passe en étape à faire à la main."),
                m.WifiNetworks.Select(w => MasterWifiRow(m, w)).ToList(),
                () => { BtnMasterAddWifi_Click(null, null); return Task.CompletedTask; });

            AddMasterSection("home", Loc.T("🏠 Lanceur et propriétaire du terminal"),
                Loc.T("Le propriétaire du terminal (kiosque / MDM) ne peut être défini que sur un terminal sans compte, juste après un wipe. ✏️ pour le définir ou le corriger."),
                new List<MasterRow>
                {
                    MasterSingleRow(Loc.T("Lanceur par défaut"), () => m.Launcher, v => m.Launcher = v, Loc.T("Lanceur par défaut (paquet/activité, ex. com.zebra.launcher/.Main) :")),
                    MasterSingleRow(Loc.T("Propriétaire du terminal (kiosque / MDM)"), () => m.DeviceOwner, v => m.DeviceOwner = v, Loc.T("Composant administrateur (paquet/classe, ex. net.soti.mobicontrol.androidwork/net.soti.mobicontrol.admin.DeviceAdminAdapter) :")),
                }, null);

            AddMasterSection("files", Loc.F("📁 Fichiers ({0})", m.Files.Count),
                Loc.T("Dossiers récupérés sur le terminal de référence et fichiers ajoutés depuis le PC (ex. configuration DataWedge). ✏️ pour changer la destination."),
                m.Files.Select(f => new MasterRow
                {
                    Label = $"{(f.IsFolder ? "📁" : "📄")} {Path.GetFileName(f.Local.TrimEnd('/'))} → {f.DevicePath}",
                    Get = () => f.Include, Set = v => f.Include = v,
                    Edit = async () =>
                    {
                        string d = await MasterPromptAsync(Loc.T("📄 Destination sur le terminal"), Loc.T("Dossier du terminal où déposer ce fichier. Pour une configuration DataWedge (Zebra) : /enterprise/device/settings/datawedge/autoimport/"), f.DevicePath);
                        if (d == null || d.Trim().Length == 0) return;
                        f.DevicePath = d.Trim().EndsWith("/") ? d.Trim() : d.Trim() + "/";
                        RerenderMaster();
                    },
                    Remove = () => { m.Files.Remove(f); RerenderMaster(); },
                }).ToList(),
                () => { BtnMasterAddFile_Click(null, null); return Task.CompletedTask; });

            // Commandes ADB libres, en fin de remise en état.
            var head = new TextBlock { Text = Loc.T("⌨️ Commandes ADB (en dernier)"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            MasterContent.Children.Add(head);
            MasterContent.Children.Add(MasterMutedText(Loc.T("Une commande par ligne, sans « adb » ni « -s ». Ex. : shell settings put global stay_on_while_plugged_in 3"), 11.5));
            _masterCmdBox = new TextBox { AcceptsReturn = true, Height = 70, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(6, 4, 6, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = string.Join(Environment.NewLine, m.Commands) };
            MasterContent.Children.Add(_masterCmdBox);
            RefreshMasterPending();
            UpdateMasterApplyButton();
        }

        private MasterRow MasterWifiRow(MasterProfile m, MasterWifi w)
        {
            string type = MasterProfile.WifiConnectType(w.Security);
            FrameworkElement extra;
            if (type == "wpa2" || type == "wpa3")
            {
                var pwd = new TextBox { Text = w.Password, Width = 200, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Tag = Loc.T("Mot de passe"), Style = (Style)FindResource("PlaceholderTextBoxStyle") };
                pwd.TextChanged += (s, a) => { w.Password = pwd.Text; RefreshMasterPending(); };
                extra = pwd;
            }
            else
            {
                var note = MasterMutedText(type == "open" ? Loc.T("Réseau ouvert") : Loc.T("Non réinjectable (entreprise / WEP)"), 11.5);
                note.Margin = new Thickness(0); note.VerticalAlignment = VerticalAlignment.Center;
                extra = note;
            }
            return new MasterRow
            {
                Label = $"{w.Ssid}  ({w.Security})",
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
            return new MasterRow
            {
                Label = item != null && item.Name.Length > 0 ? $"{label} : {item.Name}" : Loc.F("{0} : non défini", label),
                Get = () => get()?.Include == true, Set = v => { if (get() != null) get().Include = v; },
                Locked = item == null || item.Name.Length == 0,
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
            var cmb = new ComboBox { Width = 190, Height = 26, FontSize = 12, ToolTip = Loc.T("Écran ouvert sur le terminal à la fin de la remise en état") };
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

        /// <summary>Une catégorie : case générale (tout cocher / décocher), « ＋ Ajouter », explication, puis ses lignes (repliables).</summary>
        private void AddMasterSection(string key, string title, string help, List<MasterRow> items, Func<Task> add)
        {
            bool expanded = _masterExpanded.Contains(key);
            var header = new Grid { Margin = new Thickness(0, 16, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var usable = items.Where(i => !i.Locked).ToList();
            var all = new CheckBox { Content = title, Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0), FontWeight = FontWeights.SemiBold, IsEnabled = usable.Count > 0 };
            all.IsChecked = usable.Count > 0 && usable.All(i => i.Get()) ? true : usable.Any(i => i.Get()) ? (bool?)null : false;
            header.Children.Add(all);
            if (add != null)
            {
                var plus = new Button { Content = Loc.T("＋ Ajouter"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 0), IsEnabled = !_masterBusy };
                plus.Click += async (s, a) => await add();
                Grid.SetColumn(plus, 1);
                header.Children.Add(plus);
            }
            var toggle = new Button { Content = expanded ? Loc.T("Masquer ▴") : Loc.T("Voir ▾"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 24, Padding = new Thickness(8, 0, 8, 0), Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Hidden };
            Grid.SetColumn(toggle, 2);
            header.Children.Add(toggle);
            MasterContent.Children.Add(header);
            MasterContent.Children.Add(MasterMutedText(items.Count == 0 ? Loc.T("Rien pour l'instant.") + " " + help : help, 11.5));

            var list = new StackPanel { Margin = new Thickness(30, 0, 0, 0), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
            var boxes = new List<CheckBox>();
            bool syncing = false;
            foreach (var it in items)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int c = 0; c < 4; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var cb = new CheckBox { Content = new TextBlock { Text = it.Label, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = it.Label, IsChecked = !it.Locked && it.Get(), IsEnabled = !it.Locked, Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var item = it;
                if (!it.Locked)
                {
                    cb.Checked += (s, a) => { item.Set(true); if (!syncing) all.IsChecked = boxes.All(b => b.IsChecked == true) ? true : (bool?)null; };
                    cb.Unchecked += (s, a) => { item.Set(false); if (!syncing) all.IsChecked = boxes.Any(b => b.IsChecked == true) ? (bool?)null : false; };
                    boxes.Add(cb);
                }
                row.Children.Add(cb);
                if (it.Warn != null)
                {
                    var warn = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                    warn.SetResourceReference(TextBlock.ForegroundProperty, "WarnFg");
                    Grid.SetColumn(warn, 1); row.Children.Add(warn);
                    _masterWarns.Add((warn, it.Warn));
                }
                if (it.Extra != null)
                {
                    it.Extra.Margin = new Thickness(8, 0, 0, 0);
                    it.Extra.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(it.Extra, 2); row.Children.Add(it.Extra);
                }
                if (it.Edit != null)
                {
                    var edit = new Button { Content = "✏️", ToolTip = Loc.T("Modifier"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 24, Width = 30, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0) };
                    edit.Click += async (s, a) => await item.Edit();
                    Grid.SetColumn(edit, 3); row.Children.Add(edit);
                }
                if (it.Remove != null)
                {
                    var rm = new Button { Content = "✕", ToolTip = Loc.T("Retirer du Master"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 24, Width = 30, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0) };
                    rm.Click += (s, a) => item.Remove();
                    Grid.SetColumn(rm, 4); row.Children.Add(rm);
                }
                list.Children.Add(row);
            }
            all.Click += (s, a) =>
            {
                bool v = all.IsChecked == true;
                syncing = true;
                foreach (var b in boxes) b.IsChecked = v;
                syncing = false;
                all.IsChecked = v;
                RefreshMasterPending();
            };
            toggle.Click += (s, a) =>
            {
                bool show = list.Visibility != Visibility.Visible;
                list.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = show ? Loc.T("Masquer ▴") : Loc.T("Voir ▾");
                if (show) _masterExpanded.Add(key); else _masterExpanded.Remove(key);
            };
            MasterContent.Children.Add(list);
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

        private async void BtnMasterAddStep_Click(object sender, RoutedEventArgs e)
        {
            if (_master == null) return;
            string t = await MasterPromptAsync(Loc.T("✋ Étape à faire à la main"), Loc.T("Ce qu'il faut faire (ex. Ajouter le compte Google atelier@exemple.fr) :"), "", Loc.T("Ajouter"));
            if (t == null || t.Trim().Length == 0) return;
            SyncMasterFromUi();
            _master.Steps.Add(new MasterStep { Title = t.Trim(), Screen = t.IndexOf("google", StringComparison.OrdinalIgnoreCase) >= 0 ? "google" : "" });
            _masterExpanded.Add("steps");
            RenderMasterContent();
        }

        /// <summary>Master vide, rempli à la main (APK, fichiers, réglages, Wi-Fi, étapes…), sans terminal de référence.</summary>
        private async void BtnMasterNew_Click(object sender, RoutedEventArgs e)
        {
            if (_master != null && !await ShowCustomDialogAsync(Loc.T("Nouveau Master"), Loc.T("Le Master affiché sera remplacé par un Master vide (pensez à l'enregistrer d'abord s'il vous sert encore). Continuer ?"), Loc.T("Continuer"), Loc.T("Annuler"), false))
                return;
            NewMasterWorkDir();
            _master = new MasterProfile { Name = TxtMasterName.Text.Trim().Length > 0 ? TxtMasterName.Text.Trim() : Loc.F("Master — {0:dd/MM/yyyy}", DateTime.Now) };
            _masterCmdBox = null;
            TxtMasterName.Text = _master.Name;
            RenderMasterContent();
            TxtMasterSummary.Text = Loc.T("Master vide : ajoutez applis, fichiers, réglages, Wi-Fi et étapes, puis enregistrez-le.");
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
                   catWifi = Loc.T("Wi-Fi"), catHome = Loc.T("Lanceur / propriétaire"), catFiles = Loc.T("Fichiers"), catCmd = Loc.T("Commandes"), catSteps = Loc.T("À faire à la main");
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
