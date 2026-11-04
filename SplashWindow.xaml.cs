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
            TxtVersion.Text = $"Édition 2027 • v{App.VersionText}";
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                TxtStatus.Text = "Vérification des dépendances (ADB, Scrcpy)...";

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

                TxtStatus.Text = "Lancement du moteur...";
                await Task.Delay(200); // Petit délai final

                // Ouvre la fenêtre principale
                MainWindow mainApp = new MainWindow();
                mainApp.Show();

                // Ferme le Splash Screen
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur au démarrage : {ex.Message}");
                Application.Current.Shutdown();
            }
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
                "Bienvenue dans ADB Tools",
                "Pour fonctionner, ADB Tools a besoin de ces outils, téléchargés depuis leurs sites officiels (environ 20 Mo) :",
                missing.Select(c => Core.ToolsManager.DisplayName(c)).ToArray(),
                "Ils sont installés dans le dossier Tools, à côté du programme. Rien n'est installé dans Windows.",
                "Télécharger", "Quitter");

            if (!yes)
            {
                await AskAsync(
                    "Outils manquants",
                    "Sans ADB, le programme ne peut pas communiquer avec les terminaux. Vous pouvez aussi placer vous-même " +
                    "adb.exe, fastboot.exe, AdbWinApi.dll et AdbWinUsbApi.dll (Android SDK Platform-Tools de Google) dans le dossier :",
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
                        "Téléchargement impossible",
                        $"Le téléchargement de {Core.ToolsManager.DisplayName(component)} a échoué : {ex.Message}",
                        null,
                        "Vérifiez la connexion Internet. Vous pourrez réessayer depuis Paramètres ▸ Outils tiers.",
                        "Continuer", null);
                }
            }

            ProgBar.IsIndeterminate = true;
            return allOk;
        }
    }
}