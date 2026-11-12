using ADBToolsNew.Core;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ADBToolsNew
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            Core.Loc.TranslateTree(this); // interface en anglais si choisi (Paramètres ▸ Général ▸ Langue)
            TxtVersion.Text = Loc.F("version {0}", App.VersionText);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Nouvelle installation : langue, taille de l'interface et thème, avant tout le reste (le téléchargement
                // des outils qui suit s'affiche déjà dans la langue choisie).
                if (AppSettings.IsNewInstall) await RunFirstSetupAsync();

                TxtStatus.Text = Loc.T("Vérification des dépendances (ADB, Scrcpy)...");

                // Court délai pour que le Splash Screen soit lisible (auparavant 2 s + 0,8 s fixes à chaque lancement)
                await Task.Delay(700);

                // Outils indispensables (adb/fastboot, scrcpy) : téléchargés depuis leurs sources officielles
                // s'ils manquent (premier lancement), au lieu d'être livrés avec le programme.
                var missing = Core.ToolsManager.MissingEssentials();
                if (missing.Count > 0 && !await InstallMissingToolsAsync(missing))
                {
                    // Sans adb, rien ne fonctionne : on s'arrête proprement. scrcpy seul manquant n'est pas bloquant.
                    if (!Core.ToolsManager.IsInstalled(Core.ToolComponent.PlatformTools))
                    {
                        Application.Current.Shutdown();
                        return;
                    }
                }

                TxtStatus.Text = Loc.T("Lancement du moteur...");
                await Task.Delay(200); // Petit délai final

                // Ouvre la fenêtre principale
                MainWindow mainApp = new MainWindow();
                mainApp.Show();

                // Ferme le Splash Screen
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.F("Erreur au démarrage : {0}", ex.Message));
                Application.Current.Shutdown();
            }
        }

        // ------------------------------------------------------------------ Premier lancement : langue, taille, thème

        private TaskCompletionSource<bool> _setupDone;
        private bool _setupUpdating;
        private string _setupLang = "fr";
        private double _setupScale = 1.0;
        private double _areaW, _areaH, _dpi = 1.0;

        /// <summary>Textes de la fenêtre de premier lancement, dans les deux langues (le choix s'applique tout de suite).</summary>
        private string S(string fr, string en) => _setupLang == "en" ? en : fr;

        private Task RunFirstSetupAsync()
        {
            _setupDone = new TaskCompletionSource<bool>();
            _setupUpdating = true;
            try
            {
                string ui = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                _setupLang = ui == "fr" ? "fr" : "en";
                var area = SystemParameters.WorkArea;
                _areaW = area.Width; _areaH = area.Height;
                try { _dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { _dpi = 1.0; }
                _setupScale = UiScaleAdvisor.Recommend(_areaW, _areaH);
                foreach (var child in SetupLangChoices.Children)
                    if (child is System.Windows.Controls.RadioButton rb) rb.IsChecked = (rb.Tag as string) == _setupLang;
                foreach (var child in SetupScaleChoices.Children)
                    if (child is System.Windows.Controls.RadioButton rb) rb.IsChecked = Math.Abs(ParseScale(rb.Tag) - _setupScale) < 0.01;
                foreach (var child in SetupThemeChoices.Children)
                    if (child is System.Windows.Controls.RadioButton rb) rb.IsChecked = (rb.Tag as string) == ThemeManager.Mode.ToString();
            }
            finally { _setupUpdating = false; }
            RefreshSetupTexts();

            // Fenêtre un peu plus grande le temps des réglages, recentrée.
            Width = 640; Height = 600;
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
            StartupPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            BtnSetupContinue.Focus();
            return _setupDone.Task;
        }

        private static double ParseScale(object tag) =>
            double.TryParse(tag as string, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.0;

        private void RefreshSetupTexts()
        {
            TxtSetupTitle.Text = S("Bienvenue dans ADB Tools Pro", "Welcome to ADB Tools Pro");
            TxtSetupIntro.Text = S("Trois réglages avant de commencer. Vous pourrez les changer à tout moment dans Paramètres.",
                                   "Three settings before you start. You can change them at any time in Settings.");
            TxtSetupLang.Text = S("Langue", "Language");
            TxtSetupSize.Text = S("Taille de l'interface", "Interface size");
            TxtSetupTheme.Text = S("Thème", "Theme");
            string[] themes = { S("Comme Windows", "Like Windows"), S("Clair", "Light"), S("Sombre", "Dark") };
            for (int i = 0; i < SetupThemeChoices.Children.Count && i < themes.Length; i++)
                ((System.Windows.Controls.RadioButton)SetupThemeChoices.Children[i]).Content = themes[i];
            TxtSetupNote.Text = S("Tout se passe sur ce PC : aucune donnée de terminal n'est envoyée sur Internet.",
                                  "Everything stays on this PC: no device data is sent over the Internet.");
            BtnSetupContinue.Content = S("Continuer", "Continue");
            RefreshSetupScale();
        }

        private void RefreshSetupScale()
        {
            double rec = UiScaleAdvisor.Recommend(_areaW, _areaH);
            int pw = (int)Math.Round(SystemParameters.PrimaryScreenWidth * _dpi), ph = (int)Math.Round(SystemParameters.PrimaryScreenHeight * _dpi);
            TxtSetupScreen.Text = string.Format(S("Écran : {0} × {1}, zoom Windows {2} %. Conseillé : {3} %.", "Screen: {0} × {1}, Windows scaling {2} %. Recommended: {3} %."),
                pw, ph, (int)Math.Round(_dpi * 100), (int)Math.Round(rec * 100));
            bool fits = UiScaleAdvisor.Fits(_setupScale, _areaW, _areaH);
            TxtSetupFit.Text = fits
                ? S("L'interface complète tient sur l'écran (aperçu à droite).", "The whole interface fits on the screen (preview on the right).")
                : S("Trop grand pour cet écran : l'affichage sera à l'étroit. Choisissez une taille plus petite.", "Too big for this screen: the display will be cramped. Choose a smaller size.");
            TxtSetupFit.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, fits ? "BrandText" : "Action");
            var f = UiScaleAdvisor.Footprint(_setupScale, _areaW, _areaH);
            // Aperçu : cadre de l'écran aux proportions réelles, rectangle de l'interface à la taille choisie.
            double boxW = 150, boxH = _areaW > 0 ? Math.Max(60, Math.Min(110, 150 * _areaH / _areaW)) : 90;
            SetupScreenBox.Width = boxW; SetupScreenBox.Height = boxH;
            SetupAppBox.Width = Math.Max(8, Math.Min(1, f.W) * (boxW - 2));
            SetupAppBox.Height = Math.Max(6, Math.Min(1, f.H) * (boxH - 2));
            SetupAppBox.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, fits ? "BrandText" : "Action");
        }

        private void SetupLang_Checked(object sender, RoutedEventArgs e)
        {
            if (_setupUpdating || !(sender is System.Windows.Controls.RadioButton rb)) return;
            _setupLang = rb.Tag as string == "en" ? "en" : "fr";
            RefreshSetupTexts();
        }

        private void SetupScale_Checked(object sender, RoutedEventArgs e)
        {
            if (_setupUpdating || !(sender is System.Windows.Controls.RadioButton rb)) return;
            _setupScale = ParseScale(rb.Tag);
            RefreshSetupScale();
        }

        private void SetupTheme_Checked(object sender, RoutedEventArgs e)
        {
            if (_setupUpdating || !(sender is System.Windows.Controls.RadioButton rb)) return;
            if (Enum.TryParse(rb.Tag as string, out AppThemeMode mode) && mode != ThemeManager.Mode) ThemeManager.SetMode(mode);
        }

        private void BtnSetupContinue_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.Language = _setupLang;
            AppSettings.Current.UiScale = _setupScale;
            AppSettings.Save();
            ThemeManager.SetMode(ThemeManager.Mode);   // enregistre aussi le thème laissé par défaut
            // Langue appliquée tout de suite : la suite du démarrage et la fenêtre principale s'ouvrent dans la bonne langue.
            Loc.Init();
            Loc.TranslateTree(this);
            TxtVersion.Text = Loc.F("version {0}", App.VersionText);
            Width = 560; Height = 430;
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
            SetupPanel.Visibility = Visibility.Collapsed;
            StartupPanel.Visibility = Visibility.Visible;
            _setupDone?.TrySetResult(true);
        }

        // ------------------------------------------------------------------ Messages dans l'écran de démarrage
        // (au lieu des boîtes Windows, pour rester dans la charte du programme)

        private TaskCompletionSource<bool> _messageAnswer;

        /// <summary>Affiche une question dans l'écran de démarrage. Retourne vrai pour le bouton principal.
        /// <paramref name="secondary"/> null = un seul bouton.</summary>
        private Task<bool> AskAsync(string title, string body, string[] items, string note, string primary, string secondary)
        {
            TxtMessageTitle.Text = title;
            TxtMessageBody.Text = body;
            TxtMessageNote.Text = note ?? "";
            TxtMessageNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
            MessageItems.Children.Clear();
            foreach (var item in items ?? new string[0])
            {
                var tb = new System.Windows.Controls.TextBlock { Text = "•  " + item, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
                tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SideInk");
                MessageItems.Children.Add(tb);
            }
            BtnMessagePrimary.Content = primary;
            BtnMessageSecondary.Content = secondary ?? "";
            BtnMessageSecondary.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;

            StartupPanel.Visibility = Visibility.Collapsed;
            MessagePanel.Visibility = Visibility.Visible;
            BtnMessagePrimary.Focus();
            _messageAnswer = new TaskCompletionSource<bool>();
            return _messageAnswer.Task;
        }

        private void CloseMessage(bool answer)
        {
            MessagePanel.Visibility = Visibility.Collapsed;
            StartupPanel.Visibility = Visibility.Visible;
            _messageAnswer?.TrySetResult(answer);
        }

        private void BtnMessagePrimary_Click(object sender, RoutedEventArgs e) => CloseMessage(true);
        private void BtnMessageSecondary_Click(object sender, RoutedEventArgs e) => CloseMessage(false);

        /// <summary>Propose puis télécharge les outils manquants, avec la progression dans l'écran de démarrage.
        /// Retourne vrai si tout s'est bien installé.</summary>
        private async Task<bool> InstallMissingToolsAsync(System.Collections.Generic.List<Core.ToolComponent> missing)
        {
            bool yes = await AskAsync(
                Loc.T("Bienvenue dans ADB Tools"),
                Loc.T("Pour fonctionner, ADB Tools a besoin de ces outils, téléchargés depuis leurs sites officiels (environ 20 Mo) :"),
                missing.Select(c => Core.ToolsManager.DisplayName(c)).ToArray(),
                Loc.T("Ils sont installés dans le dossier Tools, à côté du programme. Rien n'est installé dans Windows."),
                Loc.T("Télécharger"), Loc.T("Quitter"));

            if (!yes)
            {
                await AskAsync(
                    Loc.T("Outils manquants"),
                    Loc.T("Sans ADB, le programme ne peut pas communiquer avec les terminaux. Vous pouvez aussi placer vous-même ") +
                    Loc.T("adb.exe, fastboot.exe, AdbWinApi.dll et AdbWinUsbApi.dll (Android SDK Platform-Tools de Google) dans le dossier :"),
                    new[] { Core.ToolsManager.ToolsFolder }, null, "Fermer", null);
                return false;
            }

            ProgBar.IsIndeterminate = false;
            ProgBar.Minimum = 0;
            ProgBar.Maximum = 100;
            var progress = new Progress<Core.ToolProgress>(p =>
            {
                TxtStatus.Text = p.Message;
                if (p.Percent < 0) ProgBar.IsIndeterminate = true;
                else { ProgBar.IsIndeterminate = false; ProgBar.Value = p.Percent; }
            });

            bool allOk = true;
            foreach (var component in missing)
            {
                try
                {
                    await Core.ToolsManager.InstallAsync(component, progress);
                }
                catch (Exception ex)
                {
                    allOk = false;
                    await AskAsync(
                        Loc.T("Téléchargement impossible"),
                        Loc.F("Le téléchargement de {0} a échoué : {1}", Core.ToolsManager.DisplayName(component), ex.Message),
                        null,
                        Loc.T("Vérifiez la connexion Internet. Vous pourrez réessayer depuis Paramètres ▸ Outils tiers."),
                        Loc.T("Continuer"), null);
                }
            }

            ProgBar.IsIndeterminate = true;
            return allOk;
        }
    }
}