using ADBToolsNew.Core;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ADBToolsNew
{
    /// <summary>
    /// Page Paramètres (thème, outils tiers, modèles de rapport, à propos) et cadre de la fenêtre
    /// (coins arrondis + ombre en fenêtre normale). Remplace l'ancien groupe « Informations » du menu.
    /// </summary>
    public partial class MainWindow
    {
        private const int SettingsTabIndex = 9;
        private const double WindowCornerRadius = 10;
        private const double WindowShadowMargin = 12;
        private bool _updatingThemeUi;

        // --------------------------------------------------------------- Navigation

        private void NavSettings_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl != null) MainTabControl.SelectedIndex = SettingsTabIndex;
            UpdateNavButtonsUI(BtnNavSettings);
            if (TxtSettingsVersion != null) TxtSettingsVersion.Text = $"Version {App.VersionText}";
            ShowSettingsPanel(CurrentSettingsTag());
        }

        private string CurrentSettingsTag()
        {
            foreach (var rb in new[] { SetNavGeneral, SetNavTools, SetNavReports, SetNavUpdates, SetNavAbout })
                if (rb != null && rb.IsChecked == true) return rb.Tag as string;
            return "General";
        }

        private void SettingsNav_Checked(object sender, RoutedEventArgs e)
        {
            // Appelé aussi pendant InitializeComponent (IsChecked="True") : les panneaux n'existent pas encore.
            if (SettingsPanelGeneral == null) return;
            ShowSettingsPanel((sender as RadioButton)?.Tag as string);
        }

        private void ShowSettingsPanel(string tag)
        {
            if (SettingsPanelGeneral == null) return;
            SettingsPanelGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanelTools.Visibility = tag == "Tools" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanelReports.Visibility = tag == "Reports" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanelUpdates.Visibility = tag == "Updates" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanelAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

            if (tag == "General") UpdateThemeUi();
            if (tag == "Tools" && SettingsToolsList.Children.Count == 0) _ = RefreshToolsListAsync(false);
            if (tag == "Reports") UpdateReportTemplateStates();
            if (tag == "Updates") UpdateAppUpdateUi();
        }

        /// <summary>Ouvre directement une section des Paramètres (ex. depuis un message « Paramètres ▸ Outils tiers »).</summary>
        private void OpenSettings(string tag)
        {
            if (tag == "Tools") SetNavTools.IsChecked = true;
            else if (tag == "Reports") SetNavReports.IsChecked = true;
            else if (tag == "Updates") SetNavUpdates.IsChecked = true;
            else if (tag == "About") SetNavAbout.IsChecked = true;
            else SetNavGeneral.IsChecked = true;
            NavSettings_Click(BtnNavSettings, new RoutedEventArgs());
        }

        // --------------------------------------------------------------- Thème

        private void BtnThemeMode_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.CycleMode();
            UpdateThemeUi();
        }

        private void ThemeChoice_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingThemeUi || !(sender is RadioButton rb)) return;
            if (Enum.TryParse(rb.Tag as string, out AppThemeMode mode) && mode != ThemeManager.Mode)
            {
                ThemeManager.SetMode(mode);
                UpdateThemeUi();
            }
        }

        /// <summary>Met à jour le bouton « Thème : … » de la barre d'outils et les cartes de la page Paramètres.</summary>
        private void UpdateThemeUi()
        {
            _updatingThemeUi = true;
            try
            {
                var mode = ThemeManager.Mode;
                if (TxtThemeModeLabel != null) TxtThemeModeLabel.Text = "Thème : " + ThemeManager.ModeLabel(mode);
                if (TxtThemeModeIcon != null)
                    TxtThemeModeIcon.Text = mode == AppThemeMode.Light ? "" : mode == AppThemeMode.Dark ? "" : "";
                if (ThemeChoiceSystem != null) ThemeChoiceSystem.IsChecked = mode == AppThemeMode.System;
                if (ThemeChoiceLight != null) ThemeChoiceLight.IsChecked = mode == AppThemeMode.Light;
                if (ThemeChoiceDark != null) ThemeChoiceDark.IsChecked = mode == AppThemeMode.Dark;
            }
            finally
            {
                _updatingThemeUi = false;
            }
        }

        // --------------------------------------------------------------- Rapports Word

        /// <summary>Indique partout quel modèle sera utilisé : badge + fichier dans les Paramètres, et libellé
        /// des boutons « Modèle » à côté des exports Word.</summary>
        private void UpdateReportTemplateStates()
        {
            ShowTemplateState(ReportKind.Sav, BadgeSav, TxtBadgeSav, TxtReportSavState);
            ShowTemplateState(ReportKind.Wifi, BadgeWifi, TxtBadgeWifi, TxtReportWifiState);
            UpdateReportTemplateButtons();
        }

        private void UpdateReportTemplateButtons()
        {
            if (BtnReportTemplateSav != null) BtnReportTemplateSav.Content = "⚙️ Modèle : " + (ReportTemplates.HasUserTemplate(ReportKind.Sav) ? "personnel" : "fourni");
            if (BtnReportTemplateWifi != null) BtnReportTemplateWifi.Content = "⚙️ Modèle : " + (ReportTemplates.HasUserTemplate(ReportKind.Wifi) ? "personnel" : "fourni");
        }

        private static void ShowTemplateState(ReportKind kind, Border badge, TextBlock badgeText, TextBlock state)
        {
            if (badge == null || badgeText == null || state == null) return;
            bool user = ReportTemplates.HasUserTemplate(kind);
            badgeText.Text = user ? "Personnel" : "Fourni";
            badge.SetResourceReference(Border.BackgroundProperty, user ? "BrandSoft" : "Surface2");
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, user ? "BrandText" : "Muted");

            if (user)
            {
                var info = new System.IO.FileInfo(ReportTemplates.UserTemplatePath(kind));
                state.Text = $"Templates\\{info.Name} — modifié le {info.LastWriteTime:dd/MM/yyyy à HH:mm}";
            }
            else
            {
                state.Text = "Modèle neutre fourni avec ADB Tools, sans logo";
            }
            if (ReportTemplates.GetLogoPath() != null) state.Text += " · logo personnel ajouté";
        }

        private void BtnTemplatesOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            ReportTemplates.EnsureUserFolder();
            OpenFolder(ReportTemplates.UserFolder);
        }

        private void BtnTemplatesTags_Click(object sender, RoutedEventArgs e) => DocumentWindow.ShowTemplateTags(this);

        // --------------------------------------------------------------- Captures (barre d'outils)

        private void BtnCapturesMenu_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button)) return;
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.Items.Add(MakeItem("Ouvrir le dossier des captures", () => BtnOpenCaptureFolder_Click(button, new RoutedEventArgs())));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem("Vider les captures…", () => BtnClearCaptureFolder_Click(button, new RoutedEventArgs())));
            menu.IsOpen = true;
        }

        // --------------------------------------------------------------- À propos

        private void BtnShowLicense_Click(object sender, RoutedEventArgs e)
            => DocumentWindow.ShowTextFile(this, "Licence GPL v3", null, "Docs\\LICENSE.txt");

        private void BtnShowThirdParty_Click(object sender, RoutedEventArgs e)
            => DocumentWindow.ShowThirdParty(this);

        // --------------------------------------------------------------- Cadre de la fenêtre

        /// <summary>Coins arrondis + ombre en fenêtre normale ; plein cadre, sans marge ni arrondi, une fois agrandie.</summary>
        private void UpdateWindowFrame()
        {
            if (RootBorder == null || WindowShadow == null) return;
            bool maximized = WindowState == WindowState.Maximized;
            var margin = new Thickness(maximized ? 0 : WindowShadowMargin);
            var radius = new CornerRadius(maximized ? 0 : WindowCornerRadius);
            RootBorder.Margin = margin;
            RootBorder.CornerRadius = radius;
            RootBorder.BorderThickness = new Thickness(maximized ? 0 : 1);
            WindowShadow.Margin = margin;
            WindowShadow.CornerRadius = radius;
            WindowShadow.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
            UpdateRootClip();
        }

        private void RootBorder_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRootClip();

        /// <summary>Découpe le contenu suivant les coins arrondis (un Border n'arrondit que son propre fond, pas
        /// ce qu'il contient : sans cela, la barre de titre et le menu dépasseraient dans les coins).</summary>
        private void UpdateRootClip()
        {
            if (RootBorder == null || !(RootBorder.Child is FrameworkElement content)) return;
            double r = WindowState == WindowState.Maximized ? 0 : WindowCornerRadius - 1;
            double w = content.ActualWidth, h = content.ActualHeight;
            content.Clip = (w > 0 && h > 0) ? new RectangleGeometry(new Rect(0, 0, w, h), r, r) : null;
        }
    }
}
