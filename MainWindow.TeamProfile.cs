using ADBToolsNew.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ADBToolsNew
{
    /// <summary>
    /// Paramètres ▸ Profil d'équipe : export / import d'un fichier .adbprofil (voir <see cref="TeamProfile"/>).
    /// Un .adbprofil glissé sur ADBToolsNew.exe (ou passé en argument) est proposé à l'import au démarrage.
    /// </summary>
    public partial class MainWindow
    {
        private void UpdateTeamProfileUi()
        {
            var s = AppSettings.Current;
            ChkTeamTemplates.IsEnabled = ReportTemplates.HasUserTemplate(ReportKind.Sav) || ReportTemplates.HasUserTemplate(ReportKind.Wifi);
            if (!ChkTeamTemplates.IsEnabled) ChkTeamTemplates.IsChecked = false;
            ChkTeamUpdateShare.IsEnabled = !string.IsNullOrWhiteSpace(s.UpdateShareFolder);
            if (!ChkTeamUpdateShare.IsEnabled) ChkTeamUpdateShare.IsChecked = false;
            ChkTeamReportsFolder.IsEnabled = !string.IsNullOrWhiteSpace(s.ReportsFolder);
            if (!ChkTeamReportsFolder.IsEnabled) ChkTeamReportsFolder.IsChecked = false;

            bool applied = !string.IsNullOrWhiteSpace(s.TeamProfileName);
            TeamProfileAppliedCard.Visibility = applied ? Visibility.Visible : Visibility.Collapsed;
            if (applied)
                TxtTeamProfileApplied.Text = Loc.F("Profil appliqué sur ce poste : « {0} »", s.TeamProfileName) +
                    (s.TeamProfileImported.HasValue ? $", le {s.TeamProfileImported.Value:dd/MM/yyyy à HH:mm}." : ".");
        }

        private async void BtnTeamExport_Click(object sender, RoutedEventArgs e)
        {
            var opt = new TeamProfileExportOptions
            {
                Header = ChkTeamHeader.IsChecked == true,
                Templates = ChkTeamTemplates.IsChecked == true,
                Library = ChkTeamLibrary.IsChecked == true,
                UpdateShare = ChkTeamUpdateShare.IsChecked == true,
                ReportsFolder = ChkTeamReportsFolder.IsChecked == true
            };
            if (!opt.Header && !opt.Templates && !opt.Library && !opt.UpdateShare && !opt.ReportsFolder)
            {
                await ShowCustomDialogAsync(Loc.T("Profil d'équipe"), Loc.T("Cochez au moins un élément à inclure dans le profil."), "OK", null, false);
                return;
            }

            string suggested = string.IsNullOrWhiteSpace(AppSettings.Current.ReportCompany) ? "Mon équipe" : AppSettings.Current.ReportCompany.Split('\n')[0].Trim();
            bool ok = await ShowCustomDialogAsync(Loc.T("👥 Nom du profil"), Loc.T("Nom affiché sur les postes qui importeront ce profil :"), Loc.T("Continuer"), Loc.T("Annuler"), false, requiresInput: true, defaultInput: suggested);
            if (!ok) return;
            opt.Name = string.IsNullOrWhiteSpace(TxtDialogInput.Text) ? suggested : TxtDialogInput.Text.Trim();

            var dlg = new SaveFileDialog
            {
                Title = Loc.T("Exporter le profil d'équipe"),
                FileName = Loc.T("Profil_") + FileNameHelper.Sanitize(opt.Name) + TeamProfile.Extension,
                DefaultExt = TeamProfile.Extension,
                Filter = TeamProfile.FileFilter
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                TeamProfile.Export(dlg.FileName, opt, opt.Library ? LibraryToJson() : null, App.VersionText);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 👥 Profil d'équipe « {1} » exporté : {2}", DateTime.Now, opt.Name, dlg.FileName));
                ShowInExplorer(dlg.FileName);
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("❌ Export impossible"), ex.Message, "OK", null, true);
            }
        }

        private async void BtnTeamImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = Loc.T("Importer un profil d'équipe"), Filter = TeamProfile.FileFilter };
            if (dlg.ShowDialog() != true) return;
            await ImportTeamProfileAsync(dlg.FileName);
        }

        /// <summary>Au démarrage : un .adbprofil passé en argument (glissé sur l'exe) est proposé à l'import.</summary>
        private async Task ImportTeamProfileFromCommandLineAsync()
        {
            string path = Environment.GetCommandLineArgs().Skip(1)
                .FirstOrDefault(a => a.EndsWith(TeamProfile.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            if (path == null) return;
            await Task.Delay(500); // laisse la fenêtre finir de s'afficher
            await ImportTeamProfileAsync(path);
        }

        private async Task ImportTeamProfileAsync(string path)
        {
            TeamProfilePackage pkg;
            try { pkg = TeamProfile.Read(path); }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("❌ Profil illisible"), ex.Message, "OK", null, true);
                return;
            }

            // Résumé de ce qui va changer, avant d'appliquer quoi que ce soit.
            var info = pkg.Info;
            var lines = new List<string>();
            if (info.ReportCompany != null || info.ReportContact != null)
                lines.Add("• En-tête des rapports : " + (string.IsNullOrWhiteSpace(info.ReportCompany) ? "(société vide)" : info.ReportCompany.Split('\n')[0].Trim()));
            if (pkg.Logo != null) lines.Add("• Logo des rapports");
            if (pkg.SavTemplate != null) lines.Add("• Modèle du rapport SAV");
            if (pkg.WifiTemplate != null) lines.Add("• Modèle du rapport Wi-Fi");
            if (pkg.LibraryJson != null) lines.Add("• Commandes de la bibliothèque ADB (ajoutées, rien n'est supprimé)");
            if (!string.IsNullOrWhiteSpace(info.UpdateShareFolder)) lines.Add("• Dossier de diffusion des mises à jour : " + info.UpdateShareFolder);
            if (!string.IsNullOrWhiteSpace(info.ReportsFolder)) lines.Add("• Dossier des rapports : " + info.ReportsFolder);
            if (lines.Count == 0)
            {
                await ShowCustomDialogAsync(Loc.T("Profil d'équipe"), Loc.T("Ce profil ne contient aucun réglage à appliquer."), "OK", null, false);
                return;
            }

            string who = string.IsNullOrWhiteSpace(info.CreatedWith) ? "" : $" ({info.CreatedWith}, {info.Created:dd/MM/yyyy})";
            bool ok = await ShowCustomDialogAsync(Loc.F("👥 Appliquer « {0} » ?", info.Name),
                Loc.F("Ce profil{0} contient :\n\n", who) + string.Join("\n", lines) +
                Loc.T("\n\nLes modèles personnels actuels éventuels sont mis de côté dans Templates\\_anciens."),
                Loc.T("Appliquer"), Loc.T("Annuler"), false);
            if (!ok) return;

            try
            {
                TeamProfile.ApplySettingsAndFiles(pkg);
                int added = pkg.LibraryJson != null ? MergeLibraryJson(pkg.LibraryJson) : 0;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 👥 Profil d'équipe « {1} » appliqué", DateTime.Now, info.Name) +
                              (pkg.LibraryJson != null ? Loc.F(" ({0} commande(s) ajoutée(s) à la bibliothèque).", added) : "."));
                UpdateReportTemplateButtons();
                UpdateTeamProfileUi();
                if (!string.IsNullOrWhiteSpace(info.UpdateShareFolder)) _ = CheckAppUpdateAsync(true);
                await ShowCustomDialogAsync(Loc.T("✅ Profil appliqué"), Loc.F("Le profil « {0} » est appliqué sur ce poste.", info.Name), "OK", null, false);
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("❌ Profil non appliqué"), Loc.T("Une partie du profil n'a pas pu être appliquée : ") + ex.Message, "OK", null, true);
            }
        }
    }
}
