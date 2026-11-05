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
    // --- MODÈLE DE SAUVEGARDE DES PRÉFÉRENCES UTILISATEUR ---
    public class UserPrefs
    {
        public double WinWidth { get; set; } = 1400;
        public double WinHeight { get; set; } = 800;
        public string WindowState { get; set; } = "Normal";
        public double ConsoleHeight { get; set; } = 250;
        public bool IsSidebarCollapsed { get; set; }
        public List<string> History { get; set; } = new List<string>();
    }

    public class AndroidDevice
    {
        public string Serial { get; set; }
        public string State { get; set; }
        public string DisplayName => string.IsNullOrEmpty(State) ? Serial : (State == "device" ? Serial : $"{Serial}  [⚠️ {State.ToUpper()}]");
    }

    // --- MODÈLE POUR LE PACKAGE MANAGER ---
    public class AndroidPackageItem
    {
        public string PackageName { get; set; }
        public string Type { get; set; }     // Utilisateur ou Système
        public string Status { get; set; }   // Activé ou Désactivé

        // Textes affichés (traduits si l'interface est en anglais) ; Type et Status restent les valeurs de référence.
        public string TypeText => ADBToolsNew.Core.Loc.T(Type);
        public string StatusText => ADBToolsNew.Core.Loc.T(Status);
    }
    // --- MODÈLE POUR L'EXPLORATEUR DE FICHIERS ---
    public class AndroidFileItem
    {
        public string Icon { get; set; } // 📁 ou 📄
        public string Name { get; set; }
        public string Size { get; set; }
        public string Date { get; set; }
        public bool IsDirectory { get; set; }
    }

    // ==========================================
    // CLASSE DE STRUCTURE DES LOGS (À mettre tout en bas, hors de MainWindow)
    // ==========================================
    public class LogEntry
    {
        public string Time { get; set; }
        public string PID { get; set; }
        public string Level { get; set; }
        public string Tag { get; set; }
        public string Message { get; set; }
        public string RawLine { get; set; }
    }

    // ==========================================
    // CLASSE DE STRUCTURE DU BANC DE TEST
    // ==========================================
    public class DiagComponent
    {
        public string SystemId { get; set; }
        public string StatusIcon { get; set; }
        public string ComponentName { get; set; }
        public string Details { get; set; }
        public string Comment { get; set; }
        public string ImagePath { get; set; } = "";

        /// <summary>Nom affiché (traduit si l'interface est en anglais) ; ComponentName reste la référence.</summary>
        public string DisplayName => ADBToolsNew.Core.Loc.T(ComponentName);
    }

    public partial class MainWindow : Window
    {
        // Palette partagée pour les indicateurs colorés (jauges dashboard, RSSI/latence radar...).
        // Créées une seule fois et "freezées" : évite de recréer un BrushConverter et de reparser
        // une chaîne hexa à chaque tick d'un timer (dashboard toutes les 3s, radar/ping en continu).
        // Couleurs d'état lues dans le thème actif (clair / sombre) au moment où on les utilise.
        private static Brush ThemeBrush(string key) => (Brush)Application.Current.FindResource(key);
        private static Brush BrushSuccess => ThemeBrush("OkFg");       // Vert
        private static Brush BrushWarning => ThemeBrush("WarnFg");     // Jaune (jamais l'orange de marque)
        private static Brush BrushDanger => ThemeBrush("BadFg");       // Rouge
        private static Brush BrushAccent => ThemeBrush("BrandText");   // Vert de marque (accent par défaut)
        private static Brush BrushNeutral => ThemeBrush("Muted");      // Gris neutre

        private static Brush CreateFrozenBrush(string hex)
        {
            var brush = (Brush)new BrushConverter().ConvertFrom(hex);
            brush.Freeze();
            return brush;
        }

        // Ne garde que les N sauvegardes ".corrompu_*.bak" les plus récentes pour un fichier donné
        // (prefs.xml, AdbLibrary.json...), afin qu'elles ne s'accumulent pas indéfiniment sur une install longue durée.
        private static void PurgeOldCorruptBackups(string baseFilePath, int keepCount = 3)
        {
            try
            {
                string dir = Path.GetDirectoryName(baseFilePath);
                string baseName = Path.GetFileName(baseFilePath);
                if (string.IsNullOrEmpty(dir)) return;

                var oldBackups = new DirectoryInfo(dir)
                    .GetFiles(baseName + ".corrompu_*.bak")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Skip(keepCount);

                foreach (var f in oldBackups)
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
        }

        // ==========================================
        // CONSOLE : point d'écriture unique
        // ==========================================
        // Auparavant ~200 "ConsoleOutput.Text += ..." recopiaient et remettaient en page TOUT le texte de la
        // console à chaque ligne : l'application ralentissait au fil d'une longue session. AppendText n'ajoute
        // que la nouvelle ligne, et la taille est plafonnée.
        private const int ConsoleMaxChars = 300000;
        private int _consoleLength = -1;

        /// <summary>Ajoute du texte à la console (utilisable depuis n'importe quel thread) et défile en bas.</summary>
        private void AppendConsole(string text)
        {
            if (string.IsNullOrEmpty(text) || ConsoleOutput == null) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendConsole(text)));
                return;
            }

            if (_consoleLength < 0) _consoleLength = ConsoleOutput.Text.Length;

            ConsoleOutput.AppendText(text);
            _consoleLength += text.Length;

            if (_consoleLength > ConsoleMaxChars)
            {
                // On ne garde que les trois derniers quarts, coupés sur un début de ligne.
                string current = ConsoleOutput.Text;
                int cut = current.IndexOf('\n', Math.Max(0, current.Length - ConsoleMaxChars * 3 / 4));
                ConsoleOutput.Text = Loc.T("[... début de la console tronqué ...]") + (cut >= 0 ? current.Substring(cut) : current);
                _consoleLength = ConsoleOutput.Text.Length;
            }

            ConsoleScroll?.ScrollToEnd();
        }

        private void ClearConsole()
        {
            ConsoleOutput.Text = "";
            _consoleLength = 0;
        }

        private readonly AdbManager _adbManager;

        // Chemin absolu unique vers adb.exe : évite qu'un "adb.exe" relatif résolu via le PATH/répertoire courant
        // échoue silencieusement si l'app est lancée depuis un autre dossier de travail.
        private readonly string _adbExePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "adb.exe");

        private bool _isSidebarCollapsed = false;
        private bool _isConsoleCollapsed = false;
        private GridLength _consoleExpandedHeight = new GridLength(250);

        private DispatcherTimer _deviceWatcherTimer;
        private DispatcherTimer _statsWatcherTimer;
        private List<string> _lastKnownDevices = new List<string>();

        // Auto-relance du démon ADB : si le watcher échoue plusieurs fois de suite (démon planté/bloqué),
        // on tente un kill-server + start-server plutôt que de rester bloqué indéfiniment.
        private int _consecutiveWatcherFailures = 0;
        private const int MaxConsecutiveWatcherFailuresBeforeRestart = 3;

        // Variables pour le Package Manager
        private List<AndroidPackageItem> _allPackages = new List<AndroidPackageItem>();
        private DispatcherTimer _snifferTimer;
        private bool _isSnifferActive = false;

        public MainWindow()
        {
            InitializeComponent();
            Core.Loc.TranslateTree(this); // interface en anglais si choisi (Paramètres ▸ Général ▸ Langue)
            // Taille de l'interface appliquée AVANT le premier affichage : appliquée après, la fenêtre (transparente,
            // coins arrondis) n'était pas redessinée correctement jusqu'à un agrandissement / une réduction.
            ApplyUiScale(Core.AppSettings.Current.UiScale);
            // La découpe des coins arrondis suit chaque changement de taille réel du contenu.
            AppContentGrid.SizeChanged += (s, e) => UpdateRootClip();
            _adbManager = new AdbManager();
            _radarService = new RadarService(_adbManager);
            _fileExplorerService = new FileExplorerService(_adbManager);
            _packageManagerService = new PackageManagerService(_adbManager);
            _foregroundAppService = new ForegroundAppService(_adbManager);
            _diagnosticService = new DiagnosticService(_adbManager);
            _screenTestService = new ScreenTestService(_adbManager);
            _mirroringService = new MirroringService();

            // FIX 1 : Correction du plein écran
            this.MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
            this.MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;

            // Les événements Loaded/Closing sont câblés depuis le XAML (Window_Loaded / Window_Closing)
            // et regroupent tout le cycle de vie de la fenêtre (préférences + moteur ADB) en un seul point d'entrée chacun.

            // La surveillance des terminaux (qui lance adb) démarre dans Window_Loaded, une fois la fenêtre affichée.
            InitializeAdbLibrary();
        }

        // ==========================================
        // GESTION DU CYCLE DE VIE ADB (AJOUTS)
        // ==========================================

        private async Task InitializeAdbDaemonAsync()
        {
            if (!File.Exists(_adbExePath))
            {
                throw new FileNotFoundException(Loc.F("Le binaire ADB est introuvable au chemin : {0}", _adbExePath));
            }

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _adbExePath,
                    Arguments = "start-server",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            })
            {
                process.Start();

                // 🔒 C'est ici que la magie opère : le démon ADB est capturé !
                ChildProcessTracker.AddProcess(process);

                await Task.Run(() => process.WaitForExit());
            }
        }

        // ==========================================
        // OUVERTURE DE FICHIERS / DOSSIERS DANS WINDOWS
        // ==========================================
        // Point unique pour Process.Start côté Windows : libère le handle du process lancé (auparavant jamais
        // libéré) et ne fait jamais planter l'action en cours si l'ouverture échoue (aucune application associée
        // au format, dossier supprimé entre-temps, PC verrouillé...) : un message en console suffit.

        /// <summary>Ouvre l'explorateur Windows avec le fichier sélectionné.</summary>
        private void ShowInExplorer(string filePath) => StartShellProcess("explorer.exe", $"/select,\"{filePath}\"", filePath);

        /// <summary>Ouvre un dossier dans l'explorateur Windows.</summary>
        private void OpenFolder(string folderPath) => StartShellProcess("explorer.exe", $"\"{folderPath}\"", folderPath);

        /// <summary>Ouvre un fichier avec l'application associée par Windows (image, vidéo, document...).</summary>
        private void OpenWithShell(string filePath) => StartShellProcess(filePath, null, filePath);

        private void StartShellProcess(string fileName, string arguments, string displayedPath)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName) { UseShellExecute = true };
                if (arguments != null) psi.Arguments = arguments;
                using (Process.Start(psi)) { }
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Ouverture impossible de « {1} » : {2}", DateTime.Now, displayedPath, ex.Message));
            }
        }

        // 🔒 Évite que la boîte de dialogue de confirmation de fermeture ne se rouvre en boucle une fois validée.
        private bool _forceCloseConfirmed = false;

        private async void ConfirmCloseDuringSideloadAsync()
        {
            bool confirmed = await ShowCustomDialogAsync(
                Loc.T("⚠️ Sideload en cours"),
                Loc.T("Un Sideload (flashage OS) est en cours !\n\nFermer l'application maintenant peut endommager le terminal de façon irréversible.\n\nVoulez-vous vraiment quitter ?"),
                Loc.T("Quitter quand même"),
                Loc.T("Annuler"),
                true);

            if (confirmed)
            {
                _forceCloseConfirmed = true;
                this.Close(); // Redéclenche Closing, qui laissera passer cette fois-ci et fera le nettoyage
            }
        }

        // 1. DÉFINITION DE L'OBJET COMMANDE

        // ==========================================
        // 0. SAUVEGARDE ET CHARGEMENT (PERSISTANCE)
        // ==========================================
        private string GetPrefsPath()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ADBToolsPro");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "prefs.xml");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateThemeUi();
            TxtTitleVersion.Text = Loc.F("version {0}", App.VersionText);
            UpdateWindowFrame();
            UpdateReportTemplateButtons();
            _ = CheckToolUpdatesInBackgroundAsync(); // mises à jour des outils tiers, une fois par jour
            _ = CheckAppUpdateAtStartupAsync();      // nouvelle version d'ADB Tools, à chaque ouverture
            // --- Chargement des préférences utilisateur (taille fenêtre, historique...) ---
            string prefsPath = GetPrefsPath();
            if (File.Exists(prefsPath))
            {
                try
                {
                    var xs = new XmlSerializer(typeof(UserPrefs));
                    using (var fs = new FileStream(prefsPath, FileMode.Open))
                    {
                        var prefs = (UserPrefs)xs.Deserialize(fs);

                        if (prefs.WinWidth > 600) this.Width = prefs.WinWidth;
                        if (prefs.WinHeight > 400) this.Height = prefs.WinHeight;

                        if (Enum.TryParse<WindowState>(prefs.WindowState, out var state))
                        {
                            this.WindowState = state;
                            UpdateMaximizeIcon(); // Icône juste dès le premier affichage si l'app rouvre déjà agrandie
                        }

                        if (prefs.ConsoleHeight >= 100 && ConsoleRowDefinition != null)
                        {
                            ConsoleRowDefinition.Height = new GridLength(prefs.ConsoleHeight);
                        }

                        if (prefs.IsSidebarCollapsed) BtnToggleSidebar_Click(null, null);

                        if (prefs.History != null)
                        {
                            foreach (var cmd in prefs.History)
                                AddCommandToHistory(cmd);
                        }
                    }
                }
                catch (Exception exCorrupt)
                {
                    // Profil corrompu : on l'isole (pour ne pas replanter à chaque démarrage) et on repart sur les valeurs par défaut.
                    try
                    {
                        string backupPath = prefsPath + $".corrompu_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                        File.Move(prefsPath, backupPath);
                        PurgeOldCorruptBackups(prefsPath);
                    }
                    catch { }

                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Profil utilisateur corrompu ({1}), réinitialisé aux valeurs par défaut. Ancien fichier conservé en .bak.", DateTime.Now, exCorrupt.GetType().Name));
                }
            }
            else
            {
                AppendConsole(Loc.T("\n[INFO] Initialisation du profil utilisateur..."));
            }

            // Centrage manuel (WindowStartupLocation="Manual") : fait ici, une fois la taille définitive
            // connue (par défaut ou restaurée des préférences), plutôt que de laisser WPF centrer AVANT que
            // la taille ne change — ce qui décalait la fenêtre — et pour contourner un bug WPF connu où
            // AllowsTransparency="True" fausse légèrement le centrage natif "CenterScreen".
            CenterOnScreen();
            ApplyDisplaySettingsAtStartup(); // taille de l'interface, console (Paramètres ▸ Général)

            // --- Initialisation du moteur ADB (démarrage du démon) ---
            try
            {
                await InitializeAdbDaemonAsync();
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("❌ Erreur Fatale"), Loc.F("Erreur critique d'initialisation ADB : {0}", ex.Message), "OK", null, true);
            }

            if (ChildProcessTracker.DisabledReason != null)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Nettoyage automatique des processus indisponible sur ce PC ({1}). L'application fonctionne normalement, mais adb/scrcpy pourraient rester ouverts après un plantage.", DateTime.Now, ChildProcessTracker.DisabledReason));
            }

            // --- Surveillance des terminaux : seulement après le démarrage du démon ---
            StartDeviceWatcher();
            StartStatsWatcher();

            OpenStartPage(); // page choisie dans Paramètres ▸ Général
            ShowWelcomeIfFirstLaunch(); // accueil au premier lancement (Paramètres ▸ Prise en main)
            _ = ImportTeamProfileFromCommandLineAsync(); // .adbprofil glissé sur l'exe
            _ = PurgeHistoryAtStartupAsync(); // durée de conservation de l'historique (Paramètres ▸ Historique)
        }

        /// <summary>Centre la fenêtre sur l'écran de travail actuel, en tenant compte de la taille réelle
        /// (par défaut ou restaurée) — nécessaire depuis le passage à WindowStartupLocation="Manual".</summary>
        private void CenterOnScreen()
        {
            // Petit écran (PC portable) : la fenêtre ne dépasse jamais la zone de travail (taille par défaut
            // 1400 × 800, ou taille mémorisée sur un écran plus grand).
            var area = SystemParameters.WorkArea;
            if (this.Width > area.Width) this.Width = Math.Max(this.MinWidth, area.Width * 0.96);
            if (this.Height > area.Height) this.Height = Math.Max(this.MinHeight, area.Height * 0.96);
            this.Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - this.Width) / 2;
            this.Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - this.Height) / 2;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 🛑 GARDE-FOU SIDELOAD : on ne laisse jamais fermer l'app en plein flashage OS (risque de brick du terminal).
            // Closing est synchrone : on annule d'abord la fermeture, puis on affiche la boîte de dialogue maison
            // (asynchrone) ; si l'utilisateur confirme, on redéclenche Close() nous-mêmes.
            if (_isSideloading && !_forceCloseConfirmed)
            {
                e.Cancel = true;
                ConfirmCloseDuringSideloadAsync();
                return;
            }

            // --- Sauvegarde des préférences utilisateur ---
            try
            {
                double currentWidth = this.WindowState == WindowState.Normal ? this.Width : this.RestoreBounds.Width;
                double currentHeight = this.WindowState == WindowState.Normal ? this.Height : this.RestoreBounds.Height;
                double consoleH = ConsoleRowDefinition != null ? ConsoleRowDefinition.Height.Value : 250;

                var prefs = new UserPrefs
                {
                    WinWidth = currentWidth,
                    WinHeight = currentHeight,
                    WindowState = this.WindowState.ToString(),
                    ConsoleHeight = consoleH,
                    IsSidebarCollapsed = this._isSidebarCollapsed,
                    History = CmbCommandHistory.Items.Cast<string>().ToList()
                };

                var xs = new XmlSerializer(typeof(UserPrefs));
                using (var fs = new FileStream(GetPrefsPath(), FileMode.Create))
                {
                    xs.Serialize(fs, prefs);
                }
            }
            catch { }

            // 🧹 NETTOYAGE COMPLET : on n'attend plus uniquement le Job Object pour arrêter les flux en arrière-plan.
            try { StopLogcat(); } catch { }
            try { StopRadar(); } catch { }
            try { StopGetEventLab(); } catch { }
            try { StopScanTestOnExit(); } catch { }
            try { StopDeviceTestsOnExit(); } catch { }

            if (_isRecording)
            {
                try
                {
                    if (_recordingProcess != null && !_recordingProcess.HasExited)
                    {
                        _recordingProcess.Kill();
                    }
                }
                catch { }
                finally
                {
                    _isRecording = false;
                }
            }

            try
            {
                if (File.Exists(_adbExePath))
                {
                    // Demande gentiment au serveur ADB de s'éteindre (Synchrone car on est en train de fermer)
                    using (var killServer = Process.Start(new ProcessStartInfo
                    {
                        FileName = _adbExePath,
                        Arguments = "kill-server",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }))
                    {
                        killServer?.WaitForExit(2000); // 2 secondes max
                    }
                }
            }
            catch
            {
                // Ignore silencieusement. Le ChildProcessTracker fera un Hard Kill de toute façon.
            }
        }

        // ==========================================
        // SYSTÈME DE NAVIGATION PAR ONGLETS (SIDEBAR)
        // ==========================================
        private void UpdateNavButtonsUI(Button activeBtn)
        {
            // Liste complète de tous les boutons du menu latéral (BtnNavLibrary ajouté à la fin)
            Button[] navBtns = { BtnNavDashboard, BtnNavMirroring, BtnNavPackages, BtnNavExplorer, BtnNavTools, BtnNavLogs, BtnNavDiag, BtnNavLibrary, BtnNavRadar, BtnNavCompare, BtnNavSettings };

            foreach (var btn in navBtns)
            {
                if (btn != null)
                {
                    // Réinitialise la valeur locale pour réactiver le survol IsMouseOver du Style XAML
                    btn.ClearValue(Button.BackgroundProperty);
                    btn.ClearValue(Button.ForegroundProperty);
                    btn.ClearValue(Button.FontWeightProperty);
                }
            }

            if (activeBtn != null)
            {
                // Applique l'état sélectionné uniquement sur le bouton actif
                // SetResourceReference (et non FindResource) : la couleur suit le thème clair / sombre en direct.
                activeBtn.SetResourceReference(Button.BackgroundProperty, "SideActiveBg");
                activeBtn.SetResourceReference(Button.ForegroundProperty, "SideActiveInk");
                activeBtn.FontWeight = FontWeights.SemiBold;
            }
            // --- GESTION DES ANIMATIONS DE TÂCHES EN FOND ---

            // --- GESTION DES ANIMATIONS DE TÂCHES EN FOND ---

            // 1. Radar Wi-Fi
            if (_radarService.IsRunning) AnimateNavButton(BtnNavRadar, true);

            // 2. Logcat Live
            if (_logcatService.IsRunning) AnimateNavButton(BtnNavLogs, true);

            // 3. Package Sniffer
            if (_isSnifferActive) AnimateNavButton(BtnNavPackages, true);

            // 4. Enregistrement Vidéo (NOUVEAU)
            if (_isRecording) AnimateNavButton(BtnNavMirroring, true);
        }

        private void NavDashboard_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null) MainTabControl.SelectedIndex = 0;
            UpdateNavButtonsUI(BtnNavDashboard);
        }

        private void NavPackages_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = 1;
                UpdateNavButtonsUI(BtnNavPackages);
                _ = LoadDevicePackagesAsync(); // lancé sans attendre : la méthode gère ses propres erreurs
            }
        }

        private void NavExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = 2;
                UpdateNavButtonsUI(BtnNavExplorer);
                LoadCurrentFolderAsync();
            }
        }

        private void NavMirroring_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = 3;
                UpdateNavButtonsUI(BtnNavMirroring);
                UpdateCaptureFolderStats();
            }
        }

        private async void NavTools_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = 4;
                UpdateNavButtonsUI(BtnNavTools);
                await RefreshToolsStateAsync(); // ACTUALISATION EN TEMPS RÉEL
            }
        }

        private void NavLogs_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = 5; // Index 5 = 6ème onglet (Logs)
                UpdateNavButtonsUI(BtnNavLogs);

                // Lie le tableau à notre collection de données la première fois
                if (LogsDataGrid.ItemsSource == null)
                {
                    LogsDataGrid.ItemsSource = _displayLogs;
                }
            }
        }

        private void NavDiag_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = 6; // Index 6 = 7ème onglet
                UpdateNavButtonsUI(BtnNavDiag);

                // Initialise la checklist si elle est vide
                if (DiagChecklistGrid.ItemsSource == null)
                {
                    InitializeDiagChecklist();
                }
            }
        }

        private void NavLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = 7; // Index 7 = 8ème onglet (Bibliothèque)
                UpdateNavButtonsUI(BtnNavLibrary);
            }
        }

        private void NavRadar_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = 8; // Index 8 = 9ème onglet (Radar Wi-Fi)
                UpdateNavButtonsUI(BtnNavRadar);
            }
        }
        // ==========================================
        // 4. WATCHER D'APPAREILS & BOUTONS
        // ==========================================
        private void StartDeviceWatcher()
        {
            _deviceWatcherTimer = new DispatcherTimer();
            _deviceWatcherTimer.Interval = TimeSpan.FromSeconds(2);
            _deviceWatcherTimer.Tick += async (sender, args) =>
            {
                _deviceWatcherTimer.Stop();
                try
                {
                    var result = await _adbManager.ExecuteAsync("devices");
                    if (result.IsSuccess)
                    {
                        _consecutiveWatcherFailures = 0;

                        var currentDevices = ParseAdbDevices(result.StandardOutput);
                        var currentDeviceNames = currentDevices.Select(d => d.DisplayName).ToList();

                        if (!_lastKnownDevices.SequenceEqual(currentDeviceNames))
                        {
                            _lastKnownDevices = currentDeviceNames;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                string previousSerial = (DeviceComboBox.SelectedItem as AndroidDevice)?.Serial;
                                _isRefreshingDeviceList = true;
                                DeviceComboBox.ItemsSource = null;
                                if (currentDevices.Count > 0)
                                {
                                    DeviceComboBox.ItemsSource = currentDevices;
                                    SelectDeviceOrFirst(currentDevices, previousSerial);
                                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [WATCHER] {1} appareil(s) branché(s).", DateTime.Now, currentDevices.Count));
                                }
                                else
                                {
                                    var emptyList = new List<AndroidDevice> { new AndroidDevice { Serial = "Aucun appareil détecté...", State = "" } };
                                    DeviceComboBox.ItemsSource = emptyList;
                                    DeviceComboBox.SelectedIndex = 0;
                                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [WATCHER] Appareils débranchés.", DateTime.Now));
                                }
                                _isRefreshingDeviceList = false;
                                if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scrollViewer) scrollViewer.ScrollToEnd();
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [WATCHER ERROR] {1}", DateTime.Now, ex.Message)); });

                    _consecutiveWatcherFailures++;
                    if (_consecutiveWatcherFailures >= MaxConsecutiveWatcherFailuresBeforeRestart)
                    {
                        _consecutiveWatcherFailures = 0;
                        await TryRestartAdbDaemonAsync();
                    }
                }
                finally { _deviceWatcherTimer.Start(); }
            };
            _deviceWatcherTimer.Start();
        }

        /// <summary>
        /// Tente de relancer le démon ADB (kill-server + start-server) après plusieurs échecs consécutifs
        /// du watcher, sans attendre une intervention manuelle de l'utilisateur.
        /// </summary>
        private async Task TryRestartAdbDaemonAsync()
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [WATCHER] ⚠️ Le démon ADB semble bloqué, tentative de relance automatique...", DateTime.Now)); });

                await _adbManager.ExecuteAsync("kill-server", TimeSpan.FromSeconds(5));
                await _adbManager.ExecuteAsync("start-server", TimeSpan.FromSeconds(10));

                Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [WATCHER] ✅ Démon ADB relancé.", DateTime.Now)); });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [WATCHER] ❌ Échec de la relance automatique du démon ADB : {1}", DateTime.Now, ex.Message)); });
            }
        }

        /// <summary>
        /// Resélectionne le terminal sur lequel l'utilisateur travaillait quand la liste change (ex. branchement
        /// d'un 2e appareil). Auparavant la sélection revenait toujours au premier de la liste.
        /// </summary>
        private void SelectDeviceOrFirst(List<AndroidDevice> devices, string serial)
        {
            int index = string.IsNullOrEmpty(serial) ? -1 : devices.FindIndex(d => d.Serial == serial);
            DeviceComboBox.SelectedIndex = index >= 0 ? index : 0;
        }

        // Dernier terminal (serial + état) dont les infos ont été chargées : évite de tout recharger quand la
        // liste est simplement reconstruite avec le même appareil sélectionné.
        private string _lastLoadedDeviceKey;
        private bool _isRefreshingDeviceList;

        private List<AndroidDevice> ParseAdbDevices(string adbOutput)
        {
            var devices = new List<AndroidDevice>();
            var lines = adbOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("List of devices attached") || line.StartsWith("*") || string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) devices.Add(new AndroidDevice { Serial = parts[0].Trim(), State = parts[parts.Length - 1].Trim() });
            }
            return devices;
        }

        private async void DeviceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;

            // Pendant la reconstruction de la liste (ItemsSource remplacé), la sélection passe brièvement à null :
            // ce n'est pas une déconnexion, on ignore cet état transitoire.
            if (selectedDevice == null && _isRefreshingDeviceList) return;

            if (selectedDevice == null || string.IsNullOrEmpty(selectedDevice.State))
            {
                _lastLoadedDeviceKey = null;
                if (StatusIndicator != null)
                {
                    StatusIndicator.Fill = (SolidColorBrush)FindResource("DangerColor");
                    StatusIndicator.ToolTip = Loc.T("Déconnecté");
                }

                TxtModel.Text = Loc.T("En attente...");
                TxtOsVersion.Text = Loc.T("En attente...");
                TxtBuild.Text = Loc.T("En attente...");
                TxtSdkApi.Text = Loc.T("En attente...");
                TxtSerial.Text = Loc.T("En attente...");
                TxtUptime.Text = Loc.T("En attente...");
                TxtScreenInfo.Text = Loc.T("En attente...");
                return;
            }

            if (selectedDevice.State != "device")
            {
                _lastLoadedDeviceKey = null;
                if (StatusIndicator != null)
                {
                    StatusIndicator.Fill = (SolidColorBrush)FindResource("AccentColor");
                    StatusIndicator.ToolTip = Loc.F("Attention : {0}", selectedDevice.State.ToUpper());
                }

                TxtModel.Text = Loc.T("Autorisation requise");
                TxtOsVersion.Text = "--";
                TxtBuild.Text = "--";
                TxtSdkApi.Text = "--";
                TxtSerial.Text = "--";
                TxtUptime.Text = "--";
                TxtScreenInfo.Text = "--";
                return;
            }

            if (StatusIndicator != null)
            {
                StatusIndicator.Fill = (SolidColorBrush)FindResource("StatusSuccess");
                StatusIndicator.ToolTip = Loc.T("Connecté et Prêt");
            }

            string deviceKey = selectedDevice.Serial + "|" + selectedDevice.State;
            if (deviceKey == _lastLoadedDeviceKey) return; // Même terminal resélectionné : infos déjà affichées
            _lastLoadedDeviceKey = deviceKey;

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] Nouvel appareil détecté ({1}). Extraction auto des infos système...", DateTime.Now, selectedDevice.Serial));
            if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();

            await LoadStaticDeviceInfoAsync(selectedDevice);

            // Si on est sur l'onglet Package Manager, on recharge la liste
            if (MainTabControl != null && MainTabControl.SelectedIndex == 1)
            {
                await LoadDevicePackagesAsync();
            }
        }

        private async System.Threading.Tasks.Task LoadStaticDeviceInfoAsync(AndroidDevice device)
        {
            try
            {
                string serial = device.Serial;

                // Un seul "getprop" pour toutes les propriétés (auparavant 5 appels adb successifs).
                var props = await _adbManager.GetAllPropsAsync(serial);
                string Prop(string key) => props.TryGetValue(key, out var v) ? v.Trim() : "";

                string brand = Prop("ro.product.manufacturer");
                string model = Prop("ro.product.model");
                if (string.IsNullOrEmpty(model)) model = "Inconnu";

                string fullModelDisplay = model;
                if (!string.IsNullOrEmpty(brand))
                {
                    brand = char.ToUpper(brand[0]) + brand.Substring(1).ToLower();
                    if (!model.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) fullModelDisplay = $"{brand} {model}";
                }

                string osVersion = Prop("ro.build.version.release") is string rel && rel.Length > 0 ? $"Android {rel}" : "Inconnu";
                string build = Prop("ro.build.display.id") is string bid && bid.Length > 0 ? bid : "Inconnu";
                string sdkApi = Prop("ro.build.version.sdk") is string sdk && sdk.Length > 0 ? $"API {sdk}" : "Inconnu";

                // Uptime : "cat /proc/uptime" renvoie "12345.67 98765.43" (secondes écoulées, puis somme du temps
                // d'inactivité cumulé sur tous les cœurs) — seul le premier nombre nous intéresse.
                var uptimeResult = await _adbManager.ExecuteAsync($"-s {serial} shell cat /proc/uptime");
                string uptimeDisplay = "Inconnu";
                if (uptimeResult.IsSuccess)
                {
                    string firstToken = (uptimeResult.StandardOutput ?? "").Trim().Split(' ')[0];
                    if (double.TryParse(firstToken, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double uptimeSeconds))
                        uptimeDisplay = FormatUptime(uptimeSeconds);
                }

                // Résolution + densité : réutilise DeviceDisplayInfo (déjà utilisé pour le test d'écran et la
                // grille tactile), pas de nouvelle logique de parsing.
                var (screenWidth, screenHeight) = await Core.DeviceDisplayInfo.ResolveScreenSizeAsync(_adbManager, serial);
                var density = await Core.DeviceDisplayInfo.ResolveDensityAsync(_adbManager, serial);
                string screenDisplay = density.HasValue
                    ? $"{screenWidth}x{screenHeight} • {density.Value.Dpi}dpi{(density.Value.IsOverride ? " (custom)" : "")}"
                    : $"{screenWidth}x{screenHeight}";

                // =================================================================
                // 🛡️ AUTO-HEALING : NETTOYAGE DE SÉCURITÉ AU BRANCHEMENT
                // =================================================================
                await _adbManager.ExecuteAsync($"-s {serial} shell settings put system show_touches 0");


                Application.Current.Dispatcher.Invoke(() =>
                {
                    TxtModel.Text = fullModelDisplay;
                    TxtOsVersion.Text = osVersion;
                    TxtBuild.Text = build;
                    TxtSdkApi.Text = sdkApi;
                    TxtSerial.Text = serial;
                    TxtUptime.Text = uptimeDisplay;
                    TxtScreenInfo.Text = screenDisplay;
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Appareil {1} configuré et prêt !", DateTime.Now, fullModelDisplay));
                    if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scrollViewer) scrollViewer.ScrollToEnd();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur d'extraction : {1}", DateTime.Now, ex.Message)); });
            }
        }

        private static string FormatUptime(double totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(totalSeconds);
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}j {ts.Hours}h";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}min";
            return $"{ts.Minutes}min";
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;
            if (selectedDevice == null || string.IsNullOrEmpty(selectedDevice.State) || selectedDevice.State != "device")
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Veuillez sélectionner un appareil valide et autorisé.", DateTime.Now));
                return;
            }

            BtnConnect.IsEnabled = false;
            await LoadStaticDeviceInfoAsync(selectedDevice);
            BtnConnect.IsEnabled = true;
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            BtnRefresh.IsEnabled = false;
            try
            {
                var result = await _adbManager.ExecuteAsync("devices");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (result.IsSuccess)
                    {
                        var currentDevices = ParseAdbDevices(result.StandardOutput);
                        _lastKnownDevices = currentDevices.Select(d => d.DisplayName).ToList();
                        string previousSerial = (DeviceComboBox.SelectedItem as AndroidDevice)?.Serial;
                        _lastLoadedDeviceKey = null; // Actualisation manuelle : on recharge les infos du terminal
                        _isRefreshingDeviceList = true;
                        DeviceComboBox.ItemsSource = null;
                        if (currentDevices.Count > 0)
                        {
                            DeviceComboBox.ItemsSource = currentDevices;
                            SelectDeviceOrFirst(currentDevices, previousSerial);
                        }
                        else
                        {
                            var emptyList = new List<AndroidDevice> { new AndroidDevice { Serial = "Aucun appareil détecté...", State = "" } };
                            DeviceComboBox.ItemsSource = emptyList; DeviceComboBox.SelectedIndex = 0;
                        }
                        _isRefreshingDeviceList = false;
                    }
                });
            }
            catch { }
            finally { Application.Current.Dispatcher.Invoke(() => { BtnRefresh.IsEnabled = true; }); }
        }

        // Permet de déplacer la fenêtre en cliquant sur la barre d'en-tête
        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                this.DragMove();
            }
        }

        // Close() et non Application.Shutdown() : Shutdown ferme la fenêtre en ignorant e.Cancel dans
        // Window_Closing, ce qui contournait le garde-fou Sideload (flashage tué en cours) et sautait la
        // sauvegarde des préférences. Fermer la fenêtre principale suffit à terminer l'application.
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Thème : voir MainWindow.Settings.cs (bouton « Thème : … » et page Paramètres).

        /// <summary>Orange « Action » du thème actif, à 60 % d'opacité (pulsation des boutons du menu quand une tâche tourne).</summary>
        private static System.Windows.Media.Color PulseColor()
        {
            var c = (System.Windows.Media.Color)Application.Current.FindResource("C.Action");
            return System.Windows.Media.Color.FromArgb(0x99, c.R, c.G, c.B);
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowState = WindowState.Maximized;
            }
        }

        // L'état peut aussi changer sans passer par BtnMaximize_Click (double-clic sur la barre de titre,
        // Aero Snap en haut de l'écran...) — l'icône doit rester juste dans tous les cas.
        private void Window_StateChanged(object sender, EventArgs e)
        {
            UpdateMaximizeIcon();
            UpdateWindowFrame();
        }

        /// <summary>Bascule l'icône du bouton Agrandir/Restaurer entre le simple carré (Segoe MDL2 Assets
        /// "ChromeMaximize", ) et les deux carrés superposés (, "ChromeRestore") — les mêmes
        /// glyphes que les fenêtres natives de Windows utilisent pour ce même bouton.</summary>
        private void UpdateMaximizeIcon()
        {
            if (BtnMaximize == null) return;
            BtnMaximize.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        // ==========================================
        // === BOUTONS HELP & ABOUT (BARRE LATÉRALE)=
        // ==========================================

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            string helpText =
                "## Prérequis de connexion\n" +
                "- Le débogage USB doit être activé dans les options développeur du terminal.\n" +
                "- Utilisez un câble data (transfert de données), et non un simple câble de charge.\n" +
                "- Au premier branchement, acceptez sur le terminal la demande « Autoriser le débogage USB ».\n\n" +
                "## Problèmes fréquents\n" +
                "- Statut « Unauthorized » : regardez l'écran du terminal et cochez « Toujours autoriser cet ordinateur ».\n" +
                "- Statut « Offline » : débranchez le câble, attendez 3 secondes et rebranchez.\n" +
                "- Terminal invisible : vérifiez que le pilote USB du fabricant (Zebra, Honeywell, Samsung...) est installé sur ce PC, ou essayez un autre port USB.\n" +
                "- ADB ou scrcpy introuvable : Paramètres ▸ Outils tiers, puis « Télécharger ».\n\n" +
                "## Connexion sans câble (Wi-Fi)\n" +
                "- Android 10 et moins : branchez le terminal en USB, puis « Wi-Fi Pairing » ▸ « Auto-Connecter ».\n" +
                "- Android 11 et plus : Options développeur ▸ Débogage sans fil ▸ « Associer avec un code », puis saisissez l'adresse et le code dans « Wi-Fi Pairing ».";
            DocumentWindow.ShowText(this, "Aide et dépannage", "Aide et dépannage", helpText);
        }

        private async void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            string aboutText = "ADB Tools Pro - Édition 2027\n" +
                               $"Version {App.VersionText}\n\n" +
                               "Outil libre (licence GNU GPL v3) pour simplifier le diagnostic, le contrôle et le dépannage des terminaux Android, de toutes marques.\n\n" +
                               "Technologies intégrées :\n" +
                               "• Android Debug Bridge (ADB)\n" +
                               "• Scrcpy (Mirroring Open-Source par Genymobile)\n" +
                               "• FFmpeg (Traitement Vidéo)\n" +
                               "• DocumentFormat.OpenXml (Génération des rapports Word)\n\n" +
                               "© 2026 - Mr.Kanard";

            await ShowCustomDialogAsync(Loc.T("ℹ️ À propos"), aboutText, Loc.T("Fermer"), "", false);
        }

        private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            _isSidebarCollapsed = !_isSidebarCollapsed;
            SidebarColumn.Width = _isSidebarCollapsed ? new GridLength(70) : new GridLength(260);
            SidebarLogoGroup.Visibility = _isSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            BtnToggleSidebar.HorizontalAlignment = _isSidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
            BtnToggleSidebar.Content = _isSidebarCollapsed ? "➔" : "◀";
        }

        /// <summary>Réduit/restaure la console (utile sur certains onglets, pas sur d'autres) : rend
        /// toute la hauteur à la zone de travail sans perdre la taille choisie par l'utilisateur.</summary>
        private void BtnToggleConsole_Click(object sender, RoutedEventArgs e)
        {
            _isConsoleCollapsed = !_isConsoleCollapsed;

            if (_isConsoleCollapsed)
            {
                _consoleExpandedHeight = ConsoleRowDefinition.Height;
                ConsoleRowDefinition.MinHeight = 0;
                ConsoleRowDefinition.Height = new GridLength(48);
                ConsoleBorder.Padding = new Thickness(15, 8, 15, 8);
                ConsoleHeaderRow.Margin = new Thickness(0);
                ConsoleScroll.Visibility = Visibility.Collapsed;
                ConsoleFooterRow.Visibility = Visibility.Collapsed;
                ConsoleSplitter.IsEnabled = false;
                BtnToggleConsole.Content = "▸";
                BtnToggleConsole.ToolTip = Loc.T("Afficher la console");
            }
            else
            {
                ConsoleRowDefinition.MinHeight = 100;
                ConsoleRowDefinition.Height = _consoleExpandedHeight;
                ConsoleBorder.Padding = new Thickness(15);
                ConsoleHeaderRow.Margin = new Thickness(0, 0, 0, 10);
                ConsoleScroll.Visibility = Visibility.Visible;
                ConsoleFooterRow.Visibility = Visibility.Visible;
                ConsoleSplitter.IsEnabled = true;
                BtnToggleConsole.Content = "▾";
                BtnToggleConsole.ToolTip = Loc.T("Réduire la console");
            }
        }

        private DateTime _wifiPairingPopupLastClosed = DateTime.MinValue;

        /// <summary>
        /// StaysOpen="False" ferme déjà le Popup au clic sur ce même bouton (il est "hors" du popup) juste
        /// avant que Click ne se déclenche pour ce clic-là : sans garde-fou, IsOpen serait déjà à false ici
        /// et on le repasserait aussitôt à true, donnant l'impression que le popup se rouvre tout seul.
        /// </summary>
        private void BtnWifiPairingToggle_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.Now - _wifiPairingPopupLastClosed).TotalMilliseconds < 200) return;
            WifiPairingPopup.IsOpen = true;
        }

        private void WifiPairingPopup_Closed(object sender, EventArgs e)
        {
            _wifiPairingPopupLastClosed = DateTime.Now;
        }

        // ==========================================
        // 🎨 EFFETS VISUELS & ANIMATIONS
        // ==========================================
        private void AnimateNavButton(System.Windows.Controls.Button navButton, bool isRunning)
        {
            if (isRunning)
            {
                // 1. On crée un pinceau transparent qu'on applique au bouton
                var animatedBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
                navButton.Background = animatedBrush;

                // 2. On crée l'animation (Va de Transparent vers ton Orange "#FF9600" à 30% d'opacité)
                // 4D en hexadécimal = 30% d'opacité environ
                var pulseAnimation = new System.Windows.Media.Animation.ColorAnimation
                {
                    To = PulseColor(), // orange « Action » du thème actif, à 60 % d'opacité
                    Duration = TimeSpan.FromSeconds(0.8), // Vitesse de la respiration
                    AutoReverse = true, // Effet d'aller-retour
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever // En boucle infinie
                };

                // 3. On lance l'animation sur la couleur
                animatedBrush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, pulseAnimation);
            }
            else
            {
                // 4. Si on arrête, on efface la couleur locale forcée pour rendre le contrôle au XAML
                navButton.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
            }
        }
    }
}
