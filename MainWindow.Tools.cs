using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ADBToolsNew
{
    /// <summary>
    /// Écran « Outils tiers » (Paramètres ▸ Outils tiers) et vérification des mises à jour en arrière-plan.
    /// Toute la logique de téléchargement est dans Core\ToolsManager.cs.
    /// </summary>
    public partial class MainWindow
    {
        private bool _toolsBusy;

        

        private void BtnToolsClose_Click(object sender, RoutedEventArgs e)
        {
            if (_toolsBusy) return; // on ne ferme pas pendant un téléchargement
            ToolsOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnToolsOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(ToolsManager.ToolsFolder);
            OpenFolder(ToolsManager.ToolsFolder);
        }

        private void BtnToolsCheck_Click(object sender, RoutedEventArgs e) => _ = RefreshToolsListAsync(true);

        private async Task ShowToolsOverlayAsync()
        {
            ToolsOverlay.Visibility = Visibility.Visible;
            await RefreshToolsListAsync(true);
        }

        /// <summary>Reconstruit la liste des outils (versions installées et, si demandé, dernières versions en ligne).</summary>
        private async Task RefreshToolsListAsync(bool checkOnline)
        {
            if (_toolsBusy) return;
            SetToolsBusy(true, checkOnline ? Loc.T("Vérification des versions disponibles…") : "");
            try
            {
                List<ToolStatus> statuses;
                try
                {
                    statuses = await ToolsManager.GetStatusesAsync(checkOnline);
                    SetToolsStatusText(checkOnline ? Loc.F("Vérifié à {0:HH:mm}.", DateTime.Now) : "");
                }
                catch (Exception ex)
                {
                    statuses = await ToolsManager.GetStatusesAsync(false);
                    SetToolsStatusText(Loc.T("Impossible de vérifier les versions en ligne : ") + ex.Message);
                }

                ToolsList.Children.Clear();
                SettingsToolsList.Children.Clear();
                foreach (var s in statuses)
                {
                    ToolsList.Children.Add(BuildToolRow(s));
                    SettingsToolsList.Children.Add(BuildToolRow(s));
                }
            }
            finally
            {
                SetToolsBusy(false, null);
            }
        }

        private UIElement BuildToolRow(ToolStatus s)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(15, 12, 15, 12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            card.SetResourceReference(Border.BackgroundProperty, "Surface2");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(MakeToolText(s.DisplayName, "Ink", 14, FontWeights.SemiBold));
            info.Children.Add(MakeToolText(s.Description, "Muted", 12, FontWeights.Normal));
            info.Children.Add(MakeToolText(s.SourceLabel + " · " + s.License, "Muted", 11, FontWeights.Normal, new Thickness(0, 4, 0, 0)));

            string state;
            string stateBrush;
            if (!s.Installed) { state = "Non installé"; stateBrush = "WarnFg"; }
            else if (s.UpdateAvailable) { state = $"Installé : v{s.InstalledVersion} — mise à jour disponible : v{s.LatestVersion}"; stateBrush = "InfoFg"; }
            else if (string.IsNullOrEmpty(s.InstalledVersion))
            {
                // Outil présent mais version illisible (ex. ancienne version de développement de FFmpeg)
                state = "Installé (version inconnue)" + (string.IsNullOrEmpty(s.LatestVersion) ? "" : $" — dernière version : v{s.LatestVersion}");
                stateBrush = "OkFg";
            }
            else if (!string.IsNullOrEmpty(s.LatestVersion)) { state = $"Installé : v{s.InstalledVersion} — à jour"; stateBrush = "OkFg"; }
            else { state = $"Installé : v{s.InstalledVersion}"; stateBrush = "OkFg"; }
            info.Children.Add(MakeToolText(state, stateBrush, 12, FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));
            grid.Children.Add(info);

            var button = new Button
            {
                Content = !s.Installed ? Loc.T("Télécharger") : (s.UpdateAvailable ? Loc.T("Mettre à jour") : Loc.T("Réinstaller")),
                Height = 32,
                Padding = new Thickness(14, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(15, 0, 0, 0),
                Tag = s.Component
            };
            // Orange (action principale) seulement quand il y a vraiment quelque chose à faire.
            button.Style = (Style)FindResource(!s.Installed || s.UpdateAvailable ? "PrimaryButtonStyle" : "OutlineButtonStyle");
            button.Click += async (sender, e) => await InstallToolAsync((ToolComponent)((Button)sender).Tag);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);

            card.Child = grid;
            return card;
        }

        private static TextBlock MakeToolText(string text, string brushKey, double size, FontWeight weight, Thickness? margin = null)
        {
            var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
            if (margin.HasValue) tb.Margin = margin.Value;
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return tb;
        }

        private void SetToolsBusy(bool busy, string message)
        {
            _toolsBusy = busy;
            BtnToolsCheck.IsEnabled = !busy;
            BtnToolsClose.IsEnabled = !busy;
            BtnSettingsToolsCheck.IsEnabled = !busy;
            foreach (var list in new[] { ToolsList, SettingsToolsList })
                foreach (var b in list.Children.OfType<Border>().Select(c => c.Child).OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()))
                    b.IsEnabled = !busy;
            if (message != null) SetToolsStatusText(message);
            foreach (var bar in new[] { ToolsProgress, SettingsToolsProgress })
            {
                bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                bar.IsIndeterminate = busy;
            }
        }

        private void SetToolsStatusText(string text)
        {
            TxtToolsStatus.Text = text;
            TxtSettingsToolsStatus.Text = text;
        }

        /// <summary>Télécharge / met à jour un outil depuis l'écran Outils tiers. Retourne vrai si c'est réussi.</summary>
        private async Task<bool> InstallToolAsync(ToolComponent component)
        {
            if (_toolsBusy) return false;
            if (component == ToolComponent.Scrcpy && _isRecording)
            {
                await ShowCustomDialogAsync(Loc.T("⏹️ Enregistrement en cours"), Loc.T("Arrêtez l'enregistrement vidéo avant de mettre à jour scrcpy."), "OK", null, false);
                return false;
            }

            SetToolsBusy(true, Loc.F("{0} : préparation…", ToolsManager.DisplayName(component)));
            var progress = new Progress<ToolProgress>(p =>
            {
                SetToolsStatusText(p.Message);
                foreach (var bar in new[] { ToolsProgress, SettingsToolsProgress })
                {
                    if (p.Percent < 0) bar.IsIndeterminate = true;
                    else { bar.IsIndeterminate = false; bar.Value = p.Percent; }
                }
            });

            bool ok = false;
            try
            {
                string version = await ToolsManager.InstallAsync(component, progress);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ {1} {2} installé dans le dossier Tools.", DateTime.Now, ToolsManager.DisplayName(component), version));
                ok = true;
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Installation de {1} impossible : {2}", DateTime.Now, ToolsManager.DisplayName(component), ex.Message));
                await ShowCustomDialogAsync(Loc.T("❌ Téléchargement impossible"), ex.Message, "OK", null, false);
            }
            finally
            {
                SetToolsBusy(false, null);
            }

            await RefreshToolsListAsync(true); // met à jour la fenêtre et la page Paramètres
            return ok;
        }

        /// <summary>Au démarrage : vérifie les mises à jour (au plus une fois par jour), sans rien bloquer.</summary>
        private async Task CheckToolUpdatesInBackgroundAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5)); // laisse le démarrage se terminer
                var updates = await ToolsManager.CheckForUpdatesAsync();
                foreach (var u in updates)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔔 Mise à jour disponible : {1} v{2} → v{3} (Paramètres ▸ Outils tiers).", DateTime.Now, u.DisplayName, u.InstalledVersion, u.LatestVersion));
                }
            }
            catch
            {
                // Pas de connexion, site indisponible... : la vérification est un bonus, jamais une erreur.
            }
        }

        /// <summary>Propose de télécharger un outil manquant au moment où on en a besoin (ex. FFmpeg pour le filigrane).</summary>
        private async Task<bool> OfferToInstallToolAsync(ToolComponent component, string reason)
        {
            bool yes = await ShowCustomDialogAsync(Loc.F("🧰 {0} n'est pas installé", ToolsManager.DisplayName(component)),
                reason + Loc.T("\n\nLe télécharger maintenant depuis sa source officielle ?"),
                Loc.T("Télécharger"), Loc.T("Plus tard"), false);
            if (!yes) return false;
            ToolsOverlay.Visibility = Visibility.Visible;
            await RefreshToolsListAsync(false);
            bool ok = await InstallToolAsync(component);
            if (ok) ToolsOverlay.Visibility = Visibility.Collapsed;
            return ok;
        }
    }
}
